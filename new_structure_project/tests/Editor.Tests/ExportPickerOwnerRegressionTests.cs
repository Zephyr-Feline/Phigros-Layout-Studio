using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

[NonParallelizable]
public sealed class ExportPickerOwnerRegressionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void PngQueueNativeFolderPickerUsesTheQueueAsItsOwner(bool selectedOnly)
    {
        using var host = new PickerHost();
        UiTestActions.ClickNamed(host.Window,
            selectedOnly ? "BatchExportSelectionPng" : "BatchExportPng");
        var queue = host.Modal<PngExportQueueDialog>();
        UiTestActions.ClickNamed(queue, "ChoosePngQueueDirectory");
        PumpUntil(() => host.Factory.Calls.Count == 1);
        Assert.Multiple(() =>
        {
            Assert.That(host.Factory.Calls.Single(), Is.SameAs(queue));
            Assert.That(host.Session.Document.StateEquals(host.Before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
            Assert.That(Control<Button>(queue, "StartPngQueueExport").IsEnabled, Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CrossProjectNativePickersUseTheBatchDialogAsTheirOwner(bool projects)
    {
        using var host = new PickerHost();
        UiTestActions.ClickNamed(host.Window, "BatchRenderProjects");
        var queue = host.Modal<ProjectBatchRenderDialog>();
        UiTestActions.ClickNamed(queue,
            projects ? "ChooseProjectBatchProjects" : "ChooseProjectBatchDirectory");
        PumpUntil(() => host.Factory.Calls.Count == 1);
        Assert.Multiple(() =>
        {
            Assert.That(host.Factory.Calls.Single(), Is.SameAs(queue));
            Assert.That(host.Session.Document.StateEquals(host.Before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
            Assert.That(Control<Button>(queue, "StartProjectBatch").IsEnabled, Is.False);
        });
    }

    [Test]
    public void SongGridNativeImagePickerUsesTheGridDialogAsItsOwner()
    {
        using var host = new PickerHost();
        UiTestActions.ClickNamed(host.Window, "BuildSongGrid");
        var grid = host.Modal<SongGridDialog>();
        UiTestActions.ClickNamed(grid, "ChooseSongGridImages");
        PumpUntil(() => host.Factory.Calls.Count == 1);
        Assert.Multiple(() =>
        {
            Assert.That(host.Factory.Calls.Single(), Is.SameAs(grid));
            Assert.That(host.Session.Document.StateEquals(host.Before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
        });
    }

    [Test]
    public void ResourceReplacementNativePickerUsesTheResourcePanelAsItsOwner()
    {
        using var host = new PickerHost(document: ResourceDocument());
        UiTestActions.ClickNamed(host.Window, "OpenResourcePanel");
        var panel = host.Modal<ResourcePanelDialog>();
        UiTestActions.ClickNamed(panel, "ReplaceResource");
        PumpUntil(() => host.Factory.Calls.Count == 1);
        Assert.Multiple(() =>
        {
            Assert.That(host.Factory.Calls.Single(), Is.SameAs(panel));
            Assert.That(host.Session.Document.StateEquals(host.Before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
        });
    }

    [Test]
    public void SongInfoReplacementNativePickerUsesTheWorkbenchAsItsOwner()
    {
        using var host = new PickerHost(document: SongDocument());
        UiTestActions.ClickNamed(host.Window, "OpenSongInfoWorkbench");
        var workbench = host.Modal<SongInfoWorkbenchDialog>();
        UiTestActions.ClickNamed(workbench, "SongInfoReplace_0");
        PumpUntil(() => host.Factory.Calls.Count == 1);
        Assert.Multiple(() =>
        {
            Assert.That(host.Factory.Calls.Single(), Is.SameAs(workbench));
            Assert.That(host.Session.Document.StateEquals(host.Before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PngDirectoryChoiceIsSingleFlightAndDoesNotUpdateAfterClose(bool closeWhilePending)
    {
        var completion = new TaskCompletionSource<string?>();
        var calls = 0;
        using var host = new PickerHost(() => { calls++; return completion.Task; });
        UiTestActions.ClickNamed(host.Window, "BatchExportPng");
        var queue = host.Modal<PngExportQueueDialog>();
        var directory = Control<TextBlock>(queue, "PngQueueDirectory");
        var before = directory.Text;
        var choose = Control<Button>(queue, "ChoosePngQueueDirectory");
        UiTestActions.Click(choose);
        UiTestActions.Click(choose); // Raised directly: the guard also protects keyboard/event reentry.
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(choose.IsEnabled, Is.False);
            Assert.That(Control<Button>(queue, "StartPngQueueExport").IsEnabled, Is.False);
        });
        if (closeWhilePending) queue.Close();
        completion.SetResult(Path.GetTempPath());
        Dispatcher.UIThread.RunJobs();
        if (!closeWhilePending) PumpUntil(() => choose.IsEnabled);
        Assert.Multiple(() =>
        {
            Assert.That(directory.Text, closeWhilePending ? Is.EqualTo(before) : Does.Contain(Path.GetTempPath()));
            Assert.That(Control<Button>(queue, "StartPngQueueExport").IsEnabled, Is.EqualTo(!closeWhilePending));
            Assert.That(host.Session.Document.StateEquals(host.Before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CrossProjectPickersShareSingleFlightAndIgnoreLateResultsAfterClose(bool projects)
    {
        var folders = new TaskCompletionSource<string?>();
        var files = new TaskCompletionSource<IReadOnlyList<string>>();
        var folderCalls = 0;
        var fileCalls = 0;
        using var host = new PickerHost(
            () => { folderCalls++; return folders.Task; },
            () => { fileCalls++; return files.Task; });
        UiTestActions.ClickNamed(host.Window, "BatchRenderProjects");
        var queue = host.Modal<ProjectBatchRenderDialog>();
        var chooseFiles = Control<Button>(queue, "ChooseProjectBatchProjects");
        var chooseFolder = Control<Button>(queue, "ChooseProjectBatchDirectory");
        var directory = Control<TextBlock>(queue, "ProjectBatchDirectory");
        var before = directory.Text;
        var first = projects ? chooseFiles : chooseFolder;
        UiTestActions.Click(first);
        UiTestActions.Click(first);
        UiTestActions.Click(projects ? chooseFolder : chooseFiles);
        Assert.Multiple(() =>
        {
            Assert.That(folderCalls, Is.EqualTo(projects ? 0 : 1));
            Assert.That(fileCalls, Is.EqualTo(projects ? 1 : 0));
            Assert.That(chooseFiles.IsEnabled, Is.False);
            Assert.That(chooseFolder.IsEnabled, Is.False);
        });
        queue.Close();
        if (projects) files.SetResult([Path.Combine(Path.GetTempPath(), "picker-project.ppgproj")]);
        else folders.SetResult(Path.GetTempPath());
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(directory.Text, Is.EqualTo(before));
            Assert.That(Control<ListBox>(queue, "ProjectBatchProjectList").Items.Count, Is.Zero);
            Assert.That(Control<Button>(queue, "StartProjectBatch").IsEnabled, Is.False);
            Assert.That(host.Session.Document.StateEquals(host.Before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
        });
    }

    [TestCase("png")]
    [TestCase("batch-directory")]
    [TestCase("batch-projects")]
    public void PickerFailureIsDisplayedAndAllowsAnotherChoice(string action)
    {
        var calls = 0;
        const string failure = "picker failure for retry";
        Task<string?> DirectoryPicker() => ++calls == 1
            ? Task.FromException<string?>(new IOException(failure))
            : Task.FromResult<string?>(null);
        Task<IReadOnlyList<string>> ProjectPicker() => ++calls == 1
            ? Task.FromException<IReadOnlyList<string>>(new IOException(failure))
            : Task.FromResult<IReadOnlyList<string>>([]);
        using var host = new PickerHost(DirectoryPicker, ProjectPicker);
        UiTestActions.ClickNamed(host.Window, action == "png" ? "BatchExportPng" : "BatchRenderProjects");
        Window queue = action == "png" ? host.Modal<PngExportQueueDialog>() : host.Modal<ProjectBatchRenderDialog>();
        var button = Control<Button>(queue, action switch
        {
            "png" => "ChoosePngQueueDirectory", "batch-projects" => "ChooseProjectBatchProjects",
            _ => "ChooseProjectBatchDirectory"
        });
        UiTestActions.Click(button);
        var error = Control<TextBlock>(queue, action == "png" ? "PngQueueError" : "ProjectBatchError");
        PumpUntil(() => error.Text?.Contains(failure, StringComparison.Ordinal) == true && button.IsEnabled);
        UiTestActions.Click(button);
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(button.IsEnabled, Is.True);
            Assert.That(host.Session.Document.StateEquals(host.Before), Is.True);
            Assert.That(host.Session.UndoCount, Is.Zero);
        });
    }

    private static T Control<T>(Window window, string name) where T : Avalonia.Controls.Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static EditorDocument ResourceDocument()
    {
        var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 };
        var assetId = Guid.NewGuid();
        document.AddAsset(assetId, P4AutomaticBackgroundTests.Solid(0xFFFF0000));
        document.Layers.Add(new Layer
        {
            Kind = LayerKind.Image, Name = "资源图层", AssetId = assetId,
            Bounds = new RectD(20, 20, 200, 120)
        });
        document.Validate();
        return document;
    }

    private static EditorDocument SongDocument()
    {
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        var group = Guid.NewGuid();
        var asset = Guid.NewGuid();
        document.AddAsset(asset, TestImages.EncodePng(20, 10,
            TestImages.SyntheticBgra(20, 10)));
        var artwork = new Layer
        {
            Kind = LayerKind.Image, Name = "歌曲 · artwork", GroupId = group, AssetId = asset,
            Bounds = new RectD(40, 40, 200, 120)
        };
        var title = new Layer
        {
            Kind = LayerKind.Text, Name = "歌曲 · title", Text = "原标题", GroupId = group,
            Bounds = new RectD(40, 180, 240, 40)
        };
        var artist = new Layer
        {
            Kind = LayerKind.Text, Name = "歌曲 · artist", Text = "原曲师", GroupId = group,
            Bounds = new RectD(40, 220, 240, 35)
        };
        var charter = new Layer
        {
            Kind = LayerKind.Text, Name = "歌曲 · charter", Text = "原谱师", GroupId = group,
            Bounds = new RectD(40, 255, 240, 30)
        };
        var badge = EditorDocument.NewBadge(new PointD(260, 40));
        badge.Name = "歌曲 · difficulty";
        badge.GroupId = group;
        document.Layers.AddRange([artwork, title, artist, charter, badge]);
        document.Validate();
        return document;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.That(condition(), Is.True, "The expected dialog/picker operation did not complete.");
    }

    private sealed class PickerHost : IDisposable
    {
        private readonly IDisposable _scope;
        public RecordingFactory Factory { get; } = new();
        public EditSession Session { get; }
        public EditorDocument Before { get; }
        public MainWindow Window { get; }

        public PickerHost(Func<Task<string?>>? directoryPicker = null,
            Func<Task<IReadOnlyList<string>>>? projectPicker = null,
            EditorDocument? document = null)
        {
            AvaloniaTestHost.EnsureStarted();
            // Avalonia keeps these test-service hooks in its runtime assembly but omits them
            // from its ref API. Scope the substitution instead of mutating individual windows.
            var locatorType = typeof(AvaloniaLocator);
            _scope = (IDisposable)locatorType.GetMethod("EnterScope")!.Invoke(null, null)!;
            var locator = locatorType.GetProperty("CurrentMutable")!.GetValue(null)!;
            var registration = locatorType.GetMethod("Bind")!
                .MakeGenericMethod(typeof(IStorageProviderFactory)).Invoke(locator, null)!;
            registration.GetType().GetMethod("ToConstant")!
                .MakeGenericMethod(typeof(RecordingFactory)).Invoke(registration, [Factory]);
            Session = new EditSession(document ?? new EditorDocument
            {
                CanvasWidth = 640, CanvasHeight = 360,
                Layers = [new Layer { Kind = LayerKind.Shape, Bounds = new RectD(30, 40, 160, 100) }]
            });
            Session.SetSelection(Session.Document.Layers.Select(layer => layer.Id));
            Before = Session.Document.Copy();
            Window = new MainWindow(Session, exportDirectoryPicker: directoryPicker,
                batchProjectPathsPicker: projectPicker);
            Window.Show();
        }

        public T Modal<T>() where T : Window
        {
            PumpUntil(() => Window.OwnedWindows.OfType<T>().Any());
            return Window.OwnedWindows.OfType<T>().Single();
        }

        public void Dispose()
        {
            Session.MarkSaved();
            foreach (var dialog in Window.OwnedWindows.ToArray()) dialog.Close();
            Window.Close();
            _scope.Dispose();
        }
    }

    private sealed class RecordingFactory : IStorageProviderFactory
    {
        public List<TopLevel> Calls { get; } = [];
        public IStorageProvider CreateProvider(TopLevel topLevel)
        {
            Calls.Add(topLevel);
            // Avalonia 12 deliberately reserves IStorageProvider implementations. Reuse its
            // built-in cancelled-picker provider while recording the public factory's owner.
            var type = typeof(IStorageProvider).Assembly.GetType(
                "Avalonia.Platform.Storage.NoopStorageProvider", throwOnError: true)!;
            return (IStorageProvider)Activator.CreateInstance(type, nonPublic: true)!;
        }
    }
}
