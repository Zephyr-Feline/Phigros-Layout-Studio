using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

[NonParallelizable]
public sealed class ResourcePanelLifecycleRegressionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ClosingWhileLoadingDiscardsLateReplacementAndLeavesMainWindowFeedbackCurrent(bool closeButton)
    {
        var loading = new TaskCompletionSource<LoadedImage>();
        using var host = new Host(load: _ => loading.Task);
        host.SetDrafts();
        var before = host.Session.Document.Copy();
        var panel = host.Open();
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        var status = Control<TextBlock>(panel, "ResourceStatus").Text;
        var details = Control<TextBlock>(panel, "ResourceDetails").Text;
        if (closeButton) UiTestActions.ClickNamed(panel, "CloseResourcePanel");
        else panel.Close();
        PumpUntil(() => UiTestActions.Status(host.Window)?.Contains("已关闭资源", StringComparison.Ordinal) == true);
        var mainStatus = UiTestActions.Status(host.Window);
        var title = host.Window.Title;
        loading.SetResult(Picture());
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(host.Session.Document.StateEquals(before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
            Assert.That(host.Session.IsDirty, Is.False);
            Assert.That(panel.DocumentChanged, Is.False);
            Assert.That(panel.BackgroundChanged, Is.False);
            Assert.That(panel.ChangeSummary, Is.Null);
            Assert.That(Control<TextBlock>(panel, "ResourceStatus").Text, Is.EqualTo(status));
            Assert.That(Control<TextBlock>(panel, "ResourceDetails").Text, Is.EqualTo(details));
            Assert.That(UiTestActions.Status(host.Window), Is.EqualTo(mainStatus));
            Assert.That(host.Window.Title, Is.EqualTo(title));
        });
        host.AssertDrafts();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ClosedPickerDiscardsBothResultsAndErrorsWithoutStartingAnImageRead(bool fail)
    {
        var picker = new TaskCompletionSource<string?>();
        var reads = 0;
        using var host = new Host(choose: _ => picker.Task,
            load: _ => { reads++; return Task.FromResult(Picture()); });
        var before = host.Session.Document.Copy();
        var panel = host.Open();
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        panel.Close();
        var error = Control<TextBlock>(panel, "ResourceError").Text;
        if (fail) picker.SetException(new IOException("late picker failure"));
        else picker.SetResult("replacement.png");
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(reads, Is.Zero);
            Assert.That(host.Session.Document.StateEquals(before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
            Assert.That(panel.DocumentChanged, Is.False);
            Assert.That(Control<TextBlock>(panel, "ResourceError").Text, Is.EqualTo(error));
        });
    }

    [Test]
    public void ClosedLoaderFailureDoesNotWriteErrorOrRefreshAReopenedPanel()
    {
        var loading = new TaskCompletionSource<LoadedImage>();
        using var host = new Host(load: _ => loading.Task);
        var panel = host.Open();
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        panel.Close();
        PumpUntil(() => UiTestActions.Status(host.Window)?.Contains("已关闭资源", StringComparison.Ordinal) == true);
        var reopened = host.Open();
        var before = host.Session.Document.Copy();
        var mainStatus = UiTestActions.Status(host.Window);
        var error = Control<TextBlock>(panel, "ResourceError").Text;
        loading.SetException(new IOException("late image failure"));
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(Control<TextBlock>(panel, "ResourceError").Text, Is.EqualTo(error));
            Assert.That(Control<TextBlock>(reopened, "ResourceError").Text, Is.Null.Or.Empty);
            Assert.That(host.Session.Document.StateEquals(before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
            Assert.That(reopened.DocumentChanged, Is.False);
            Assert.That(UiTestActions.Status(host.Window), Is.EqualTo(mainStatus));
        });
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void ReplacementRejectsChangedOrReplacedDocumentAtEachAsyncBoundary(bool loadingBoundary,
        bool replaceDocument)
    {
        var picker = new TaskCompletionSource<string?>();
        var loading = new TaskCompletionSource<LoadedImage>();
        var reads = 0;
        using var host = new Host(choose: _ => loadingBoundary
                ? Task.FromResult<string?>("replacement.png") : picker.Task,
            load: _ => { reads++; return loading.Task; });
        var panel = host.Open();
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        if (replaceDocument) host.Session.ReplaceDocument(host.Session.Document.Copy());
        else host.Session.UpdateLayer(host.LayerId, layer => layer.Name = "另一次已提交修改");
        var current = host.Session.Document;
        var before = current.Copy();
        var undo = host.Session.UndoCount;
        var dirty = host.Session.IsDirty;
        if (loadingBoundary) loading.SetResult(Picture());
        else picker.SetResult("replacement.png");
        PumpUntil(() => Control<TextBlock>(panel, "ResourceError").Text?.Contains("发生变化", StringComparison.Ordinal) == true);
        Assert.Multiple(() =>
        {
            Assert.That(host.Session.Document, Is.SameAs(current));
            Assert.That(host.Session.Document.StateEquals(before), Is.True);
            Assert.That(host.Session.UndoCount, Is.EqualTo(undo));
            Assert.That(host.Session.IsDirty, Is.EqualTo(dirty));
            Assert.That(reads, Is.EqualTo(loadingBoundary ? 1 : 0));
            Assert.That(panel.DocumentChanged, Is.False);
            Assert.That(panel.ChangeSummary, Is.Null);
        });
    }

    [Test]
    public void SuccessfulReplacementIsSingleFlightOneUndoAndKeepsUnappliedDrafts()
    {
        var loading = new TaskCompletionSource<LoadedImage>();
        var picks = 0;
        using var host = new Host(choose: _ => { picks++; return Task.FromResult<string?>("replacement.png"); },
            load: _ => loading.Task);
        host.SetDrafts();
        var before = host.Session.Document.Copy();
        var panel = host.Open();
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        UiTestActions.ClickNamed(panel, "CleanupUnreferencedAssets");
        Assert.That(picks, Is.EqualTo(1));
        Assert.That(panel.OwnedWindows, Is.Empty);
        loading.SetResult(Picture());
        PumpUntil(() => panel.DocumentChanged);
        Assert.Multiple(() =>
        {
            Assert.That(host.Session.UndoCount, Is.EqualTo(1));
            Assert.That(host.Session.Document.Find(host.LayerId).Bounds, Is.EqualTo(before.Find(host.LayerId).Bounds));
            Assert.That(host.Session.Document.Find(host.LayerId).Name, Is.EqualTo(before.Find(host.LayerId).Name));
            Assert.That(host.Session.Document.Find(host.LayerId).AssetId, Is.Not.EqualTo(before.Find(host.LayerId).AssetId));
            Assert.That(panel.BackgroundChanged, Is.False);
        });
        UiTestActions.ClickNamed(panel, "CloseResourcePanel");
        PumpUntil(() => UiTestActions.Status(host.Window)?.Contains("资源已安全替换", StringComparison.Ordinal) == true);
        host.AssertDrafts();
        Assert.That(host.Window.Title, Does.Contain("*"));
        Assert.That(host.Session.Undo(), Is.True);
        Assert.That(host.Session.Document.StateEquals(before), Is.True);
        Assert.That(host.Session.Redo(), Is.True);
        Assert.That(host.Session.Document.Find(host.LayerId).AssetId, Is.Not.EqualTo(before.Find(host.LayerId).AssetId));
    }

    [Test]
    public void CancelledPickerAndEmptyCleanupPreserveRedoAndUnappliedDrafts()
    {
        using var host = new Host(choose: _ => Task.FromResult<string?>(null));
        host.Session.UpdateLayer(host.LayerId, layer => layer.Name = "可重做名称");
        host.Session.Undo();
        host.SetDrafts();
        var before = host.Session.Document.Copy();
        var panel = host.Open();
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        UiTestActions.ClickNamed(panel, "CleanupUnreferencedAssets");
        Assert.Multiple(() =>
        {
            Assert.That(host.Session.Document.StateEquals(before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
            Assert.That(host.Session.RedoCount, Is.EqualTo(1));
            Assert.That(host.Session.IsDirty, Is.False);
            Assert.That(panel.DocumentChanged, Is.False);
        });
        panel.Close();
        Dispatcher.UIThread.RunJobs();
        host.AssertDrafts();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReplacementFailureIsAtomicPreservesRedoAndAllowsRetry(bool invalidImage)
    {
        var calls = 0;
        using var host = new Host(load: _ => ++calls == 1
            ? invalidImage ? Task.FromResult(new LoadedImage(new AssetBlob([1, 2, 3]), 1, 1))
                : Task.FromException<LoadedImage>(new IOException("read failure"))
            : Task.FromResult(Picture()));
        host.Session.UpdateLayer(host.LayerId, layer => layer.Name = "可重做名称");
        host.Session.Undo();
        var before = host.Session.Document.Copy();
        var panel = host.Open();
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        PumpUntil(() => !string.IsNullOrEmpty(Control<TextBlock>(panel, "ResourceError").Text));
        Assert.Multiple(() =>
        {
            Assert.That(host.Session.Document.StateEquals(before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
            Assert.That(host.Session.RedoCount, Is.EqualTo(1));
            Assert.That(panel.DocumentChanged, Is.False);
        });
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        PumpUntil(() => panel.DocumentChanged);
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(host.Session.UndoCount, Is.EqualTo(1));
            Assert.That(host.Session.RedoCount, Is.Zero);
            Assert.That(Control<TextBlock>(panel, "ResourceError").Text, Is.Empty);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CleanupConfirmationRejectsChangesToTheDocumentOrDocumentIdentity(bool replaceDocument)
    {
        using var host = new Host(orphan: true);
        var panel = host.Open();
        UiTestActions.ClickNamed(panel, "CleanupUnreferencedAssets");
        PumpUntil(() => panel.OwnedWindows.OfType<ConfirmDialog>().Any());
        var confirm = panel.OwnedWindows.OfType<ConfirmDialog>().Single();
        if (replaceDocument) host.Session.ReplaceDocument(host.Session.Document.Copy());
        else host.Session.UpdateLayer(host.LayerId, layer => layer.Name = "确认期间已修改");
        var before = host.Session.Document.Copy();
        var undo = host.Session.UndoCount;
        UiTestActions.ClickNamed(confirm, "AcceptConfirm");
        PumpUntil(() => Control<TextBlock>(panel, "ResourceError").Text?.Contains("发生变化", StringComparison.Ordinal) == true);
        Assert.Multiple(() =>
        {
            Assert.That(host.Session.Document.StateEquals(before), Is.True);
            Assert.That(host.Session.UndoCount, Is.EqualTo(undo));
            Assert.That(panel.DocumentChanged, Is.False);
        });
    }

    [Test]
    public void CancelledCleanupKeepsInventoryAndRedo()
    {
        using var host = new Host(orphan: true);
        host.Session.UpdateLayer(host.LayerId, layer => layer.Name = "可重做名称");
        host.Session.Undo();
        var before = host.Session.Document.Copy();
        var panel = host.Open();
        UiTestActions.ClickNamed(panel, "CleanupUnreferencedAssets");
        PumpUntil(() => panel.OwnedWindows.OfType<ConfirmDialog>().Any());
        UiTestActions.ClickNamed(panel.OwnedWindows.OfType<ConfirmDialog>().Single(), "CancelConfirm");
        PumpUntil(() => Control<TextBlock>(panel, "ResourceStatus").Text?.Contains("已取消", StringComparison.Ordinal) == true);
        Assert.Multiple(() =>
        {
            Assert.That(host.Session.Document.StateEquals(before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
            Assert.That(host.Session.RedoCount, Is.EqualTo(1));
            Assert.That(panel.DocumentChanged, Is.False);
        });
    }

    [Test]
    public void ReplacingAnOrdinaryResourceAfterTheFixedBackgroundKeepsCumulativeBackgroundFlag()
    {
        using var host = new Host(background: true);
        var panel = host.Open();
        var list = Control<ListBox>(panel, "ResourceList");
        list.SelectedItem = list.Items.OfType<AssetResource>().Single(resource => resource.IsFixedBackground);
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        PumpUntil(() => panel.BackgroundChanged);
        var background = host.Session.Document.FixedBackgroundAssetId;
        list.SelectedItem = list.Items.OfType<AssetResource>().Single(resource => !resource.IsFixedBackground);
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        PumpUntil(() => host.Session.UndoCount == 2);
        Assert.Multiple(() =>
        {
            Assert.That(panel.BackgroundChanged, Is.True);
            Assert.That(panel.DocumentChanged, Is.True);
            Assert.That(host.Session.Document.FixedBackgroundAssetId, Is.EqualTo(background));
        });
        panel.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.That(host.Session.Undo(), Is.True);
        Assert.That(host.Session.Undo(), Is.True);
        Assert.That(host.Session.Document.FixedBackgroundAssetId, Is.EqualTo(host.BackgroundId));
    }

    [Test]
    public void FixedBackgroundReplacementRefreshesFeedbackAndKeepsUnappliedLayerAndCanvasDrafts()
    {
        using var host = new Host(background: true);
        host.SetDrafts();
        var before = host.Session.Document.Copy();
        var panel = host.Open();
        var list = Control<ListBox>(panel, "ResourceList");
        list.SelectedItem = list.Items.OfType<AssetResource>().Single(resource => resource.IsFixedBackground);
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        PumpUntil(() => panel.BackgroundChanged);
        Assert.That(host.Session.Document.FixedBackgroundAssetId, Is.Not.EqualTo(host.BackgroundId));
        UiTestActions.ClickNamed(panel, "CloseResourcePanel");
        PumpUntil(() => UiTestActions.Status(host.Window)?.Contains("资源已安全替换", StringComparison.Ordinal) == true);
        host.AssertDrafts();
        Assert.Multiple(() =>
        {
            Assert.That(host.Session.Document.CanvasWidth, Is.EqualTo(before.CanvasWidth));
            Assert.That(host.Session.Document.BackgroundArgb, Is.EqualTo(before.BackgroundArgb));
            Assert.That(host.Session.Document.Find(host.LayerId).Name, Is.EqualTo(before.Find(host.LayerId).Name));
            Assert.That(host.Session.UndoCount, Is.EqualTo(1));
            Assert.That(host.Window.Title, Does.Contain("*"));
        });
        Assert.That(host.Session.Undo(), Is.True);
        Assert.That(host.Session.Document.StateEquals(before), Is.True);
    }

    private static LoadedImage Picture() => new(new AssetBlob(TestImages.EncodePng(8, 6,
        TestImages.SyntheticBgra(8, 6))), 8, 6);

    private static T Control<T>(Window window, string name) where T : Avalonia.Controls.Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.That(condition(), Is.True, "The resource operation did not complete.");
    }

    private sealed class Host : IDisposable
    {
        public EditSession Session { get; }
        public MainWindow Window { get; }
        public Guid LayerId { get; }
        public Guid? BackgroundId { get; }

        public Host(Func<EditorFileAction, Task<string?>>? choose = null,
            Func<string, Task<LoadedImage>>? load = null, bool orphan = false, bool background = false)
        {
            AvaloniaTestHost.EnsureStarted();
            var asset = Guid.NewGuid();
            var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 };
            document.AddAsset(asset, P4AutomaticBackgroundTests.Solid(0xFFFF0000));
            var layer = new Layer
            {
                Kind = LayerKind.Image, Name = "资源图层", AssetId = asset,
                Bounds = new RectD(20, 20, 200, 120)
            };
            LayerId = layer.Id;
            document.Layers.Add(layer);
            if (orphan || background)
            {
                var extra = Guid.NewGuid();
                document.AddAsset(extra, P4AutomaticBackgroundTests.Solid(0xFF0000FF));
                if (background) document.FixedBackgroundAssetId = BackgroundId = extra;
            }
            document.Validate();
            Session = new EditSession(document);
            Session.SelectedLayerId = LayerId;
            Window = new MainWindow(Session,
                filePathPicker: choose ?? (_ => Task.FromResult<string?>("replacement.png")),
                imageLoader: load ?? (_ => Task.FromResult(Picture())));
            Window.Show();
        }

        public ResourcePanelDialog Open()
        {
            UiTestActions.ClickNamed(Window, "OpenResourcePanel");
            PumpUntil(() => Window.OwnedWindows.OfType<ResourcePanelDialog>().Any());
            return Window.OwnedWindows.OfType<ResourcePanelDialog>().Single();
        }

        public void SetDrafts()
        {
            Control<TextBox>(Window, "LayerName").Text = "未应用的图层名称";
            Control<TextBox>(Window, "CanvasWidth").Text = "未应用的画布宽度";
            Control<TextBox>(Window, "BackgroundColor").Text = "未应用的背景颜色";
            Control<TextBox>(Window, "BackgroundBlur").Text = "未应用的背景模糊";
        }

        public void AssertDrafts()
        {
            Assert.That(Control<TextBox>(Window, "LayerName").Text, Is.EqualTo("未应用的图层名称"));
            Assert.That(Control<TextBox>(Window, "CanvasWidth").Text, Is.EqualTo("未应用的画布宽度"));
            Assert.That(Control<TextBox>(Window, "BackgroundColor").Text, Is.EqualTo("未应用的背景颜色"));
            Assert.That(Control<TextBox>(Window, "BackgroundBlur").Text, Is.EqualTo("未应用的背景模糊"));
        }

        public void Dispose()
        {
            Session.MarkSaved();
            foreach (var dialog in Window.OwnedWindows.ToArray()) dialog.Close();
            Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
