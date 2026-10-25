using System.Text.Json;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3FExportQualityPolicyTests
{
    private string _folder = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(),
            "r3f-quality-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Test]
    public async Task BlockPolicyStopsBeforeCreatingTheOutputDirectory()
    {
        var document = LowResolutionDocument();
        var directory = Path.Combine(_folder, "blocked");
        var rendered = false;
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PngExportQueue.ExecuteAsync(document,
                [new PngExportTask(1000, false)], directory,
                _ => { },
                (_, _, _) => { rendered = true; return [1]; },
                (_, _) => throw new AssertionException("No file should be written."),
                new ProtectedSourcePaths(), () => null,
                isLocalFontMissing: _ => false,
                readImageDimensions: ImageAssetValidator.ReadEncodedDimensions,
                qualityPolicy: ExportQualityPolicy.BlockLowResolution));
        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Message, Does.Contain("quality policy"));
        Assert.Multiple(() =>
        {
            Assert.That(rendered, Is.False);
            Assert.That(Directory.Exists(directory), Is.False);
        });
    }

    [Test]
    public async Task QueueManifestV2RecordsPolicyAndTaskWarnings()
    {
        var document = LowResolutionDocument();
        var directory = Path.Combine(_folder, "notify");
        var result = await PngExportQueue.ExecuteAsync(document,
            [new PngExportTask(1000, false)], directory,
            _ => { },
            (_, _, _) => TestImages.RenderPng(document, false, 1000),
            (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
            new ProtectedSourcePaths(), () => null,
            isLocalFontMissing: _ => false,
            readImageDimensions: ImageAssetValidator.ReadEncodedDimensions,
            qualityPolicy: ExportQualityPolicy.Notify).ConfigureAwait(false);
        Assert.That(result.Items.Single().Warnings.Any(warning =>
            warning.Kind == ExportWarningKind.LowResolutionArtwork), Is.True);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        var root = manifest.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("formatVersion").GetInt32(), Is.EqualTo(2));
            Assert.That(root.GetProperty("qualityPolicy").GetString(), Is.EqualTo("Notify"));
            Assert.That(root.GetProperty("tasks")[0].GetProperty("warnings").EnumerateArray()
                .Any(warning => warning.GetProperty("kind").GetString() == "LowResolutionArtwork"),
                Is.True);
        });
    }

    [Test]
    public async Task ProjectBatchManifestV2RecordsPerTaskWarnings()
    {
        var source = Path.Combine(_folder, "quality.ppgproj");
        NativePackageStore.Save(source, LowResolutionDocument(), "r3f-test");
        var outputParent = Path.Combine(_folder, "out");
        Directory.CreateDirectory(outputParent);
        var plan = new ProjectRenderPlan(outputParent,
            [source], [new PngExportTask(1000, false)]);
        var result = await ProjectRenderBatch.ExecuteAsync(plan,
            _ => { },
            (document, transparent, width) => TestImages.RenderPng(document, transparent, width),
            (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
            new ProtectedSourcePaths(), () => null, _ => false,
            readImageDimensions: ImageAssetValidator.ReadEncodedDimensions)
            .ConfigureAwait(false);
        Assert.That(result.Projects.Single().Tasks.Single().Warnings.Any(warning =>
            warning.Kind == ExportWarningKind.LowResolutionArtwork), Is.True);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        var root = manifest.RootElement;
        Assert.That(root.GetProperty("formatVersion").GetInt32(), Is.EqualTo(2));
        Assert.That(root.GetProperty("projects")[0].GetProperty("tasks")[0]
            .GetProperty("warnings").EnumerateArray()
            .Any(warning => warning.GetProperty("kind").GetString() == "LowResolutionArtwork"),
            Is.True);
    }

    private static EditorDocument LowResolutionDocument()
    {
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        var asset = Guid.NewGuid();
        document.AddAsset(asset, TestImages.EncodePng(400, 240,
            TestImages.SyntheticBgra(400, 240)));
        document.Layers.Add(new Layer
        {
            Kind = LayerKind.Image, Name = "低分辨率曲绘", AssetId = asset,
            Bounds = new RectD(100, 100, 800, 480)
        });
        document.Validate();
        return document;
    }
}
