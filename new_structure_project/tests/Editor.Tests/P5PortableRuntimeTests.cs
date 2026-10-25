using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Editor.Desktop;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5PortableRuntimeTests
{
    private string _folder = "";
    private string _package = "";
    private const string SourceHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "portable-runtime-" + Guid.NewGuid().ToString("N"));
        _package = Path.Combine(_folder, "candidate 中文 空格");
        Directory.CreateDirectory(_package);
        File.WriteAllBytes(Path.Combine(_package, "Editor.Desktop.exe"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(_package, "build-evidence.json"), JsonSerializer.Serialize(new { SourceManifestSha256 = SourceHash }));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_folder, recursive: true);

    [TestCase("missing-catalog")]
    [TestCase("version")]
    [TestCase("count")]
    [TestCase("payload-changed")]
    [TestCase("payload-missing")]
    [TestCase("extra-file")]
    [TestCase("source-mismatch")]
    [TestCase("parent-path")]
    [TestCase("catalog-itself")]
    [TestCase("duplicate")]
    [TestCase("malformed-hash")]
    public void InvalidPackageProducesFailedEvidenceBeforeAnyChildProcess(string scenario)
    {
        var files = new[] { Entry("Editor.Desktop.exe"), Entry("build-evidence.json") }.ToList();
        var version = 1;
        var count = files.Count;
        var source = SourceHash;
        switch (scenario)
        {
            case "version": version = 2; break;
            case "count": count++; break;
            case "payload-changed": File.WriteAllBytes(Path.Combine(_package, "Editor.Desktop.exe"), [4, 3, 2, 1]); break;
            case "payload-missing": File.Delete(Path.Combine(_package, "Editor.Desktop.exe")); break;
            case "extra-file": File.WriteAllText(Path.Combine(_package, "extra.txt"), "unexpected"); break;
            case "source-mismatch": source = new string('B', 64); break;
            case "parent-path": files[0] = files[0] with { Path = "../outside.exe" }; break;
            case "catalog-itself": files[0] = files[0] with { Path = "candidate-files.json" }; break;
            case "duplicate": files[1] = files[0]; break;
            case "malformed-hash": files[0] = files[0] with { Sha256 = new string('Z', 64) }; break;
        }
        if (scenario != "missing-catalog")
            File.WriteAllText(Path.Combine(_package, "candidate-files.json"), JsonSerializer.Serialize(new
            { EvidenceVersion = version, SourceManifestSha256 = source, FileCount = count, Files = files }));
        var before = Snapshot();
        var output = Path.Combine(_folder, "evidence 中文 空格");
        Assert.That(PortableRuntimeVerification.Run(output, _package), Is.EqualTo(1));
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "runtime-summary.json")));
        Assert.Multiple(() =>
        {
            Assert.That(report.RootElement.GetProperty("status").GetString(), Is.EqualTo("FAILED"));
            Assert.That(report.RootElement.GetProperty("error").GetString(), Is.Not.Empty);
            Assert.That(report.RootElement.GetProperty("commands").GetArrayLength(), Is.Zero);
            Assert.That(report.RootElement.GetProperty("reducedEnvironment").GetBoolean(), Is.False);
            Assert.That(report.RootElement.GetProperty("acceptance").GetProperty("cleanMachine").GetString(), Is.EqualTo("NOT_ASSERTED"));
            Assert.That(Snapshot(), Is.EqualTo(before));
        });
    }

    [TestCase("same")]
    [TestCase("nested")]
    [TestCase("ancestor")]
    public void PackageAndEvidenceMustBeDisjointWithoutWritingIntoThePackage(string scenario)
    {
        var before = Snapshot();
        var output = scenario switch
        {
            "same" => _package + Path.DirectorySeparatorChar,
            "nested" => Path.Combine(_package, "evidence"),
            _ => _folder
        };
        Assert.Throws<IOException>(() => PortableRuntimeVerification.Run(output, _package));
        Assert.That(Snapshot(), Is.EqualTo(before));
        Assert.That(File.Exists(Path.Combine(output, "runtime-summary.json")), Is.False);
    }

    [Test]
    public void ExistingEvidenceIsNeverOverwritten()
    {
        var output = Path.Combine(_folder, "previous-evidence");
        Directory.CreateDirectory(output);
        var summary = Path.Combine(output, "runtime-summary.json");
        File.WriteAllText(summary, "previous evidence");
        var time = File.GetLastWriteTimeUtc(summary);
        Assert.Throws<IOException>(() => PortableRuntimeVerification.Run(output, _package));
        Assert.That(File.ReadAllText(summary), Is.EqualTo("previous evidence"));
        Assert.That(File.GetLastWriteTimeUtc(summary), Is.EqualTo(time));
    }

    [Test]
    public void RealProbeProducesEveryProjectAndExportConsumedByThePortableVerifier()
    {
        // Run the shipping probe in a fresh process, then use the verifier's actual input
        // list. A schema upgrade must not leave the producer and portable driver disagreeing.
        var probe = Path.Combine(_folder, "actual probe 中文 空格");
        var host = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", "dotnet.exe"));
        Assert.That(File.Exists(host), Is.True, "Use the runtime already hosting this test.");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(PortableRuntimeVerification).Assembly.Location);
        start.ArgumentList.Add("--probe");
        start.ArgumentList.Add(probe);
        var fontProfile = Path.Combine(_folder, "isolated probe fonts");
        Directory.CreateDirectory(fontProfile);
        start.Environment["PPG_INTERNAL_VERIFY_FONT_DIRECTORY"] = fontProfile;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("The shipping probe timed out.");
        }
        Task.WaitAll(stdout, stderr);
        Assert.That(process.ExitCode, Is.Zero, stdout.Result + stderr.Result);
        var projects = (string[])typeof(PortableRuntimeVerification)
            .GetField("Projects", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.That(projects, Has.Length.EqualTo(11));
        var native = Path.Combine(probe, $"internal-native-v{NativePackageStore.SchemaVersion}.package");
        Assert.That(projects, Does.Contain(Path.GetFileName(native)));
        using (var archive = ZipFile.OpenRead(native))
        using (var entry = archive.GetEntry("project.json")!.Open())
        using (var manifest = JsonDocument.Parse(entry))
            Assert.That(manifest.RootElement.GetProperty("schemaVersion").GetInt32(),
                Is.EqualTo(NativePackageStore.SchemaVersion));
        foreach (var name in projects)
        {
            var path = Path.Combine(probe, name);
            Assert.That(File.Exists(path), Is.True, "Portable input is missing: " + name);
            Assert.DoesNotThrow(() => ProjectReopenProbe.Verify(path,
                path + ".opaque.png", path + ".transparent.png"), name);
            var document = NativePackageStore.Read(path);
            foreach (var transparent in new[] { false, true })
            {
                var mode = transparent ? "transparent" : "opaque";
                var expected = File.ReadAllBytes(path + ".width1920." + mode + ".png");
                Assert.That(PngExporter.Render(document, transparent, TestImages.Decoder, 1920),
                    Is.EqualTo(expected), name + " at width 1920: " + mode);
            }
        }
        Assert.That(File.Exists(Path.Combine(probe, "self-created-image.png")), Is.False);
        Assert.That(File.Exists(Path.Combine(probe, "导出 PNG.png")), Is.True);
        Assert.That(Directory.EnumerateFileSystemEntries(fontProfile), Is.Empty);
    }

    private CatalogEntry Entry(string name)
    {
        var file = Path.Combine(_package, name);
        return new(name, new FileInfo(file).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    }

    private string[] Snapshot() => Directory.EnumerateFiles(_package, "*", SearchOption.AllDirectories).Order()
        .Select(file => file + ":" + File.GetLastWriteTimeUtc(file).Ticks + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();

    private sealed record CatalogEntry(string Path, long Length, string Sha256);
}
