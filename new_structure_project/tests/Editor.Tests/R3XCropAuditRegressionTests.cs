using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3XCropAuditRegressionTests
{
    [TestCase(true, false)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    public void QueueCropToggleRejectsInvalidProjectionWithoutLosingTasksOrState(
        bool enableCrop, bool exceedsPixelLimit)
    {
        AvaloniaTestHost.EnsureStarted();
        var document = enableCrop
            ? new EditorDocument { CanvasWidth = 2740, CanvasHeight = 1080 }
            : exceedsPixelLimit
                ? new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 }
                : new EditorDocument { CanvasWidth = 320, CanvasHeight = 4320 };
        var region = enableCrop
            ? exceedsPixelLimit
                ? new PngExportRegion(0, 0, 1000, 1000)
                : new PngExportRegion(100, 40, 100, 1000)
            : exceedsPixelLimit
                ? new PngExportRegion(0, 0, 200, 10)
                : new PngExportRegion(0, 0, 300, 100);
        var parent = ParentFolder();
        var dialog = new PngExportQueueDialog(document, [], () => Task.FromResult<string?>(parent),
            ExportQualityPolicy.BlockLowResolution,
            new ExportPresetLibrary(Path.Combine(parent, "presets.json")), region);
        try
        {
            var crop = Control<CheckBox>(dialog, "PngQueueCropToSelection");
            if (!enableCrop) crop.IsChecked = true;
            var addedWidth = enableCrop ? exceedsPixelLimit ? 10000 : 100
                : exceedsPixelLimit ? 16000 : 2000;
            Control<TextBox>(dialog, "PngQueueWidth").Text = addedWidth.ToString();
            UiTestActions.ClickNamed(dialog, "AddPngQueueTask");
            UiTestActions.ClickNamed(dialog, "ChoosePngQueueDirectory");
            Dispatcher.UIThread.RunJobs();
            var list = Control<ListBox>(dialog, "PngQueueTaskList");
            Assert.That(list.ItemCount, Is.EqualTo(2), "both tasks are valid before switching");
            list.SelectedIndex = 0;
            var selected = list.SelectedItem;
            var rows = list.Items.Cast<object>().ToArray();
            var count = Control<TextBlock>(dialog, "PngQueueTaskCount").Text;
            var directory = Control<TextBlock>(dialog, "PngQueueDirectory").Text;
            var before = document.Copy();
            Assert.That(Control<Button>(dialog, "StartPngQueueExport").IsEnabled, Is.True);

            crop.IsChecked = enableCrop;
            Dispatcher.UIThread.RunJobs();

            Assert.Multiple(() =>
            {
                Assert.That(crop.IsChecked, Is.EqualTo(!enableCrop), "reject the invalid mode change");
                Assert.That(list.Items.Cast<object>().ToArray(), Is.EqualTo(rows));
                Assert.That(list.SelectedItem, Is.SameAs(selected));
                Assert.That(Control<TextBlock>(dialog, "PngQueueTaskCount").Text, Is.EqualTo(count));
                Assert.That(Control<TextBlock>(dialog, "PngQueueDirectory").Text, Is.EqualTo(directory));
                Assert.That(Control<CheckBox>(dialog, "PngQueueBlockLowResolution").IsChecked, Is.True);
                Assert.That(Control<Button>(dialog, "StartPngQueueExport").IsEnabled, Is.True);
                Assert.That(Control<TextBlock>(dialog, "PngQueueError").Text, Is.Not.Null.And.Not.Empty);
                Assert.That(document.StateEquals(before), Is.True);
            });
        }
        finally
        {
            dialog.Close();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Test]
    public void QueueCropToggleRoundTripPreservesTasksSelectionAndPolicy()
    {
        AvaloniaTestHost.EnsureStarted();
        var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 };
        var region = new PngExportRegion(10, 20, 300, 200);
        var parent = ParentFolder();
        var dialog = new PngExportQueueDialog(document, [], () => Task.FromResult<string?>(parent),
            ExportQualityPolicy.BlockLowResolution,
            new ExportPresetLibrary(Path.Combine(parent, "presets.json")), region);
        try
        {
            Control<TextBox>(dialog, "PngQueueWidth").Text = "800";
            Control<ComboBox>(dialog, "PngQueueMode").SelectedIndex = 1;
            UiTestActions.ClickNamed(dialog, "AddPngQueueTask");
            var list = Control<ListBox>(dialog, "PngQueueTaskList");
            list.SelectedIndex = 0;
            var rows = list.Items.Cast<object>().Select(item => item.ToString()).ToArray();
            var crop = Control<CheckBox>(dialog, "PngQueueCropToSelection");
            crop.IsChecked = true;
            Assert.Multiple(() =>
            {
                Assert.That(crop.IsChecked, Is.True);
                Assert.That(list.ItemCount, Is.EqualTo(2));
                Assert.That(list.SelectedIndex, Is.Zero);
                Assert.That(list.Items.Cast<object>().First().ToString(), Does.Contain("640 × 427"));
                Assert.That(list.Items.Cast<object>().Last().ToString(), Does.Contain("800 × 533"));
            });
            crop.IsChecked = false;
            Assert.Multiple(() =>
            {
                Assert.That(crop.IsChecked, Is.False);
                Assert.That(list.Items.Cast<object>().Select(item => item.ToString()), Is.EqualTo(rows));
                Assert.That(list.SelectedIndex, Is.Zero);
                Assert.That(Control<CheckBox>(dialog, "PngQueueBlockLowResolution").IsChecked, Is.True);
                Assert.That(Control<TextBlock>(dialog, "PngQueueTaskCount").Text, Is.EqualTo("任务数：2"));
                Assert.That(Control<TextBlock>(dialog, "PngQueueError").Text, Is.Empty);
            });
        }
        finally
        {
            dialog.Close();
            Directory.Delete(parent, recursive: true);
        }
    }

    [TestCase(500, 500, true)]
    [TestCase(5000, 5000, false)]
    [TestCase(10000, 5000, false)]
    [TestCase(10000, 4999, true)]
    public void CroppedFixedBackgroundPreflightUsesCanvasScaleAndTransparentExemption(
        int sourceWidth, int sourceHeight, bool upscaled)
    {
        var backgroundId = Guid.NewGuid();
        var document = new EditorDocument
        {
            CanvasWidth = 1000, CanvasHeight = 1000, FixedBackgroundAssetId = backgroundId
        };
        document.AddAsset(backgroundId, [1]);
        var before = document.Copy();
        var region = new PngExportRegion(100, 100, 100, 100);
        var warnings = ExportPreflight.Inspect(document, _ => false, 500,
            _ => (sourceWidth, sourceHeight), outputRegion: region);
        var transparent = ExportPreflight.Inspect(document, _ => false, 500,
            _ => (sourceWidth, sourceHeight), transparentBackground: true, outputRegion: region);
        Assert.Multiple(() =>
        {
            Assert.That(warnings.Count(warning => warning.Kind == ExportWarningKind.LowResolutionArtwork),
                Is.EqualTo(upscaled ? 1 : 0));
            if (upscaled) Assert.That(warnings.Single().Message, Does.Contain("displayed 5000×5000 px"));
            Assert.That(transparent, Is.Empty);
            Assert.That(document.StateEquals(before), Is.True);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CliCropQualityBlocksFixedBackgroundBeforeWritingAndKeepsControlsWorking(
        bool destinationExists)
    {
        AvaloniaTestHost.EnsureStarted();
        var backgroundId = Guid.NewGuid();
        var document = new EditorDocument
        {
            CanvasWidth = 1000, CanvasHeight = 1000, FixedBackgroundAssetId = backgroundId
        };
        document.AddAsset(backgroundId,
            TestImages.EncodePng(500, 500, TestImages.SyntheticBgra(500, 500)));
        var parent = ParentFolder();
        var source = Path.Combine(parent, "source.ppgproj");
        var blocked = Path.Combine(parent, "blocked.png");
        byte[] original = [80, 82, 69, 83, 69, 82, 86, 69];
        try
        {
            NativePackageStore.Save(source, document, "r3x-audit-test");
            var sourceBytes = File.ReadAllBytes(source);
            if (destinationExists) File.WriteAllBytes(blocked, original);
            var blockedError = new StringWriter();
            var blockedCode = RenderCommand.Run(["--render", source, blocked, "--width", "500",
                "--crop", "100,100,100,100", "--block-low-resolution"],
                new StringWriter(), blockedError, () => { });
            Assert.Multiple(() =>
            {
                Assert.That(blockedCode, Is.EqualTo(1));
                Assert.That(blockedError.ToString(), Does.Contain("RENDER BLOCKED"));
                Assert.That(File.Exists(blocked), Is.EqualTo(destinationExists));
                if (destinationExists) Assert.That(File.ReadAllBytes(blocked), Is.EqualTo(original));
                Assert.That(File.ReadAllBytes(source), Is.EqualTo(sourceBytes));
            });

            var full = Path.Combine(parent, "full.png");
            var fullError = new StringWriter();
            Assert.That(RenderCommand.Run(["--render", source, full, "--width", "500",
                "--block-low-resolution"], new StringWriter(), fullError, () => { }), Is.Zero,
                fullError.ToString());
            Assert.That(File.ReadAllBytes(full), Is.EqualTo(TestImages.RenderPng(document, false, 500)));
            Assert.That(fullError.ToString(), Does.Not.Contain("LowResolutionArtwork"));

            var advisory = Path.Combine(parent, "advisory.png");
            var advisoryError = new StringWriter();
            var region = new PngExportRegion(100, 100, 100, 100);
            Assert.That(RenderCommand.Run(["--render", source, advisory, "--width", "500",
                "--crop", "100,100,100,100"], new StringWriter(), advisoryError, () => { }), Is.Zero,
                advisoryError.ToString());
            Assert.That(advisoryError.ToString(), Does.Contain("LowResolutionArtwork"));
            Assert.That(File.ReadAllBytes(advisory),
                Is.EqualTo(TestImages.RenderPngRegion(document, false, 500, region)));

            var transparent = Path.Combine(parent, "transparent.png");
            var transparentError = new StringWriter();
            Assert.That(RenderCommand.Run(["--render", source, transparent, "--width", "500",
                "--crop", "100,100,100,100", "--transparent", "--block-low-resolution"],
                new StringWriter(), transparentError, () => { }), Is.Zero, transparentError.ToString());
            Assert.That(transparentError.ToString(), Does.Not.Contain("LowResolutionArtwork"));
            Assert.That(File.ReadAllBytes(transparent),
                Is.EqualTo(TestImages.RenderPngRegion(document, true, 500, region)));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(sourceBytes));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static T Control<T>(Control parent, string name) where T : Control =>
        parent.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static string ParentFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "r3x-audit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
