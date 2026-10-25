using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3FExportQualityUiTests
{
    [Test]
    public void SingleExportPolicyBlocksContinueUntilTheOutputStopsUpscaling()
    {
        AvaloniaTestHost.EnsureStarted();
        var dialog = new PngExportDialog(ImageDocument());
        try
        {
            dialog.Show();
            var block = dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(check => check.Name == "PngBlockLowResolution");
            var confirm = dialog.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "ConfirmPngSize");
            block.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(confirm.IsEnabled, Is.False);
                Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "PngOutputQuality").Text,
                    Does.Contain("阻止继续"));
            });
            var width = dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "PngOutputWidth");
            width.Text = "500";
            Dispatcher.UIThread.RunJobs();
            Assert.That(confirm.IsEnabled, Is.True);
        }
        finally { dialog.Close(); }
    }

    [Test]
    public void BatchPolicyBlocksStartUntilTheWarningIsClearedOrPolicyIsOff()
    {
        AvaloniaTestHost.EnsureStarted();
        var parent = Path.Combine(Path.GetTempPath(),
            "r3f-policy-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(parent);
        var dialog = new PngExportQueueDialog(ImageDocument(),
            Array.Empty<ExportWarning>(), () => Task.FromResult<string?>(parent));
        try
        {
            dialog.Show();
            UiTestActions.ClickNamed(dialog, "ChoosePngQueueDirectory");
            PumpUntil(() => dialog.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "StartPngQueueExport").IsEnabled);
            var start = dialog.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "StartPngQueueExport");
            var block = dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(check => check.Name == "PngQueueBlockLowResolution");
            block.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(start.IsEnabled, Is.False);
                Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "PngQueuePolicyNote").Text,
                    Does.Contain("阻止开始导出"));
            });
            block.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.That(start.IsEnabled, Is.True);
        }
        finally
        {
            dialog.Close();
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    private static EditorDocument ImageDocument()
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

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(condition(), Is.True, "The UI operation did not complete.");
    }
}
