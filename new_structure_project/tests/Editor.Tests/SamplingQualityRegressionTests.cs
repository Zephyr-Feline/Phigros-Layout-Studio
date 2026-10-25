using Avalonia.Controls;
using Avalonia.LogicalTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class SamplingQualityRegressionTests
{
    [TestCase(ImageFitMode.Stretch)]
    [TestCase(ImageFitMode.Cover)]
    [TestCase(ImageFitMode.Contain)]
    public void EveryFitChecksTheSampledCropRatherThanTheCompleteImage(ImageFitMode fit)
    {
        var document = Document(new RectD(250, 250, 500, 500), new RectD(0, 0, .1, .1), fit);
        var before = document.Copy();
        foreach (var transparent in new[] { false, true })
        {
            var warnings = ExportPreflight.Inspect(document, _ => false, 1000,
                _ => (1000, 1000), transparentBackground: transparent);
            var warning = warnings.Single(item => item.Kind == ExportWarningKind.LowResolutionArtwork);
            Assert.Multiple(() =>
            {
                Assert.That(warning.Message, Does.Contain("source 100×100 px"));
                Assert.That(warning.Message, Does.Contain("displayed 500×500 px"));
                Assert.That(UiText.Translate(warning.Message), Does.Contain("采样 100×100"));
                Assert.That(document.StateEquals(before), Is.True);
            });
        }
    }

    [TestCase(1000, 100, 1000, 1000, 1000, 100, false)]
    [TestCase(100, 1000, 1000, 1000, 100, 1000, false)]
    [TestCase(200, 100, 500, 500, 500, 250, true)]
    [TestCase(1000, 100, 500, 500, 500, 50, false)]
    public void ContainChecksTheFittedImageRatherThanTheLetterbox(
        int sourceWidth, int sourceHeight, int boxWidth, int boxHeight,
        int displayedWidth, int displayedHeight, bool upscaled)
    {
        var document = Document(new RectD(0, 0, boxWidth, boxHeight), new RectD(0, 0, 1, 1),
            ImageFitMode.Contain);
        var warnings = ExportPreflight.Inspect(document, _ => false, 1000,
            _ => (sourceWidth, sourceHeight));
        var quality = warnings.Where(item => item.Kind == ExportWarningKind.LowResolutionArtwork).ToArray();
        Assert.That(quality, Has.Length.EqualTo(upscaled ? 1 : 0));
        if (upscaled)
            Assert.That(quality.Single().Message,
                Does.Contain($"displayed {displayedWidth}×{displayedHeight} px"));
    }

    private static IEnumerable<TestCaseData> RasterCropCases()
    {
        yield return new(new RectD(.15, .15, .21, .21), 3, 2, 3, 2, false);
        yield return new(new RectD(.15, .15, .21, .21), 3, 2, 4, 2, true);
        yield return new(new RectD(.9, .875, .1, .125), 1, 1, 1, 1, false);
        yield return new(new RectD(.9, .875, .1, .125), 1, 1, 2, 1, true);
        yield return new(new RectD(.99999999995, .99999999995, 1e-10, 1e-10), 1, 1, 2, 2, true);
        yield return new(new RectD(.45, .45, 1e-6, 1e-6), 1, 1, 1, 1, false);
        yield return new(new RectD(.45, .45, 1e-6, 1e-6), 1, 1, 2, 2, true);
    }

    [TestCaseSource(nameof(RasterCropCases))]
    public void RasterCropRoundsOutwardsAndClampsLikeThePainter(RectD crop,
        int sampledWidth, int sampledHeight, int boxWidth, int boxHeight, bool upscaled)
    {
        var document = Document(new RectD(0, 0, boxWidth, boxHeight), crop, ImageFitMode.Stretch);
        var warnings = ExportPreflight.Inspect(document, _ => false, 1000, _ => (10, 8));
        var quality = warnings.Where(item => item.Kind == ExportWarningKind.LowResolutionArtwork).ToArray();
        Assert.That(quality, Has.Length.EqualTo(upscaled ? 1 : 0));
        if (upscaled)
            Assert.That(quality.Single().Message,
                Does.Contain($"source {sampledWidth}×{sampledHeight} px"));
    }

    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    public void LegacyFitRotationAndHiddenLayersKeepTheirExistingScope(
        bool slanted, bool rotated, bool hidden)
    {
        var document = Document(new RectD(250, 250, 500, 500), new RectD(.25, 0, .5, 1), null);
        var layer = document.Layers.Single();
        layer.Slanted = slanted;
        layer.RotationDegrees = rotated ? 45 : 0;
        layer.Visible = !hidden;
        var before = document.Copy();
        var warnings = ExportPreflight.Inspect(document, _ => false, 1000, _ => (1000, 100));
        Assert.Multiple(() =>
        {
            Assert.That(layer.ImageFit, Is.Null);
            Assert.That(layer.EffectiveImageFit,
                Is.EqualTo(slanted ? ImageFitMode.Cover : ImageFitMode.Stretch));
            Assert.That(warnings.Select(item => item.Kind), Is.EqualTo(new[]
            {
                hidden ? ExportWarningKind.HiddenLayer : ExportWarningKind.LowResolutionArtwork
            }));
            if (!hidden) Assert.That(warnings.Single().Message, Does.Contain("source 500×100 px"));
            Assert.That(document.StateEquals(before), Is.True);
        });
    }

    private static IEnumerable<TestCaseData> OutputScaleCases()
    {
        yield return new(200, null, false);
        yield return new(1000, null, true);
        yield return new(200, new PngExportRegion(0, 0, 500, 500), false);
        yield return new(300, new PngExportRegion(0, 0, 500, 500), true);
        // Height rounds from 1.5 to 2, but the image still uses the uniform 0.5 render scale.
        yield return new(500, new PngExportRegion(0, 0, 1000, 3), false);
    }

    [TestCaseSource(nameof(OutputScaleCases))]
    public void SamplingQualityUsesTheUniformWidthAndRegionRenderScale(
        int outputWidth, PngExportRegion? region, bool upscaled)
    {
        var document = Document(new RectD(0, 0, 100, 100), new RectD(0, 0, .25, .25),
            ImageFitMode.Stretch);
        var warnings = ExportPreflight.Inspect(document, _ => false, outputWidth,
            _ => (200, 200), outputRegion: region);
        Assert.That(warnings.Count(item => item.Kind == ExportWarningKind.LowResolutionArtwork),
            Is.EqualTo(upscaled ? 1 : 0));
    }

    [Test]
    public void QueuePolicyBlocksSampleUpscaleAndReleasesAfterWidthOrPolicyChange()
    {
        AvaloniaTestHost.EnsureStarted();
        var document = Document(new RectD(0, 0, 250, 250), new RectD(0, 0, .1, .1),
            ImageFitMode.Cover,
            TestImages.EncodePng(500, 500, TestImages.SyntheticBgra(500, 500)));
        var before = document.Copy();
        var parent = ParentFolder();
        var dialog = new PngExportQueueDialog(document, [], () => Task.FromResult<string?>(parent),
            ExportQualityPolicy.BlockLowResolution,
            new ExportPresetLibrary(Path.Combine(parent, "presets.json")));
        try
        {
            UiTestActions.ClickNamed(dialog, "ChoosePngQueueDirectory");
            var list = Control<ListBox>(dialog, "PngQueueTaskList");
            var policy = Control<CheckBox>(dialog, "PngQueueBlockLowResolution");
            var start = Control<Button>(dialog, "StartPngQueueExport");
            Assert.That(start.IsEnabled, Is.False);
            Assert.That(list.Items.Cast<object>().Single().ToString(), Does.Contain("放大提示 1"));
            Assert.That(Control<TextBlock>(dialog, "PngQueueResolution").Text,
                Does.Contain("采样 50×50"));
            policy.IsChecked = false;
            Assert.That(start.IsEnabled, Is.True);
            policy.IsChecked = true;
            Assert.That(start.IsEnabled, Is.False);
            Control<TextBox>(dialog, "PngQueueWidth").Text = "200";
            UiTestActions.ClickNamed(dialog, "AddPngQueueTask");
            Assert.That(start.IsEnabled, Is.False, "the old large task remains blocked");
            list.SelectedIndex = 0;
            UiTestActions.ClickNamed(dialog, "RemovePngQueueTask");
            Assert.Multiple(() =>
            {
                Assert.That(list.ItemCount, Is.EqualTo(1));
                Assert.That(list.Items.Cast<object>().Single().ToString(), Does.Not.Contain("放大提示"));
                Assert.That(start.IsEnabled, Is.True);
                Assert.That(policy.IsChecked, Is.True);
                Assert.That(document.StateEquals(before), Is.True);
            });
        }
        finally { dialog.Close(); Directory.Delete(parent, recursive: true); }
    }

    [TestCase(ImageFitMode.Stretch, false)]
    [TestCase(ImageFitMode.Stretch, true)]
    [TestCase(ImageFitMode.Cover, false)]
    [TestCase(ImageFitMode.Cover, true)]
    [TestCase(ImageFitMode.Contain, false)]
    [TestCase(ImageFitMode.Contain, true)]
    public void CliSampleUpscaleBlocksBeforeWritingAndPreservesSourceAndExistingTarget(
        ImageFitMode fit, bool destinationExists)
    {
        AvaloniaTestHost.EnsureStarted();
        var document = Document(new RectD(250, 250, 500, 500), new RectD(0, 0, .1, .1), fit,
            TestImages.EncodePng(1000, 1000, TestImages.SyntheticBgra(1000, 1000)));
        var parent = ParentFolder();
        var source = Path.Combine(parent, "source.ppgproj");
        var blocked = Path.Combine(parent, "blocked.png");
        byte[] existing = [80, 82, 69, 83, 69, 82, 86, 69];
        try
        {
            NativePackageStore.Save(source, document, "sampling-quality-test");
            var sourceBytes = File.ReadAllBytes(source);
            if (destinationExists) File.WriteAllBytes(blocked, existing);
            var error = new StringWriter();
            var code = RenderCommand.Run(["--render", source, blocked, "--block-low-resolution"],
                new StringWriter(), error, () => { });
            Assert.Multiple(() =>
            {
                Assert.That(code, Is.EqualTo(1));
                Assert.That(error.ToString(), Does.Contain("RENDER BLOCKED"));
                Assert.That(error.ToString(), Does.Contain("source 100×100 px"));
                Assert.That(File.Exists(blocked), Is.EqualTo(destinationExists));
                if (destinationExists) Assert.That(File.ReadAllBytes(blocked), Is.EqualTo(existing));
                Assert.That(File.ReadAllBytes(source), Is.EqualTo(sourceBytes));
            });

            var transparent = Path.Combine(parent, "transparent.png");
            Assert.That(RenderCommand.Run(["--render", source, transparent, "--transparent",
                "--block-low-resolution"], new StringWriter(), new StringWriter(), () => { }),
                Is.EqualTo(1), "transparent background does not exempt visible image layers");
            Assert.That(File.Exists(transparent), Is.False);

            var advisory = Path.Combine(parent, "advisory.png");
            var advisoryError = new StringWriter();
            Assert.That(RenderCommand.Run(["--render", source, advisory],
                new StringWriter(), advisoryError, () => { }), Is.Zero, advisoryError.ToString());
            Assert.That(advisoryError.ToString(), Does.Contain("source 100×100 px"));
            Assert.That(File.ReadAllBytes(advisory), Is.EqualTo(TestImages.RenderPng(document)));

            var small = Path.Combine(parent, "small.png");
            var smallError = new StringWriter();
            Assert.That(RenderCommand.Run(["--render", source, small, "--width", "200",
                "--block-low-resolution"], new StringWriter(), smallError, () => { }), Is.Zero,
                smallError.ToString());
            Assert.That(smallError.ToString(), Does.Not.Contain("LowResolutionArtwork"));
            Assert.That(File.ReadAllBytes(small), Is.EqualTo(TestImages.RenderPng(document, false, 200)));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(sourceBytes));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Test]
    public void CliContainLeavesLetterboxSpaceOutOfTheQualityCheck()
    {
        AvaloniaTestHost.EnsureStarted();
        var document = Document(new RectD(0, 0, 1000, 1000), new RectD(0, 0, 1, 1),
            ImageFitMode.Contain, TestImages.EncodePng(1000, 100, TestImages.SyntheticBgra(1000, 100)));
        var parent = ParentFolder();
        var source = Path.Combine(parent, "source.ppgproj");
        var destination = Path.Combine(parent, "contain.png");
        try
        {
            NativePackageStore.Save(source, document, "sampling-quality-test");
            var sourceBytes = File.ReadAllBytes(source);
            var error = new StringWriter();
            var code = RenderCommand.Run(["--render", source, destination, "--block-low-resolution"],
                new StringWriter(), error, () => { });
            Assert.That(code, Is.Zero, error.ToString());
            Assert.That(error.ToString(), Does.Not.Contain("LowResolutionArtwork"));
            Assert.That(File.ReadAllBytes(destination), Is.EqualTo(TestImages.RenderPng(document)));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(sourceBytes));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static EditorDocument Document(RectD bounds, RectD crop, ImageFitMode? fit,
        byte[]? imageBytes = null)
    {
        var asset = Guid.NewGuid();
        var document = new EditorDocument
        {
            CanvasWidth = 1000, CanvasHeight = 1000,
            Layers = [new Layer
            {
                Kind = LayerKind.Image, AssetId = asset, Bounds = bounds, ImageCrop = crop,
                ImageFit = fit
            }]
        };
        document.AddAsset(asset, imageBytes ?? [1]);
        return document;
    }

    private static T Control<T>(Control parent, string name) where T : Control =>
        parent.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static string ParentFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sampling-quality-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
