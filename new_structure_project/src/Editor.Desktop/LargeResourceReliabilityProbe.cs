using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;

namespace Editor.Desktop;

/// <summary>Transient probe configuration; never a project, preference or export preset.</summary>
public sealed record LargeResourceProbeOutput(string Name, int Width, bool Transparent,
    PngExportRegion? Region = null);

public sealed record LargeResourceProbeOptions
{
    public int CanvasWidth { get; init; } = 7680;
    public int CanvasHeight { get; init; } = 4320;
    public int ImageCount { get; init; } = 10;
    public int ImageSize { get; init; } = 2048;
    public int LayerCount { get; init; } = 200;
    public long CachePixelBudget { get; init; } = DecodedImageCache.DefaultPixelBudget;
    public int CacheEntryLimit { get; init; } = DecodedImageCache.DefaultEntryLimit;
    public int HistoryEntries { get; init; } = 64;
    public int HistoryEdits { get; init; } = 90;
    public int PreviewWidth { get; init; } = 640;
    public int PreviewHeight { get; init; } = 360;
    public IReadOnlyList<LargeResourceProbeOutput> Outputs { get; init; } =
        Array.AsReadOnly(new[]
        {
            new LargeResourceProbeOutput("canvas", 7680, false),
            new LargeResourceProbeOutput("pixel-limit", 8000, true, new(0, 0, 3456, 4320)),
            new LargeResourceProbeOutput("dimension-limit", 16384, false, new(0, 0, 7680, 1080))
        });
}

public sealed record LargeResourceMemorySnapshot(string Phase, long WorkingSetBytes,
    long PrivateBytes, long PeakWorkingSetLifetimeBytes, long ManagedHeapBytes,
    long TotalAllocatedManagedBytes);

public sealed record LargeResourceHistoryEvidence(int HistoryLimit, int GestureEdits,
    int RetainedUndoStatesWalked, int RetainedRedoStatesWalked, bool PayloadsShared,
    bool CleanupUndoRestoredSameBlob);

public sealed record LargeResourceCacheEvidence(long PixelBudget, int EntryLimit,
    int RetainedImagesAfterPopulation, long RetainedPixelBytesAfterPopulation,
    bool EvictionObserved, bool CachedPixelsMatchFresh, bool ClearVerified,
    string CachedFileName, string FreshFileName, string Scope);

public sealed record LargeResourcePackageEvidence(string FileName, string Sha256,
    int AssetEntries, bool RoundTripStateEqual, bool SourceUnchanged);

public sealed record LargeResourceOutputEvidence(string Name, string FileName,
    int Width, int Height, long Pixels, bool Transparent, PngExportRegion? CropRegion,
    string Sha256, long EncodedBytes, double DurationMs);

public sealed class LargeResourceProbeResult
{
    public string Scenario => "large-resources";
    public string Status { get; internal set; } = "RUNNING";
    public string ConfigurationKind { get; internal set; } = "SCALED_TEST";
    public string? Error { get; internal set; }
    public int CanvasWidth { get; internal set; }
    public int CanvasHeight { get; internal set; }
    public int LayerCount { get; internal set; }
    public int ImageCount { get; internal set; }
    public int ImageSize { get; internal set; }
    public int DistinctImagePayloads { get; internal set; }
    public long EncodedAssetBytes { get; internal set; }
    public long DecodedImagePopulationBytes { get; internal set; }
    public LargeResourceHistoryEvidence? History { get; internal set; }
    public LargeResourceCacheEvidence? PreviewCache { get; internal set; }
    public LargeResourcePackageEvidence? Package { get; internal set; }
    public List<LargeResourceOutputEvidence> Outputs { get; } = [];
    public bool ReopenedOutputMatches { get; internal set; }
    public bool OriginalDocumentUnchanged { get; internal set; }
    public bool ReadOnlyOperationsPreservedSession { get; internal set; }
    public List<LargeResourceMemorySnapshot> Memory { get; } = [];
    public string MemoryScope =>
        "Local measurements only, not pass/fail budgets. Peak working set is the process-lifetime " +
        "OS peak, including earlier scenarios in this process. Private bytes and managed heap " +
        "are phase snapshots, not peaks; no full GC is forced. Cache bytes estimate retained " +
        "BGRA pixels only. Encoded assets, codec temporaries, export bitmaps and other native " +
        "allocations are outside that cache budget. No physical FPS, GPU-memory or leak verdict.";
}

/// <summary>
/// Explicitly opt-in local reliability evidence. The caller initializes Avalonia and launches
/// the production configuration in its own process. Ordinary tests inject a small configuration.
/// </summary>
public static class LargeResourceReliabilityProbe
{
    public const string ReportFileName = "large-resource-evidence.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static LargeResourceProbeResult Run(string outputDirectory,
        LargeResourceProbeOptions? options = null)
    {
        var settings = ValidateOptions(options ?? new());
        var directory = PrepareDirectory(outputDirectory);
        var report = new LargeResourceProbeResult
        {
            ConfigurationKind = IsProduction(settings) ? "PRODUCTION" : "SCALED_TEST",
            CanvasWidth = settings.CanvasWidth,
            CanvasHeight = settings.CanvasHeight,
            LayerCount = settings.LayerCount,
            ImageCount = settings.ImageCount,
            ImageSize = settings.ImageSize,
            DecodedImagePopulationBytes = checked((long)settings.ImageCount * settings.ImageSize *
                settings.ImageSize * 4)
        };
        try
        {
            SampleMemory(report, "start");
            var original = CreateDocument(settings);
            var originalState = original.Copy();
            var originalBlobs = original.Assets.ToDictionary(pair => pair.Key, pair => pair.Value);
            report.DistinctImagePayloads = original.Assets.Values.Select(blob => blob.Sha256).Distinct().Count();
            report.EncodedAssetBytes = original.Assets.Values.Sum(blob => (long)blob.Length);
            Require(report.DistinctImagePayloads == settings.ImageCount, "Generated image contents are not distinct.");
            NativeImaging.ValidateImages(original);
            SampleMemory(report, "valid-images");

            var session = new EditSession(original, settings.HistoryEntries);
            report.History = ExerciseHistory(session, settings, originalBlobs);
            session.SetSelection([session.Document.Layers[0].Id, session.Document.Layers[1].Id]);
            SampleMemory(report, "history-and-cleanup");
            var guard = new SessionGuard(session);
            var snapshot = session.Document.Copy();
            var snapshotState = snapshot.Copy();
            var snapshotBlobs = snapshot.Assets.ToDictionary(pair => pair.Key, pair => pair.Value);
            report.PreviewCache = ExerciseCache(snapshot, directory, settings);
            guard.Verify(session);
            SampleMemory(report, "preview-cache-cleared");

            const string projectName = "fixture.ppgproj";
            var projectPath = Path.Combine(directory, projectName);
            SaveNewPackage(projectPath, snapshot, directory);
            var projectHash = HashFile(projectPath);
            var reopened = NativePackageStore.Read(projectPath);
            NativeImaging.ValidateImages(reopened);
            Require(reopened.StateEquals(snapshot), "Native save/reopen changed document state.");
            int assetEntries;
            using (var archive = ZipFile.OpenRead(projectPath))
                assetEntries = archive.Entries.Count(entry =>
                    entry.FullName.StartsWith("assets/", StringComparison.Ordinal));
            Require(assetEntries == report.DistinctImagePayloads,
                "Native package duplicated or lost an image payload.");
            guard.Verify(session);
            SampleMemory(report, "saved-and-reopened");

            foreach (var output in settings.Outputs)
            {
                report.Outputs.Add(RenderNew(snapshot, directory, output, output.Name + ".png"));
                guard.Verify(session);
                SampleMemory(report, "output-" + output.Name);
            }
            var first = settings.Outputs[0];
            var reopenedOutput = RenderNew(reopened, directory, first, "reopened-canvas.png");
            report.ReopenedOutputMatches = reopenedOutput.Sha256 == report.Outputs[0].Sha256;
            Require(report.ReopenedOutputMatches, "Reopened project rendered different PNG bytes.");
            report.Package = new(projectName, projectHash, assetEntries, true,
                HashFile(projectPath) == projectHash);
            Require(report.Package.SourceUnchanged, "Rendering changed the saved source package.");
            guard.Verify(session);
            report.ReadOnlyOperationsPreservedSession = true;
            report.OriginalDocumentUnchanged = original.StateEquals(originalState) &&
                original.Assets.All(pair => ReferenceEquals(pair.Value, originalBlobs[pair.Key])) &&
                snapshot.StateEquals(snapshotState) && snapshot.Assets.All(pair =>
                    ReferenceEquals(pair.Value, snapshotBlobs[pair.Key]));
            Require(report.OriginalDocumentUnchanged, "The original scene was changed by the probe.");
            SampleMemory(report, "complete");
            GC.KeepAlive(original);
            report.Status = "PASS";
            WriteReport(directory, report);
            return report;
        }
        catch (Exception error)
        {
            report.Status = "FAILED";
            report.Error = error.Message;
            // WriteNew never replaces an existing report, including one from an earlier failure.
            WriteReport(directory, report);
            throw;
        }
    }

    private static LargeResourceProbeOptions ValidateOptions(LargeResourceProbeOptions options)
    {
        var document = new EditorDocument
            { CanvasWidth = options.CanvasWidth, CanvasHeight = options.CanvasHeight };
        document.Validate();
        if (options.ImageCount is < 2 or > 32 || options.ImageSize is < 16 or > 2048 ||
            options.LayerCount < options.ImageCount + 3 || options.LayerCount > 200 ||
            options.CacheEntryLimit is < 1 or > DecodedImageCache.DefaultEntryLimit ||
            options.HistoryEntries is < 1 or > 128 || options.HistoryEdits <= options.HistoryEntries ||
            options.HistoryEdits > 256 || options.PreviewWidth is < 32 or > 1280 ||
            options.PreviewHeight is < 32 or > 720)
            throw new ArgumentException("The reliability probe configuration is outside its bounded range.");
        var oneImageBytes = checked((long)options.ImageSize * options.ImageSize * 4);
        if (options.CachePixelBudget < oneImageBytes ||
            options.CachePixelBudget >= oneImageBytes * options.ImageCount)
            throw new ArgumentException("Image population must exceed the cache budget, which must fit one image.");
        if (options.Outputs is null || options.Outputs.Count is < 1 or > 8)
            throw new ArgumentException("The probe requires one to eight output specifications.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "preview-cached", "preview-fresh", "reopened-canvas", "CON", "PRN", "AUX", "NUL" };
        for (var index = 1; index <= 9; index++) { reserved.Add("COM" + index); reserved.Add("LPT" + index); }
        foreach (var output in options.Outputs)
        {
            if (output is null || string.IsNullOrEmpty(output.Name) || output.Name.Length > 40 ||
                output.Name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_') ||
                reserved.Contains(output.Name) || !names.Add(output.Name))
                throw new ArgumentException("Output names must be unique, controlled ASCII names.");
            _ = PngExportSize.Create(document, output.Width, output.Region);
        }
        if (options.Outputs[0].Region is not null || options.Outputs[0].Width != options.CanvasWidth)
            throw new ArgumentException("The first output must use the complete canvas at its native width.");
        return options with { Outputs = Array.AsReadOnly(options.Outputs.ToArray()) };
    }

    private static bool IsProduction(LargeResourceProbeOptions options)
    {
        var production = new LargeResourceProbeOptions();
        return (options with { Outputs = production.Outputs }) == production &&
            options.Outputs.SequenceEqual(production.Outputs);
    }

    private static string PrepareDirectory(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (File.Exists(fullPath)) throw new IOException("The probe output path is already a file.");
        if (Directory.Exists(fullPath))
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateFileSystemEntries(fullPath).Any())
                throw new IOException("The probe requires a new or empty, ordinary output directory.");
        }
        else Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static EditorDocument CreateDocument(LargeResourceProbeOptions options)
    {
        var document = new EditorDocument
        {
            CanvasWidth = options.CanvasWidth,
            CanvasHeight = options.CanvasHeight,
            BackgroundArgb = 0xFF17232D
        };
        var assets = new Guid[options.ImageCount];
        for (var index = 0; index < assets.Length; index++)
        {
            assets[index] = Guid.NewGuid();
            document.AddAsset(assets[index], CreateImage(index, options.ImageSize));
        }
        var columns = Math.Min(10, options.LayerCount);
        var rows = (int)Math.Ceiling(options.LayerCount / (double)columns);
        var cellWidth = options.CanvasWidth / (double)columns;
        var cellHeight = options.CanvasHeight / (double)rows;
        for (var index = 0; index < options.LayerCount; index++)
        {
            var box = new RectD(index % columns * cellWidth + cellWidth * .08,
                index / columns * cellHeight + cellHeight * .08, cellWidth * .8, cellHeight * .65);
            Layer layer;
            var component = (index - assets.Length) % 4;
            if (index < assets.Length || component == 3)
                layer = new Layer
                {
                    Kind = LayerKind.Image, AssetId = assets[index % assets.Length],
                    Bounds = box, Slanted = index % 2 == 0,
                    ImageFit = index % 3 == 0 ? ImageFitMode.Contain : ImageFitMode.Cover,
                    ImageCrop = index % 3 == 0 ? new(.1, .1, .8, .8) : new(0, 0, 1, 1)
                };
            else if (component == 2)
                layer = EditorDocument.NewBadge(new PointD(box.X, box.Y), Math.Max(8, Math.Min(box.Width, box.Height)));
            else if (component == 0)
                layer = new Layer { Kind = LayerKind.Shape, Bounds = box, ColorArgb = 0xAA2070A0, Slanted = true };
            else
                layer = new Layer
                {
                    Kind = LayerKind.Text, Bounds = box, Text = $"资源 / Layer {index}",
                    FontSize = Math.Clamp(box.Height / 3, 8, 40), ColorArgb = 0xFFF0E8D8
                };
            layer.Name = $"Reliability {index}";
            layer.RotationDegrees = index % 11 == 0 ? 13 : 0;
            document.Layers.Add(layer);
        }
        document.FixedBackgroundAssetId = assets[1];
        document.Validate();
        return document;
    }

    private static byte[] CreateImage(int index, int dimension)
    {
        var pixels = new byte[checked(dimension * dimension * 4)];
        if (index == 0) new Random(714311).NextBytes(pixels); // One nontrivial encoded payload for history sharing.
        for (var y = 0; y < dimension; y++)
        for (var x = 0; x < dimension; x++)
        {
            var at = (y * dimension + x) * 4;
            if (index != 0)
            {
                pixels[at] = (byte)((x / 8 + index * 23) % 256);
                pixels[at + 1] = (byte)((y / 8 + index * 31) % 256);
                pixels[at + 2] = (byte)(((x + y) / 16 + index * 47) % 256);
            }
            pixels[at + 3] = 255;
        }
        using var bitmap = new DecodedRaster(dimension, dimension, pixels).ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    private static LargeResourceHistoryEvidence ExerciseHistory(EditSession session,
        LargeResourceProbeOptions options, IReadOnlyDictionary<Guid, AssetBlob> originalBlobs)
    {
        var unused = Guid.NewGuid();
        var shared = originalBlobs.Values.First();
        session.Document.AddAsset(unused, shared);
        var moving = session.Document.Layers.First(layer => layer.Kind == LayerKind.Shape);
        for (var index = 0; index < options.HistoryEdits; index++)
        {
            session.BeginGesture(moving.Id, includeGroup: false);
            session.MoveBy(index % 2 == 0 ? .125 : 0, index % 2 == 0 ? 0 : .125);
            Require(session.CommitGesture(), "A reliability gesture did not create history.");
            VerifySharedPayloads(session.Document, originalBlobs);
        }
        Require(session.UndoCount == options.HistoryEntries, "History was not bounded as configured.");
        var final = session.Document.Copy();
        var undo = 0;
        while (session.Undo()) { undo++; VerifySharedPayloads(session.Document, originalBlobs); }
        var redo = 0;
        while (session.Redo()) { redo++; VerifySharedPayloads(session.Document, originalBlobs); }
        Require(undo == options.HistoryEntries && redo == undo && session.Document.StateEquals(final),
            "The bounded undo/redo walk changed document contents.");
        var cleanup = session.CleanUnreferencedAssets();
        Require(cleanup.Count == 1 && !session.Document.Assets.ContainsKey(unused), "Cleanup did not remove only the unused alias.");
        Require(session.Undo() && ReferenceEquals(session.Document.Assets[unused], shared),
            "Cleanup undo did not restore the same immutable payload.");
        Require(session.Redo() && !session.Document.Assets.ContainsKey(unused), "Cleanup redo did not remove the unused alias.");
        VerifySharedPayloads(session.Document, originalBlobs);
        return new(options.HistoryEntries, options.HistoryEdits, undo, redo, true, true);
    }

    private static void VerifySharedPayloads(EditorDocument document, IReadOnlyDictionary<Guid, AssetBlob> blobs)
    {
        foreach (var (id, blob) in blobs)
            Require(document.Assets.TryGetValue(id, out var current) && ReferenceEquals(current, blob),
                "A document/history snapshot copied or replaced an immutable image payload.");
    }

    private static LargeResourceCacheEvidence ExerciseCache(EditorDocument document, string directory,
        LargeResourceProbeOptions options)
    {
        using var cache = new DecodedImageCache(options.CachePixelBudget, options.CacheEntryLimit);
        var blobs = document.Assets.Values.ToArray();
        for (var index = 0; index < blobs.Length; index++)
        {
            cache.Use(blobs[index], NativeImaging.Decoder, _ => { });
            VerifyCacheBudget(cache, options);
        }
        var retainedCount = cache.Count;
        var retainedBytes = cache.EstimatedPixelBytes;
        // Every unique, valid payload was successfully decoded with room for at least one
        // bitmap. Retaining fewer entries than that population proves actual eviction without
        // retaining a bitmap borrowed by Use beyond its callback.
        var evicted = retainedCount < blobs.Length;
        Require(evicted, "The valid-image population did not cause decoded bitmap eviction.");
        var cached = RenderPreview(document, options, cache);
        VerifyCacheBudget(cache, options);
        var fresh = RenderPreview(document, options, null);
        Require(cached.AsSpan().SequenceEqual(fresh), "Cached preview PNG differs from independent fresh painting.");
        const string cachedName = "preview-cached.png";
        const string freshName = "preview-fresh.png";
        WriteNewBytes(Path.Combine(directory, cachedName), cached);
        WriteNewBytes(Path.Combine(directory, freshName), fresh);
        cache.Clear();
        Require(cache.Count == 0 && cache.EstimatedPixelBytes == 0, "Clearing the decoded cache retained pixel entries.");
        return new(options.CachePixelBudget, options.CacheEntryLimit, retainedCount, retainedBytes,
            true, true, true, cachedName, freshName,
            "Actual valid PNG decode/eviction and cached/fresh preview equality. Estimated retained BGRA " +
            "pixels only; not total process memory, export allocations, physical display frames or canvas-detach acceptance.");
    }

    private static void VerifyCacheBudget(DecodedImageCache cache, LargeResourceProbeOptions options) =>
        Require(cache.Count <= options.CacheEntryLimit && cache.EstimatedPixelBytes <= options.CachePixelBudget,
            "Decoded preview cache exceeded its existing retention contract.");

    private static byte[] RenderPreview(EditorDocument document, LargeResourceProbeOptions options,
        DecodedImageCache? cache)
    {
        using var target = new RenderTargetBitmap(new(options.PreviewWidth, options.PreviewHeight), new(96, 96));
        var view = new ViewportTransform(document.CanvasWidth, document.CanvasHeight,
            options.PreviewWidth, options.PreviewHeight);
        using (var context = target.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(view.Scale, view.Scale) *
                                     Matrix.CreateTranslation(view.Origin.X, view.Origin.Y)))
            ScenePainter.Paint(context, document, NativeImaging.Decoder, imageCache: cache);
        using var stream = new MemoryStream();
        target.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    private static void SaveNewPackage(string path, EditorDocument document, string directory)
    {
        var staging = Path.Combine(directory, ".stage-" + Guid.NewGuid().ToString("N"));
        var fullStage = Path.GetFullPath(staging);
        Require(fullStage.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "The staging path escaped the owned output directory.");
        if (File.Exists(staging) || Directory.Exists(staging)) throw new IOException("The private staging path already exists.");
        Directory.CreateDirectory(staging);
        var stagedPackage = Path.Combine(staging, "payload.ppgproj");
        var saved = false;
        try
        {
            NativePackageStore.Save(stagedPackage, document, "large-resource-reliability");
            saved = true;
            using var input = new FileStream(stagedPackage, FileMode.Open, FileAccess.Read, FileShare.Read);
            AtomicFile.WriteNew(path, input.CopyTo);
        }
        finally
        {
            if (saved && File.Exists(stagedPackage)) File.Delete(stagedPackage);
            if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any())
                Directory.Delete(staging); // Never recursively delete an unexpected file.
        }
    }

    private static LargeResourceOutputEvidence RenderNew(EditorDocument document, string directory,
        LargeResourceProbeOutput output, string fileName)
    {
        var size = PngExportSize.Create(document, output.Width, output.Region);
        var watch = Stopwatch.StartNew();
        var png = PngExporter.Render(document, output.Transparent, NativeImaging.Decoder, output.Width, output.Region);
        Require(png.Length >= 24 && png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            png.AsSpan(12, 4).SequenceEqual("IHDR"u8) &&
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) == size.Width &&
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) == size.Height,
            "Rendered PNG dimensions disagree with the requested output.");
        var path = Path.Combine(directory, fileName);
        WriteNewBytes(path, png);
        var hash = Convert.ToHexString(SHA256.HashData(png));
        Require(HashFile(path) == hash, "PNG bytes changed while writing the artifact.");
        watch.Stop();
        return new(output.Name, fileName, size.Width, size.Height, (long)size.Width * size.Height,
            output.Transparent, output.Region, hash, png.LongLength, watch.Elapsed.TotalMilliseconds);
    }

    private static void WriteReport(string directory, LargeResourceProbeResult report) =>
        WriteNewBytes(Path.Combine(directory, ReportFileName), JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions));

    private static void WriteNewBytes(string path, byte[] bytes) =>
        AtomicFile.WriteNew(path, stream => stream.Write(bytes));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void SampleMemory(LargeResourceProbeResult report, string phase)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        report.Memory.Add(new(phase, process.WorkingSet64, process.PrivateMemorySize64,
            process.PeakWorkingSet64, GC.GetTotalMemory(forceFullCollection: false),
            GC.GetTotalAllocatedBytes(precise: false)));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class SessionGuard(EditSession session)
    {
        private readonly EditorDocument _document = session.Document.Copy();
        private readonly int _undo = session.UndoCount;
        private readonly int _redo = session.RedoCount;
        private readonly bool _dirty = session.IsDirty;
        private readonly Guid[] _selected = session.SelectedLayerIds.ToArray();

        public void Verify(EditSession current) => Require(current.Document.StateEquals(_document) &&
            current.UndoCount == _undo && current.RedoCount == _redo && current.IsDirty == _dirty &&
            current.SelectedLayerIds.SequenceEqual(_selected),
            "Read-only probe work changed the current session, history, selection or saved state.");
    }
}
