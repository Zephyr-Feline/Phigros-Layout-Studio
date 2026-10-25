using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Platform;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;

namespace Editor.Desktop;

/// <summary>Internal, synthetic font lifecycle evidence. Each stage requires a fresh process.</summary>
public static class FontRestartEvidence
{
    public const string Usage = "Usage: --font-evidence <prepare|reopen|missing|restore|restored-reopen> <evidence-directory>";
    private static readonly string[] Stages = ["prepare", "reopen", "missing", "restore", "restored-reopen"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private const string ManifestName = "font-fixture.json";
    private const string ProjectName = "local-font 中文.ppgproj";

    public static int Run(string[] args, TextWriter output, TextWriter error, Action initialize)
    {
        if (args.Length != 3 || args[0] != "--font-evidence" || !Stages.Contains(args[1]) || string.IsNullOrWhiteSpace(args[2]))
        { error.WriteLine(Usage); return 2; }
        var record = new StageRecord { Stage = args[1], ProcessId = Environment.ProcessId };
        string? reportPath = null;
        try
        {
            var root = Path.GetFullPath(args[2]);
            var candidate = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (root.TrimEnd(Path.DirectorySeparatorChar).Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(candidate + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Font evidence and candidate must be separate directories.");
            var stagePath = Path.Combine(root, "font-stage-" + record.Stage + ".json");
            if (File.Exists(stagePath)) throw new IOException("Existing font stage evidence must not be overwritten.");
            if (record.Stage == "prepare")
            {
                if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                    throw new IOException("Font fixture directory must be new or empty.");
                Directory.CreateDirectory(root); reportPath = stagePath;
            }
            initialize();
            record.DefaultFontDirectory = LocalFontLibrary.Default.DirectoryPath;
            var bundled = BundledFont();
            var expected = LocalFontRegistry.Inspect(bundled);
            record.FontReference = expected.Reference;
            record.ColdRegistry = LocalFontRegistry.IsMissing(expected.Reference);
            if (!record.ColdRegistry) throw new InvalidDataException("Font is already registered; this stage needs a fresh process.");
            var libraryPath = Path.Combine(root, "fixture-font-library");
            var project = Path.Combine(root, ProjectName);
            var cached = Path.Combine(libraryPath, expected.Sha256 + ".ttf");
            if (record.Stage == "prepare")
            {
                var source = Path.Combine(root, "bundled-fixture-source.ttf");
                NativeDocumentStore.WriteBytesAtomically(source, bundled);
                var library = new LocalFontLibrary(libraryPath);
                var font = library.Import(source);
                var document = Document(font.Reference);
                var fallback = document.Copy(); fallback.Layers[0].FontFamily = LocalFontRegistry.Fallback;
                if (PngExporter.Render(document).SequenceEqual(PngExporter.Render(fallback)))
                    throw new InvalidDataException("Font fixture did not change the glyph pixels.");
                NativePackageStore.Save(project, document, "p5-font-restart");
                var pngs = new List<PngCase>();
                foreach (var width in new int?[] { null, 1920 })
                foreach (var transparent in new[] { false, true })
                {
                    var name = $"font-{(width is null ? "native" : "width1920")}-{(transparent ? "transparent" : "opaque")}.png";
                    var path = Path.Combine(root, name);
                    NativeDocumentStore.WriteBytesAtomically(path, PngExporter.Render(document, transparent, outputWidth: width));
                    pngs.Add(new(name, Hash(path), transparent, width));
                }
                File.Delete(source);
                Write(Path.Combine(root, ManifestName), new Fixture(1, expected.Sha256, expected.Reference,
                    Hash(project), File.GetLastWriteTimeUtc(project), pngs.ToArray()));
                record.SourceRemoved = !File.Exists(source);
                record.ProjectUnchanged = true;
                record.CachedFontSha256 = Hash(cached);
                record.Status = "PASS";
            }
            else
            {
                var fixture = ReadFixture(root, expected);
                reportPath = stagePath;
                if (record.Stage == "missing") File.Delete(cached); // Only our hash-named synthetic cache entry.
                var library = new LocalFontLibrary(libraryPath);
                record.InitialLibraryWarnings = library.Warnings.ToArray();
                if (record.Stage == "restore")
                {
                    var source = Path.Combine(root, "bundled-fixture-restore.ttf");
                    NativeDocumentStore.WriteBytesAtomically(source, bundled);
                    library.Import(source);
                    File.Delete(source);
                }
                record.LibraryWarnings = library.Warnings.ToArray();
                var missing = record.Stage == "missing";
                if (LocalFontRegistry.IsMissing(expected.Reference) != missing || library.Fonts.Count != (missing ? 0 : 1))
                    throw new InvalidDataException("Font registration after library reload is incorrect.");
                if (!missing && record.Stage != "restore" && library.Warnings.Count != 0)
                    throw new InvalidDataException("Cached font reload reported a warning.");
                var session = new EditSession(new EditorDocument());
                var workflow = new NativeProjectWorkflow(session, NativeImaging.ValidateImages, "p5-font-restart");
                workflow.Open(project);
                var document = session.Document;
                if (workflow.IsDirty || session.UndoCount != 0 || session.RedoCount != 0 || document.Assets.Count != 0 ||
                    document.Layers.Count != 1 || document.Layers[0].FontFamily != expected.Reference)
                    throw new InvalidDataException("Native reopen changed the font reference or clean state.");
                var warnings = ExportPreflight.Inspect(document, LocalFontRegistry.IsMissing);
                record.MissingFontWarnings = warnings.Count(item => item.Kind == ExportWarningKind.MissingLocalFont);
                if (record.MissingFontWarnings != (missing ? 1 : 0)) throw new InvalidDataException("Missing-font preflight is incorrect.");
                foreach (var png in fixture.Pngs)
                {
                    var actual = PngExporter.Render(document, png.Transparent, NativeImaging.Decoder, png.Width);
                    var baseline = File.ReadAllBytes(Path.Combine(root, png.Name));
                    if (missing)
                    {
                        var fallback = document.Copy(); fallback.Layers[0].FontFamily = LocalFontRegistry.Fallback;
                        if (!actual.SequenceEqual(PngExporter.Render(fallback, png.Transparent, NativeImaging.Decoder, png.Width)) ||
                            actual.SequenceEqual(baseline)) throw new InvalidDataException("Missing font did not use the unchanged-reference fallback.");
                        NativeDocumentStore.WriteBytesAtomically(Path.Combine(root, "missing-" + png.Name), actual);
                    }
                    else if (!actual.SequenceEqual(baseline)) throw new InvalidDataException("Restarted/restored font changed its PNG pixels.");
                    record.PngChecks++;
                }
                record.SourceRemoved = !File.Exists(Path.Combine(root, "bundled-fixture-source.ttf")) &&
                    !File.Exists(Path.Combine(root, "bundled-fixture-restore.ttf"));
                record.ProjectUnchanged = Hash(project) == fixture.ProjectSha256 && File.GetLastWriteTimeUtc(project) == fixture.ProjectLastWriteUtc;
                if (!record.ProjectUnchanged || !record.SourceRemoved) throw new IOException("Font workflow changed its project or retained its import source.");
                if (!missing) record.CachedFontSha256 = Hash(cached);
                record.Status = "PASS";
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { record.Error = exception.Message; error.WriteLine("FONT EVIDENCE FAILED: " + exception.Message); }
        finally
        {
            record.FinishedUtc = DateTime.UtcNow;
            if (reportPath is not null) Write(reportPath, record);
        }
        output.WriteLine($"FONT {record.Stage} {record.Status}: fresh registry {record.ColdRegistry}; {record.PngChecks} PNG checks.");
        return record.Status == "PASS" ? 0 : 1;
    }

    private static Fixture ReadFixture(string root, LocalFontInfo expected)
    {
        var path = Path.Combine(root, ManifestName);
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Synthetic font fixture manifest is missing or too large.");
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), Json);
        var expectedNames = new[] { "font-native-opaque.png", "font-native-transparent.png", "font-width1920-opaque.png", "font-width1920-transparent.png" };
        if (fixture is null || fixture.EvidenceVersion != 1 || fixture.FontSha256 != expected.Sha256 || fixture.FontReference != expected.Reference ||
            fixture.Pngs is null || fixture.Pngs.Length != 4 || !fixture.Pngs.Select(png => png.Name).SequenceEqual(expectedNames))
            throw new InvalidDataException("Synthetic font fixture identity is invalid.");
        for (var i = 0; i < fixture.Pngs.Length; i++)
        {
            var png = fixture.Pngs[i];
            if (png.Transparent != (i % 2 == 1) || png.Width != (i < 2 ? null : (int?)1920) || Hash(Path.Combine(root, png.Name)) != png.Sha256)
                throw new InvalidDataException("Font baseline PNG integrity mismatch.");
        }
        var project = Path.Combine(root, ProjectName);
        if (Hash(project) != fixture.ProjectSha256 || File.GetLastWriteTimeUtc(project) != fixture.ProjectLastWriteUtc)
            throw new InvalidDataException("Font fixture project integrity mismatch.");
        return fixture;
    }

    private static byte[] BundledFont()
    {
        using var input = AssetLoader.Open(new Uri("avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf"));
        using var memory = new MemoryStream(); input.CopyTo(memory); return memory.ToArray();
    }
    private static EditorDocument Document(string font) => new()
    {
        CanvasWidth = 800, CanvasHeight = 600, BackgroundArgb = 0xFF263C48,
        Layers = [new Layer { Kind = LayerKind.Text, Name = "Synthetic local-font text", Text = "WWW iii 012345\nRestart / fallback",
            FontFamily = font, FontSize = 70, Bounds = new(20, 40, 760, 500), ColorArgb = 0xFFFFFF00 }]
    };
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static void Write<T>(string path, T value) => NativeDocumentStore.WriteBytesAtomically(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)));
    private sealed record PngCase(string Name, string Sha256, bool Transparent, int? Width);
    private sealed record Fixture(int EvidenceVersion, string FontSha256, string FontReference, string ProjectSha256, DateTime ProjectLastWriteUtc, PngCase[] Pngs);
    private sealed class StageRecord
    {
        public string Stage { get; init; } = "";
        public string Status { get; set; } = "FAILED";
        public int ProcessId { get; init; }
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public DateTime FinishedUtc { get; set; }
        public string DefaultFontDirectory { get; set; } = "";
        public string FontReference { get; set; } = "";
        public bool ColdRegistry { get; set; }
        public bool SourceRemoved { get; set; }
        public bool ProjectUnchanged { get; set; }
        public string? CachedFontSha256 { get; set; }
        public int PngChecks { get; set; }
        public int MissingFontWarnings { get; set; }
        public string[] LibraryWarnings { get; set; } = [];
        public string[] InitialLibraryWarnings { get; set; } = [];
        public string? Error { get; set; }
    }
}
