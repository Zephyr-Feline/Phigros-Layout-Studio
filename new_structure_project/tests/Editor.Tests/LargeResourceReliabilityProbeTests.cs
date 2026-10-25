using System.Security.Cryptography;
using System.Text.Json;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class LargeResourceReliabilityProbeTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void ScaledValidPngSceneExercisesRealHistoryCachePackageAndAllOutputs(bool entryLimited,
        bool trailingSeparator)
    {
        var directory = NewDirectory();
        var settings = SmallOptions();
        if (entryLimited)
        {
            settings = settings with { CacheEntryLimit = 1 };
            Directory.CreateDirectory(directory); // An explicitly empty directory is also allowed.
        }
        var result = LargeResourceReliabilityProbe.Run(
            trailingSeparator ? directory + Path.DirectorySeparatorChar : directory, settings);
        using var report = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(directory, LargeResourceReliabilityProbe.ReportFileName)));
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("PASS"));
            Assert.That(result.ConfigurationKind, Is.EqualTo("SCALED_TEST"),
                "small ordinary tests must never claim the production memory/export boundaries");
            Assert.That(result.Scenario, Is.EqualTo("large-resources"));
            Assert.That(report.RootElement.GetProperty("status").GetString(), Is.EqualTo("PASS"));
            Assert.That(report.RootElement.GetProperty("configurationKind").GetString(), Is.EqualTo("SCALED_TEST"));
            Assert.That(result.DistinctImagePayloads, Is.EqualTo(3));
            Assert.That(result.DecodedImagePopulationBytes, Is.EqualTo(3 * 32 * 32 * 4));
            Assert.That(result.DecodedImagePopulationBytes, Is.GreaterThan(settings.CachePixelBudget));
            Assert.That(result.EncodedAssetBytes, Is.GreaterThan(0));
            Assert.That(result.LayerCount, Is.EqualTo(20));
            Assert.That(result.CanvasWidth, Is.EqualTo(640));
            Assert.That(result.CanvasHeight, Is.EqualTo(360));
            Assert.That(result.History!.RetainedUndoStatesWalked, Is.EqualTo(4));
            Assert.That(result.History.RetainedRedoStatesWalked, Is.EqualTo(4));
            Assert.That(result.History.PayloadsShared, Is.True);
            Assert.That(result.History.CleanupUndoRestoredSameBlob, Is.True);
            Assert.That(result.PreviewCache!.EvictionObserved, Is.True);
            Assert.That(result.PreviewCache.CachedPixelsMatchFresh, Is.True);
            Assert.That(result.PreviewCache.ClearVerified, Is.True);
            Assert.That(result.PreviewCache.RetainedImagesAfterPopulation, Is.EqualTo(entryLimited ? 1 : 2));
            Assert.That(result.PreviewCache.RetainedPixelBytesAfterPopulation,
                Is.LessThanOrEqualTo(settings.CachePixelBudget));
            Assert.That(result.Package!.AssetEntries, Is.EqualTo(3));
            Assert.That(result.Package.RoundTripStateEqual, Is.True);
            Assert.That(result.Package.SourceUnchanged, Is.True);
            Assert.That(result.ReopenedOutputMatches, Is.True);
            Assert.That(result.OriginalDocumentUnchanged, Is.True);
            Assert.That(result.ReadOnlyOperationsPreservedSession, Is.True);
            Assert.That(result.Memory.Select(sample => sample.Phase),
                Does.Contain("start").And.Contain("complete"));
            Assert.That(result.Memory.All(sample => sample.WorkingSetBytes > 0 && sample.PrivateBytes > 0), Is.True);
            Assert.That(Directory.GetDirectories(directory), Is.Empty, "owned package staging is removed without recursion");
        });
        Assert.That(result.Outputs.Select(output => (output.Name, output.Width, output.Height, output.Transparent)),
            Is.EqualTo(new[] { ("canvas", 640, 360, false), ("pixel-limit", 400, 500, true),
                ("dimension-limit", 1024, 144, false) }));
        foreach (var output in result.Outputs)
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, output.FileName));
            var (width, height, _) = TestImages.DecodePng(bytes);
            Assert.Multiple(() =>
            {
                Assert.That((width, height), Is.EqualTo((output.Width, output.Height)));
                Assert.That(output.Pixels, Is.EqualTo((long)width * height));
                Assert.That(output.EncodedBytes, Is.EqualTo(bytes.LongLength));
                Assert.That(output.Sha256, Is.EqualTo(Convert.ToHexString(SHA256.HashData(bytes))));
            });
        }
        var reopened = NativePackageStore.Read(Path.Combine(directory, result.Package!.FileName));
        Assert.That(reopened.Layers.Select(layer => layer.Kind).Distinct(),
            Is.EquivalentTo(new[] { LayerKind.Image, LayerKind.Shape, LayerKind.Text, LayerKind.Badge }));
        Assert.That(File.ReadAllBytes(Path.Combine(directory, result.PreviewCache!.CachedFileName)),
            Is.EqualTo(File.ReadAllBytes(Path.Combine(directory, result.PreviewCache.FreshFileName))));
        Assert.That(File.ReadAllBytes(Path.Combine(directory, "reopened-canvas.png")),
            Is.EqualTo(File.ReadAllBytes(Path.Combine(directory, "canvas.png"))));
    }

    [TestCase("canvas")]
    [TestCase("image-count")]
    [TestCase("image-size")]
    [TestCase("layer-count")]
    [TestCase("history")]
    [TestCase("preview")]
    [TestCase("cache-too-small")]
    [TestCase("cache-no-pressure")]
    [TestCase("cache-entry-limit")]
    [TestCase("no-outputs")]
    [TestCase("null-outputs")]
    [TestCase("null-output")]
    [TestCase("too-many-outputs")]
    [TestCase("duplicate-output")]
    [TestCase("unsafe-output")]
    [TestCase("reserved-output")]
    [TestCase("first-cropped")]
    [TestCase("first-scaled")]
    [TestCase("output-width")]
    [TestCase("output-height")]
    [TestCase("output-pixels")]
    [TestCase("output-region")]
    public void InvalidConfigurationFailsBeforeCreatingEvidenceOrOutputDirectory(string invalid)
    {
        var settings = SmallOptions();
        settings = invalid switch
        {
            "canvas" => settings with { CanvasWidth = 8000 },
            "image-count" => settings with { ImageCount = 1 },
            "image-size" => settings with { ImageSize = 4096 },
            "layer-count" => settings with { LayerCount = 5 },
            "history" => settings with { HistoryEdits = 4 },
            "preview" => settings with { PreviewWidth = 31 },
            "cache-too-small" => settings with { CachePixelBudget = 4095 },
            "cache-no-pressure" => settings with { CachePixelBudget = 12288 },
            "cache-entry-limit" => settings with { CacheEntryLimit = 0 },
            "no-outputs" => settings with { Outputs = [] },
            "null-outputs" => settings with { Outputs = null! },
            "null-output" => settings with { Outputs = [null!] },
            "too-many-outputs" => settings with { Outputs = Enumerable.Range(0, 9)
                .Select(index => new LargeResourceProbeOutput("output" + index, 640, false)).ToArray() },
            "duplicate-output" => settings with { Outputs = [new("canvas", 640, false), new("CANVAS", 640, true)] },
            "unsafe-output" => settings with { Outputs = [new("../escape", 640, false)] },
            "reserved-output" => settings with { Outputs = [new("reopened-canvas", 640, false)] },
            "first-cropped" => settings with { Outputs = [new("canvas", 640, false, new(0, 0, 320, 180))] },
            "first-scaled" => settings with { Outputs = [new("canvas", 320, false)] },
            "output-width" => settings with { Outputs = [new("canvas", 640, false), new("invalid", 16385, false)] },
            "output-height" => settings with { Outputs = [new("canvas", 640, false), new("invalid", 200, false, new(0, 0, 1, 360))] },
            "output-pixels" => settings with { Outputs = [new("canvas", 640, false), new("invalid", 16384, false)] },
            "output-region" => settings with { Outputs = [new("canvas", 640, false), new("invalid", 640, false, new(0, 0, 641, 360))] },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        var directory = NewDirectory();
        Assert.That(() => LargeResourceReliabilityProbe.Run(directory, settings), Throws.Exception);
        Assert.That(Directory.Exists(directory), Is.False,
            "configuration validation must precede directory creation and image generation");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NonemptyEvidenceDirectoryOrFileIsNeverOverwritten(bool pathIsFile)
    {
        var directory = NewDirectory();
        var marker = pathIsFile ? directory : Path.Combine(directory, LargeResourceReliabilityProbe.ReportFileName);
        if (!pathIsFile) Directory.CreateDirectory(directory);
        var bytes = new byte[] { 7, 2, 9, 4 };
        File.WriteAllBytes(marker, bytes);
        Assert.That(() => LargeResourceReliabilityProbe.Run(directory, SmallOptions()), Throws.TypeOf<IOException>());
        Assert.That(File.ReadAllBytes(marker), Is.EqualTo(bytes));
        if (!pathIsFile) Assert.That(Directory.GetFileSystemEntries(directory), Is.EqualTo(new[] { marker }));
    }

    [Test]
    public void PreviousCompletedEvidenceCannotBeReplacedByAnotherRun()
    {
        var directory = NewDirectory();
        LargeResourceReliabilityProbe.Run(directory, SmallOptions());
        var before = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!,
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.That(() => LargeResourceReliabilityProbe.Run(directory, SmallOptions()), Throws.TypeOf<IOException>());
        var after = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!,
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.That(after, Is.EquivalentTo(before));
    }

    private static LargeResourceProbeOptions SmallOptions() => new()
    {
        CanvasWidth = 640, CanvasHeight = 360, ImageCount = 3, ImageSize = 32,
        LayerCount = 20, CachePixelBudget = 8192, HistoryEntries = 4, HistoryEdits = 8,
        PreviewWidth = 160, PreviewHeight = 90,
        Outputs = Array.AsReadOnly(new[]
        {
            new LargeResourceProbeOutput("canvas", 640, false),
            new LargeResourceProbeOutput("pixel-limit", 400, true, new(0, 0, 288, 360)),
            new LargeResourceProbeOutput("dimension-limit", 1024, false, new(0, 0, 640, 90))
        })
    };

    private static string NewDirectory() => Path.Combine(TestContext.CurrentContext.WorkDirectory,
        "large-resource-" + Guid.NewGuid().ToString("N"));
}
