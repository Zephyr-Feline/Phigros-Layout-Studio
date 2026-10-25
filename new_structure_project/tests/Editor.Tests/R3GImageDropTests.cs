using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3GImageDropTests
{
    private string _folder = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(),
            "r3g-drop-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Test]
    public void PathFilterKeepsSupportedFilesInOrderAndCountsIgnoredItems()
    {
        var first = Path.Combine(_folder, "A.png");
        var second = Path.Combine(_folder, "B.jpg");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not an image");
        Directory.CreateDirectory(Path.Combine(_folder, "subdir"));
        var selection = ImageImportPathFilter.Filter([
            first,
            second,
            Path.Combine(_folder, "notes.txt"),
            Path.Combine(_folder, "missing.png"),
            Path.Combine(_folder, "subdir"),
            first.ToUpperInvariant()
        ]);
        Assert.Multiple(() =>
        {
            Assert.That(selection.Paths, Is.EqualTo(new[] { first, second }));
            Assert.That(selection.Ignored, Is.EqualTo(4));
        });
    }

    [Test]
    public void DroppedPathsUseTheBatchWorkflowAsOneUndoableImport()
    {
        AvaloniaTestHost.EnsureStarted();
        var first = Path.Combine(_folder, "拖放一.png");
        var second = Path.Combine(_folder, "drop-two.png");
        File.WriteAllBytes(first, TestImages.EncodePng(8, 6,
            TestImages.SyntheticBgra(8, 6)));
        File.WriteAllBytes(second, TestImages.EncodePng(12, 9,
            TestImages.SyntheticBgra(12, 9)));
        var edit = new EditSession(new EditorDocument { CanvasWidth = 900, CanvasHeight = 600 });
        var window = new MainWindow(edit, imageLoader: path =>
        {
            var bytes = File.ReadAllBytes(path);
            var (width, height, _) = TestImages.DecodePng(bytes);
            return Task.FromResult(new LoadedImage(new AssetBlob(bytes), width, height));
        });
        try
        {
            window.Show();
            var task = window.ImportDroppedImagesAsync([first, second]);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Assert.That(task.IsCompleted, Is.True);
            Assert.That(task.GetAwaiter().GetResult(), Is.EqualTo(2));
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Layers, Has.Count.EqualTo(2));
                Assert.That(edit.Document.Layers.Select(layer => layer.Name),
                    Is.EqualTo(new[] { "拖放一.png", "drop-two.png" }));
                Assert.That(edit.SelectedLayerIds, Has.Count.EqualTo(2));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
                Assert.That(UiTestActions.Status(window), Does.Contain("拖放"));
                Assert.That(window.OwnedWindows, Is.Empty);
            });
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Layers, Is.Empty);
        }
        finally
        {
            edit.MarkSaved();
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
        }
    }

    [Test]
    public void UnsupportedDropLeavesTheDocumentAndHistoryUntouched()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(new EditorDocument());
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var count = window.ImportDroppedImagesAsync(
                [Path.Combine(_folder, "missing.png"), Path.Combine(_folder, "note.txt")])
                .GetAwaiter().GetResult();
            Assert.Multiple(() =>
            {
                Assert.That(count, Is.Zero);
                Assert.That(edit.Document.Layers, Is.Empty);
                Assert.That(edit.UndoCount, Is.Zero);
                Assert.That(edit.IsDirty, Is.False);
                Assert.That(UiTestActions.Status(window), Does.Contain("没有支持的本地图片"));
            });
        }
        finally { window.Close(); }
    }
}
