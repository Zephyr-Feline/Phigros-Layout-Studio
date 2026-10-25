using Avalonia.Platform;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5FontRestartTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        AvaloniaTestHost.EnsureStarted();
        _root = Directory.CreateTempSubdirectory("p5-font-restart-").FullName;
    }

    [TearDown] public void TearDown() => Directory.Delete(_root, recursive: true);

    [TestCase(false)]
    [TestCase(true)]
    public void ReimportRepairsADeletedOrCorruptCachedFontEvenWhileItsFaceIsRegistered(bool corrupt)
    {
        var source = SourceFont();
        var bytes = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var library = new LocalFontLibrary(Path.Combine(_root, "library"));
        var info = library.Import(source);
        var cached = Directory.GetFiles(library.DirectoryPath).Single();
        if (corrupt) File.WriteAllBytes(cached, [1, 2, 3]);
        else File.Delete(cached);
        Assert.That(library.Import(source), Is.EqualTo(info));
        Assert.That(File.Exists(cached), Is.True, "reimport must restore persistence, not just the process-local registration");
        Assert.That(File.ReadAllBytes(cached), Is.EqualTo(bytes));
        Assert.That(library.Fonts, Has.Count.EqualTo(1));
        Assert.That(new LocalFontLibrary(library.DirectoryPath).Warnings, Is.Empty);
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(time));
    }

    [Test]
    public void ReimportOfAnIntactCachedFontPreservesItsBytesAndTimestamp()
    {
        var source = SourceFont();
        var library = new LocalFontLibrary(Path.Combine(_root, "library"));
        var info = library.Import(source);
        var cached = Directory.GetFiles(library.DirectoryPath).Single();
        var time = File.GetLastWriteTimeUtc(cached); var bytes = File.ReadAllBytes(cached);
        Assert.That(library.Import(source), Is.EqualTo(info));
        Assert.That(File.ReadAllBytes(cached), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(cached), Is.EqualTo(time));
    }

    [Test]
    public void ReimportClearsTheWarningForTheCacheFileItSuccessfullyRepairs()
    {
        var source = SourceFont();
        var imported = new LocalFontLibrary(Path.Combine(_root, "library"));
        var font = imported.Import(source);
        var cached = Directory.GetFiles(imported.DirectoryPath).Single();
        File.WriteAllBytes(cached, [1, 2, 3]);
        var reloaded = new LocalFontLibrary(imported.DirectoryPath);
        Assert.That(reloaded.Fonts, Is.Empty);
        Assert.That(reloaded.Warnings, Has.Count.EqualTo(1));
        Assert.That(reloaded.Import(source), Is.EqualTo(font));
        Assert.That(reloaded.Warnings, Is.Empty, "a repaired cache must not keep claiming that its font failed to load");
    }

    [TestCase(".TTF")]
    [TestCase(".TtF")]
    public void RepairClearsCaseVariantCacheWarningAndKeepsTheOtherBrokenFontWarning(string extension)
    {
        var source = SourceFont();
        var bytes = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var directory = Path.Combine(_root, "library"); Directory.CreateDirectory(directory);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var cached = Path.Combine(directory, hash + extension);
        var other = Path.Combine(directory, new string('0', 64) + ".otf");
        File.WriteAllBytes(cached, [1, 2, 3]);
        File.WriteAllBytes(other, [4, 5, 6]);
        var library = new LocalFontLibrary(directory);
        Assert.That(library.Warnings, Has.Count.EqualTo(2));
        var font = library.Import(source);
        Assert.That(font.Sha256, Is.EqualTo(hash));
        Assert.That(library.Warnings, Has.Count.EqualTo(1), "only the repaired file's warning should clear, regardless of extension casing");
        Assert.That(library.Warnings.Single(), Does.StartWith(Path.GetFileName(other) + ": "));
        Assert.That(File.ReadAllBytes(cached), Is.EqualTo(bytes));
        Assert.That(File.ReadAllBytes(other), Is.EqualTo(new byte[] { 4, 5, 6 }));
        Assert.That(library.Fonts, Has.Count.EqualTo(1));
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(time));
    }

    [Test]
    public void AnUnreadableCacheStopsReimportWithoutChangingItsSourceOrMemoryRecord()
    {
        var source = SourceFont();
        var library = new LocalFontLibrary(Path.Combine(_root, "library"));
        var info = library.Import(source);
        var cached = Directory.GetFiles(library.DirectoryPath).Single();
        var before = File.ReadAllBytes(cached); var time = File.GetLastWriteTimeUtc(cached);
        using (var locked = new FileStream(cached, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => library.Import(source));
        Assert.That(library.Fonts.Single(), Is.EqualTo(info));
        Assert.That(File.ReadAllBytes(cached), Is.EqualTo(before));
        Assert.That(File.GetLastWriteTimeUtc(cached), Is.EqualTo(time));
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(before));
    }

    [Test]
    public void FreshProcessesReloadFallbackRestoreAndReloadAgainWithoutEmbeddingOrChangingTheProject()
    {
        var phases = new[] { "prepare", "reopen", "missing", "restore", "restored-reopen" };
        var processes = new HashSet<int>();
        var checks = 0;
        var project = Path.Combine(_root, "fixture", "local-font 中文.ppgproj");
        byte[]? bytes = null; DateTime time = default;
        foreach (var phase in phases)
        {
            Assert.That(Invoke(phase).Code, Is.Zero);
            using var report = Stage(phase);
            var record = report.RootElement;
            Assert.That(record.GetProperty("status").GetString(), Is.EqualTo("PASS"));
            Assert.That(record.GetProperty("coldRegistry").GetBoolean(), Is.True);
            Assert.That(record.GetProperty("sourceRemoved").GetBoolean(), Is.True);
            Assert.That(record.GetProperty("projectUnchanged").GetBoolean(), Is.True);
            Assert.That(record.GetProperty("defaultFontDirectory").GetString(), Is.EqualTo(Path.Combine(_root, "isolated-default-fonts")));
            Assert.That(processes.Add(record.GetProperty("processId").GetInt32()), Is.True);
            Assert.That(record.GetProperty("missingFontWarnings").GetInt32(), Is.EqualTo(phase == "missing" ? 1 : 0));
            checks += record.GetProperty("pngChecks").GetInt32();
            if (bytes is null) { bytes = File.ReadAllBytes(project); time = File.GetLastWriteTimeUtc(project); }
            Assert.That(File.ReadAllBytes(project), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(project), Is.EqualTo(time));
        }
        Assert.That(checks, Is.EqualTo(16));
        Assert.That(Editor.Formats.NativePackageStore.Read(project).Assets, Is.Empty);
        Assert.That(Directory.EnumerateFiles(Path.Combine(_root, "isolated-default-fonts")), Is.Empty);
    }

    [TestCase("project")]
    [TestCase("baseline")]
    [TestCase("cache")]
    public void AChangedFixtureOrCorruptFontCannotProduceRestartPass(string changed)
    {
        Assert.That(Invoke("prepare").Code, Is.Zero);
        var fixture = Path.Combine(_root, "fixture");
        var project = Path.Combine(fixture, "local-font 中文.ppgproj");
        var changedPath = changed switch
        {
            "project" => project,
            "baseline" => Path.Combine(fixture, "font-native-opaque.png"),
            _ => Directory.GetFiles(Path.Combine(fixture, "fixture-font-library")).Single()
        };
        File.WriteAllBytes(changedPath, [3, 2, 1]);
        var bytes = File.ReadAllBytes(project); var time = File.GetLastWriteTimeUtc(project);
        var result = Invoke("reopen");
        Assert.That(result.Code, Is.EqualTo(1), result.Log);
        Assert.That(result.Log, Does.Contain("FAILED"));
        Assert.That(File.ReadAllBytes(project), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(project), Is.EqualTo(time));
        if (changed == "cache")
        {
            Assert.That(Invoke("restore").Code, Is.Zero);
            using var restored = Stage("restore");
            Assert.That(restored.RootElement.GetProperty("initialLibraryWarnings").GetArrayLength(), Is.EqualTo(1));
            Assert.That(restored.RootElement.GetProperty("libraryWarnings").GetArrayLength(), Is.Zero);
            Assert.That(Invoke("restored-reopen").Code, Is.Zero);
        }
    }

    [Test]
    public void RepeatedPreparationKeepsEarlierEvidenceAndInputsIntact()
    {
        Assert.That(Invoke("prepare").Code, Is.Zero);
        var fixture = Path.Combine(_root, "fixture");
        string[] Snapshot() => Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories).Order()
            .Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        var before = Snapshot();
        Assert.That(Invoke("prepare").Code, Is.EqualTo(1));
        Assert.That(Snapshot(), Is.EqualTo(before));
    }

    [TestCase("--font-evidence")]
    [TestCase("--font-evidence|prepare")]
    [TestCase("--font-evidence|unknown|output")]
    [TestCase("--font-evidence|reopen| ")]
    [TestCase("--font-evidence|prepare|output|extra")]
    [TestCase("--wrong|prepare|output")]
    public void InvalidStageSyntaxDoesNotInitializeGraphics(string command)
    {
        var error = new StringWriter();
        Assert.That(FontRestartEvidence.Run(command.Split('|'), new StringWriter(), error,
            () => throw new AssertionException("Unexpected graphics initialization")), Is.EqualTo(2));
        Assert.That(error.ToString(), Does.Contain("Usage:"));
    }

    private JsonDocument Stage(string phase) => JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "fixture", "font-stage-" + phase + ".json")));

    private (int Code, string Log) Invoke(string phase)
    {
        // Reuse the runtime already hosting NUnit, plus the referenced desktop build copied by MSBuild.
        // No installation, package restore, environment-global mutation or optional local font.
        var host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        Assert.That(File.Exists(host), Is.True, "the existing test runtime host must be available");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(FontRestartEvidence).Assembly.Location);
        start.ArgumentList.Add("--font-evidence"); start.ArgumentList.Add(phase);
        start.ArgumentList.Add(Path.Combine(_root, "fixture"));
        var profile = Path.Combine(_root, "isolated-default-fonts"); Directory.CreateDirectory(profile);
        start.Environment["PPG_INTERNAL_VERIFY_FONT_DIRECTORY"] = profile;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        { process.Kill(entireProcessTree: true); process.WaitForExit(); Assert.Fail("Font evidence child timed out."); }
        Task.WaitAll(stdout, stderr);
        var log = stdout.Result + stderr.Result;
        TestContext.WriteLine(log);
        return (process.ExitCode, log);
    }

    private string SourceFont()
    {
        var path = Path.Combine(_root, "already-bundled.ttf");
        using var input = AssetLoader.Open(new Uri("avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf"));
        using var output = File.Create(path); input.CopyTo(output);
        return path;
    }
}
