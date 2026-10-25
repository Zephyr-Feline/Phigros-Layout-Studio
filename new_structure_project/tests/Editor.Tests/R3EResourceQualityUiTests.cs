using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3EResourceQualityUiTests
{
    [Test]
    public void ResourcePanelCleanupConfirmsThenPerformsOneUndoableEdit()
    {
        AvaloniaTestHost.EnsureStarted();
        var document = CleanupDocument(out var referenced, out var orphan, out var layerId);
        var edit = new EditSession(document);
        edit.SelectedLayerId = layerId;
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var name = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerName");
            name.Text = "未应用的名称草稿";
            UiTestActions.ClickNamed(window, "OpenResourcePanel");
            PumpUntil(() => window.OwnedWindows.OfType<ResourcePanelDialog>().Any());
            var dialog = window.OwnedWindows.OfType<ResourcePanelDialog>().Single();
            UiTestActions.ClickNamed(dialog, "CleanupUnreferencedAssets");
            PumpUntil(() => dialog.OwnedWindows.OfType<ConfirmDialog>().Any());
            var confirm = dialog.OwnedWindows.OfType<ConfirmDialog>().Single();
            Assert.That(confirm.GetLogicalDescendants().OfType<TextBlock>()
                .Any(block => block.Text?.Contains("内嵌资源", StringComparison.Ordinal) == true), Is.True);
            UiTestActions.ClickNamed(confirm, "AcceptConfirm");
            PumpUntil(() => !edit.Document.Assets.ContainsKey(orphan));
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Assets.ContainsKey(referenced), Is.True);
                Assert.That(edit.Document.Find(layerId).AssetId, Is.EqualTo(referenced));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
                Assert.That(edit.Undo(), Is.True);
            });
            Assert.That(edit.Document.Assets.ContainsKey(orphan), Is.True);
            edit.Redo();
            UiTestActions.ClickNamed(dialog, "CloseResourcePanel");
            PumpUntil(() => !window.OwnedWindows.OfType<ResourcePanelDialog>().Any());
            PumpUntil(() => UiTestActions.Status(window)?.Contains("清理", StringComparison.Ordinal) == true);
            Assert.That(edit.Document.Assets.ContainsKey(orphan), Is.False);
            Assert.That(name.Text, Is.EqualTo("未应用的名称草稿"),
                "asset cleanup must not submit or overwrite side-panel drafts");
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
        }
    }

    [Test]
    public void ExportWidthDialogShowsResolutionWarningOnlyWhenTheTaskWouldUpscale()
    {
        AvaloniaTestHost.EnsureStarted();
        var dialog = new PngExportDialog(ImageDocument());
        try
        {
            dialog.Show();
            var quality = dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "PngOutputQuality");
            Assert.That(quality.Text, Does.Contain("源图分辨率不足"));
            var width = dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "PngOutputWidth");
            width.Text = "500";
            Dispatcher.UIThread.RunJobs();
            Assert.That(quality.Text, Does.Contain("未发现源图放大提示"));
        }
        finally { dialog.Close(); }
    }

    [Test]
    public void BatchQueueShowsResolutionWarningForTheSelectedTask()
    {
        AvaloniaTestHost.EnsureStarted();
        var dialog = new PngExportQueueDialog(ImageDocument(),
            Array.Empty<ExportWarning>(), () => Task.FromResult<string?>(null));
        try
        {
            dialog.Show();
            var resolution = dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "PngQueueResolution");
            Assert.That(resolution.Text, Does.Contain("源图分辨率不足"));
            var width = dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "PngQueueWidth");
            width.Text = "500";
            UiTestActions.ClickNamed(dialog, "AddPngQueueTask");
            Dispatcher.UIThread.RunJobs();
            Assert.That(resolution.Text, Does.Contain("未发现源图放大提示"));
        }
        finally { dialog.Close(); }
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

    private static EditorDocument CleanupDocument(out Guid referenced, out Guid orphan,
        out Guid layerId)
    {
        referenced = Guid.NewGuid();
        orphan = Guid.NewGuid();
        var layer = new Layer
        {
            Kind = LayerKind.Image, Name = "保留曲绘", AssetId = referenced,
            Bounds = new RectD(20, 20, 120, 80)
        };
        layerId = layer.Id;
        var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360,
            Layers = [layer] };
        document.AddAsset(referenced, TestImages.EncodePng(4, 4,
            TestImages.SyntheticBgra(4, 4)));
        document.AddAsset(orphan, TestImages.EncodePng(8, 8,
            TestImages.SyntheticBgra(8, 8)));
        document.Validate();
        return document;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(condition(), Is.True, "The UI operation did not complete.");
    }
}
