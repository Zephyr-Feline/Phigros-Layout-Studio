using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Editor.Formats;

namespace Editor.Desktop;

/// <summary>Runs only the extracted candidate: no SDK, package restore, shell or graphical input.</summary>
public static class PortableRuntimeVerification
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private const string CatalogName = "candidate-files.json";
    private static readonly string[] Projects = [ProbeRunner.NativeProjectFileName, "tiff-project.ppgproj",
        "automatic-background.ppgproj", "song-card.ppgproj", "custom-layout.ppgproj", "arranged-groups.ppgproj",
        "imported-song-groups.ppgproj", "selected-song.ppgproj", "batch-3-songs.ppgproj", "batch-5-songs.ppgproj",
        "song-style.ppgproj"];

    public static int Run(string output, string? packageRoot = null)
    {
        var root = Path.GetFullPath(packageRoot ?? AppContext.BaseDirectory);
        output = Path.GetFullPath(output);
        if (Inside(root, output) || Inside(output, root)) throw new IOException("Package and output must be separate directories.");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Extracted package directory is missing.");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Runtime evidence output must be new or empty.");
        Directory.CreateDirectory(output);
        var report = new RuntimeReport { PackageRoot = root };
        var stamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var catalogPath = Path.Combine(root, CatalogName);
        DateTime? catalogTime = null;
        Catalog? catalog = null;
        try
        {
            if (!Environment.Is64BitProcess) throw new IOException("The win-x64 candidate requires a 64-bit process.");
            catalog = ReadCatalog(catalogPath);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in catalog.Files)
            {
                if (string.IsNullOrWhiteSpace(entry.Path) || Path.IsPathRooted(entry.Path) || entry.Length < 0 ||
                    entry.Path.Split('/', '\\').Any(part => part is "." or ".." or "") ||
                    !IsHash(entry.Sha256) || !names.Add(entry.Path.Replace('\\', '/'))) throw new InvalidDataException("Invalid or duplicate catalog entry.");
                var file = Path.GetFullPath(Path.Combine(root, entry.Path));
                if (!Inside(root, file) || file.Equals(catalogPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Catalog path leaves the package or references the catalog itself.");
                if (!File.Exists(file) || new FileInfo(file).Length != entry.Length || Hash(file) != entry.Sha256.ToUpperInvariant())
                    throw new InvalidDataException($"Candidate integrity mismatch: {entry.Path}");
                stamps[entry.Path] = File.GetLastWriteTimeUtc(file);
            }
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();
            if (files != catalog.FileCount + 1) throw new InvalidDataException("Candidate has extra or missing files.");
            using var build = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "build-evidence.json")));
            if (build.RootElement.GetProperty("SourceManifestSha256").GetString() != catalog.SourceManifestSha256)
                throw new InvalidDataException("Build and catalog source identities disagree.");
            var executable = Path.Combine(root, "Editor.Desktop.exe");
            if (!names.Contains("Editor.Desktop.exe")) throw new InvalidDataException("Candidate executable is missing from the catalog.");
            report.SourceManifestSha256 = catalog.SourceManifestSha256;
            report.CatalogSha256 = Hash(catalogPath); catalogTime = File.GetLastWriteTimeUtc(catalogPath);
            report.PackageFiles = files;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var system = Path.Combine(windows, "System32");
            var reducedPath = system + Path.PathSeparator + windows;
            report.DotnetOnReducedPath = new[] { system, windows }.Any(path => File.Exists(Path.Combine(path, "dotnet.exe")));
            if (report.DotnetOnReducedPath) throw new IOException("Reduced environment still contains a dotnet command.");
            report.ReducedEnvironment = true;
            var fontProfile = Path.Combine(output, "isolated-default-fonts");
            Directory.CreateDirectory(fontProfile);
            report.FontProfileDirectory = fontProfile;
            var probe = Path.Combine(output, "probe 中文 空格");
            Invoke(["--probe", probe], 0);
            using (var preview = JsonDocument.Parse(File.ReadAllText(Path.Combine(probe, "preview-cache-evidence.json"))))
            {
                var evidence = preview.RootElement;
                if (evidence.GetProperty("Status").GetString() != "PASS" || evidence.GetProperty("Scenes").GetArrayLength() != 3 ||
                    evidence.GetProperty("BoundaryExport").GetProperty("Width").GetInt32() != 7680 ||
                    evidence.GetProperty("BoundaryExport").GetProperty("Height").GetInt32() != 4320)
                    throw new InvalidDataException("Cache/8K evidence is incomplete.");
            }
            foreach (var name in Projects)
            {
                var project = Path.Combine(probe, name);
                var hash = Hash(project); var time = File.GetLastWriteTimeUtc(project);
                Invoke(["--verify-reopen", project, project + ".opaque.png", project + ".transparent.png"], 0);
                report.ReopenChecks++;
                foreach (var transparent in new[] { false, true })
                {
                    var mode = transparent ? "transparent" : "opaque";
                    var target = project + ".cli-" + mode + ".png";
                    var arguments = new List<string> { "--render", project, target };
                    if (transparent) arguments.Add("--transparent");
                    Invoke(arguments, 0);
                    SamePng(target, project + "." + mode + ".png");
                    target = project + ".cli-width1920-" + mode + ".png";
                    arguments = ["--render", project, target, "--width", "1920"];
                    if (transparent) arguments.Add("--transparent");
                    Invoke(arguments, 0);
                    SamePng(target, project + ".width1920." + mode + ".png");
                }
                if (Hash(project) != hash || File.GetLastWriteTimeUtc(project) != time)
                    throw new IOException($"Reopen/export changed its input: {name}");
            }
            var fontRoot = Path.Combine(output, "font restart 中文 空格");
            var fontProcesses = new HashSet<int>();
            foreach (var stage in new[] { "prepare", "reopen", "missing", "restore", "restored-reopen" })
            {
                Invoke(["--font-evidence", stage, fontRoot], 0);
                var stagePath = Path.Combine(fontRoot, "font-stage-" + stage + ".json");
                using var evidence = JsonDocument.Parse(File.ReadAllText(stagePath));
                var values = evidence.RootElement;
                if (values.GetProperty("status").GetString() != "PASS" || !values.GetProperty("coldRegistry").GetBoolean() ||
                    !values.GetProperty("projectUnchanged").GetBoolean() || !values.GetProperty("sourceRemoved").GetBoolean() ||
                    values.GetProperty("defaultFontDirectory").GetString() != fontProfile ||
                    !fontProcesses.Add(values.GetProperty("processId").GetInt32()) ||
                    values.GetProperty("missingFontWarnings").GetInt32() != (stage == "missing" ? 1 : 0))
                    throw new InvalidDataException("Fresh-process font lifecycle evidence is incomplete.");
                report.FontPngChecks += values.GetProperty("pngChecks").GetInt32();
                report.FontStages.Add(new { Stage = stage, EvidenceSha256 = Hash(stagePath) });
            }
            if (report.FontPngChecks != 16 || Directory.EnumerateFileSystemEntries(fontProfile).Any())
                throw new InvalidDataException("Font evidence altered its isolated default library or lacks PNG checks.");
            foreach (var command in new[] { "--render", "--unknown-command", "--compat-evidence", "--verify-portable", "--font-evidence" })
                Invoke([command], 2, 30);
            var protectedProject = Path.Combine(probe, "arranged-groups.ppgproj");
            var protectedHash = Hash(protectedProject);
            Invoke(["--render", protectedProject, protectedProject], 1, 30);
            if (Hash(protectedProject) != protectedHash) throw new IOException("CLI input protection failed.");
            var existing = protectedProject + ".cli-opaque.png";
            var existingHash = Hash(existing);
            Invoke(["--render", Path.Combine(probe, "missing.ppgproj"), existing], 1, 30);
            if (Hash(existing) != existingHash) throw new IOException("Failed render changed an existing output.");
            Invoke(["--render", protectedProject, existing, "--width", "0"], 2, 30);
            if (Hash(existing) != existingHash) throw new IOException("Invalid width changed an existing output.");
            Invoke(["--legacy", Path.Combine(output, "legacy probe")], 0);
            if (!File.Exists(Path.Combine(probe, ProbeRunner.NativeProjectFileName)) ||
                !File.Exists(Path.Combine(probe, "导出 PNG.png")) || File.Exists(Path.Combine(probe, "self-created-image.png")))
                throw new InvalidDataException("Source independence evidence is incomplete.");
            report.Status = "PASS";

            void SamePng(string actual, string expected)
            {
                if (Hash(actual) != Hash(expected)) throw new InvalidDataException($"CLI PNG differs: {actual}");
                report.CliPngChecks++;
            }
            void Invoke(IReadOnlyList<string> arguments, int expectedExit, int timeoutSeconds = 180)
            {
                var start = new ProcessStartInfo(executable) { WorkingDirectory = root,
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                start.Environment["PATH"] = reducedPath;
                start.Environment.Remove("DOTNET_ROOT"); start.Environment.Remove("DOTNET_ROOT_X64");
                start.Environment[LocalFontLibrary.VerificationDirectoryVariable] = fontProfile;
                var record = new CommandRecord { Arguments = arguments.ToArray(), ExpectedExitCode = expectedExit,
                    Log = $"runtime-command-{report.Commands.Count + 1:000}.log" };
                report.Commands.Add(record);
                var clock = Stopwatch.StartNew();
                var log = Path.Combine(output, record.Log);
                try
                {
                    using var process = Process.Start(start) ?? throw new IOException("Candidate process did not start.");
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutSeconds * 1000))
                    {
                        record.TimedOut = true; process.Kill(entireProcessTree: true); process.WaitForExit();
                    }
                    Task.WaitAll(stdout, stderr);
                    Write(log, stdout.Result + stderr.Result);
                    record.ExitCode = process.ExitCode;
                    if (record.TimedOut) throw new IOException("Candidate process timed out; its log was retained.");
                    if (record.ExitCode != expectedExit) throw new IOException($"Unexpected candidate exit {record.ExitCode}; expected {expectedExit}. See {log}");
                }
                finally
                {
                    record.DurationMs = clock.ElapsedMilliseconds;
                    if (File.Exists(log)) record.LogSha256 = Hash(log);
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { report.Status = "FAILED"; report.Error = error.Message; }
        finally
        {
            if (catalogTime is not null && catalog is not null)
            {
                try
                {
                    report.PackageUnchanged = File.Exists(catalogPath) && Hash(catalogPath) == report.CatalogSha256 &&
                        File.GetLastWriteTimeUtc(catalogPath) == catalogTime &&
                        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() == report.PackageFiles &&
                        catalog.Files.All(entry => File.Exists(Path.Combine(root, entry.Path)) &&
                            Hash(Path.Combine(root, entry.Path)) == entry.Sha256.ToUpperInvariant() &&
                            File.GetLastWriteTimeUtc(Path.Combine(root, entry.Path)) == stamps[entry.Path]);
                }
                catch (IOException) { report.PackageUnchanged = false; }
                catch (UnauthorizedAccessException) { report.PackageUnchanged = false; }
                if (report.PackageUnchanged != true) { report.Status = "FAILED"; report.Error = "Candidate files changed during verification."; }
            }
            report.FinishedUtc = DateTime.UtcNow;
            Write(Path.Combine(output, "runtime-summary.json"), JsonSerializer.Serialize(report, Json));
        }
        Console.WriteLine($"RUNTIME VERIFICATION {report.Status}: {report.ReopenChecks} reopens, {report.CliPngChecks} PNGs; {report.Commands.Count} processes.");
        Console.WriteLine($"FONT LIFECYCLE: {report.FontStages.Count} fresh stages, {report.FontPngChecks} PNG checks.");
        if (report.Error is not null) Console.Error.WriteLine(report.Error);
        Console.WriteLine("Evidence is for this host. Clean-machine and physical acceptance are not asserted.");
        return report.Status == "PASS" ? 0 : 1;
    }

    private static Catalog ReadCatalog(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 4L * 1024 * 1024) throw new InvalidDataException("Candidate catalog is missing or too large.");
        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (catalog is null || catalog.EvidenceVersion != 1 || !IsHash(catalog.SourceManifestSha256) ||
            catalog.Files is null || catalog.Files.Length is < 1 or > 2000 || catalog.FileCount != catalog.Files.Length)
            throw new InvalidDataException("Candidate catalog is invalid.");
        return catalog;
    }
    private static bool Inside(string parent, string child) =>
        child.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(
            parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static void Write(string path, string content) => NativeDocumentStore.WriteBytesAtomically(path, Encoding.UTF8.GetBytes(content));
    private sealed record CandidateFile(string Path, long Length, string Sha256);
    private sealed record Catalog(int EvidenceVersion, string SourceManifestSha256, int FileCount, CandidateFile[] Files);
    private sealed class CommandRecord
    {
        public string[] Arguments { get; init; } = [];
        public int ExpectedExitCode { get; init; }
        public int? ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public long DurationMs { get; set; }
        public string Log { get; init; } = "";
        public string? LogSha256 { get; set; }
    }
    private sealed class RuntimeReport
    {
        public string Status { get; set; } = "FAILED";
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public DateTime FinishedUtc { get; set; }
        public string OperatingSystem { get; } = RuntimeInformation.OSDescription;
        public string Framework { get; } = RuntimeInformation.FrameworkDescription;
        public bool Is64BitProcess { get; } = Environment.Is64BitProcess;
        public string PackageRoot { get; init; } = "";
        public string? SourceManifestSha256 { get; set; }
        public string? CatalogSha256 { get; set; }
        public int? PackageFiles { get; set; }
        public bool? PackageUnchanged { get; set; }
        public bool ReducedEnvironment { get; set; }
        public bool DotnetOnReducedPath { get; set; }
        public int ReopenChecks { get; set; }
        public int CliPngChecks { get; set; }
        public string? FontProfileDirectory { get; set; }
        public int FontPngChecks { get; set; }
        public List<object> FontStages { get; } = [];
        public List<CommandRecord> Commands { get; } = [];
        public string? Error { get; set; }
        public object Acceptance { get; } = new { CleanMachine = "NOT_ASSERTED", PhysicalWindows = "NOT_RUN",
            ChineseIme = "NOT_RUN", DpiAndMonitors = "NOT_RUN", P3Visual = "PENDING", Publication = "PENDING" };
    }
}
