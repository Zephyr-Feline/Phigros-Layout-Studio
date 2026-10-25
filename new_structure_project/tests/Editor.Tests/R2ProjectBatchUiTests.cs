using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R2ProjectBatchUiTests
{
    private string _folder = null!;

    [SetUp]
    public void SetUp()
    {
        AvaloniaTestHost.EnsureStarted();
        _folder = Path.Combine(Path.GetTempPath(), "r2-batch-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Test]
    public void DialogCollectsProjectsTasksAndParentWithoutTouchingADocument()
    {
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var owner = new Window();
        var first = Project("first", 640, 360);
        var second = Project("second", 800, 600);
        var parent = Directory.CreateDirectory(Path.Combine(_folder, "out")).FullName;
        var dialog = new ProjectBatchRenderDialog(
            () => Task.FromResult<IReadOnlyList<string>>([first, second]),
            () => Task.FromResult<string?>(parent));
        try
        {
            owner.Show();
            var pending = dialog.ShowDialog<ProjectBatchRenderRequest?>(owner);
            dialog.UpdateLayout();
            UiTestActions.ClickNamed(dialog, "ChooseProjectBatchProjects");
            PumpUntil(() => dialog.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "ProjectBatchProjectList").Items.Count == 2);
            UiTestActions.ClickNamed(dialog, "ChooseProjectBatchDirectory");
            PumpUntil(() => dialog.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "StartProjectBatch").IsEnabled);
            UiTestActions.ClickNamed(dialog, "StartProjectBatch");
            PumpUntil(() => pending.IsCompleted);
            var request = pending.GetAwaiter().GetResult();
            Assert.That(request, Is.Not.Null);
            Assert.That(request!.ProjectPaths, Is.EqualTo(new[] { first, second }));
            Assert.That(request.Tasks.Single().Width, Is.EqualTo(1920));
            Assert.That(request.OutputParent, Is.EqualTo(parent));
        }
        finally
        {
            foreach (var window in owner.OwnedWindows.ToArray()) window.Close();
            owner.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void DesktopCrossProjectQueueWritesEachProjectAndLeavesTheCurrentSessionAlone()
    {
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var first = Project("desk-a", 640, 360);
        var second = Project("desk-b", 480, 480);
        var parent = Directory.CreateDirectory(Path.Combine(_folder, "desk-out")).FullName;
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 });
        var before = edit.Document.Copy();
        var window = new MainWindow(edit, recovery: EmptyRecovery(edit),
            batchProjectPathsPicker: () => Task.FromResult<IReadOnlyList<string>>([first, second]),
            exportDirectoryPicker: () => Task.FromResult<string?>(parent));
        try
        {
            window.Show();
            UiTestActions.ClickNamed(window, "BatchRenderProjects");
            PumpUntil(() => window.OwnedWindows.OfType<ProjectBatchRenderDialog>().Any());
            var setup = window.OwnedWindows.OfType<ProjectBatchRenderDialog>().Single();
            UiTestActions.ClickNamed(setup, "ChooseProjectBatchProjects");
            PumpUntil(() => setup.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "ProjectBatchProjectList").Items.Count == 2);
            UiTestActions.ClickNamed(setup, "ChooseProjectBatchDirectory");
            PumpUntil(() => setup.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "StartProjectBatch").IsEnabled);
            UiTestActions.ClickNamed(setup, "StartProjectBatch");
            PumpUntil(() => window.OwnedWindows.OfType<ProjectBatchProgressDialog>().Any());
            var progress = window.OwnedWindows.OfType<ProjectBatchProgressDialog>().Single();
            PumpUntil(() => progress.Result is not null);
            Assert.That(progress.Result!.Projects.Select(project => project.Status),
                Is.All.EqualTo(ProjectRenderStatus.Completed));
            Assert.That(progress.Result.Projects.SelectMany(project => project.Tasks)
                .All(task => File.Exists(Path.Combine(progress.Result.Directory, task.FileName))), Is.True);
            Assert.That(edit.Document.StateEquals(before), Is.True);
            Assert.That(edit.IsDirty, Is.False);
            UiTestActions.ClickNamed(progress, "CloseProjectBatchProgress");
            PumpUntil(() => window.OwnedWindows.Count == 0);
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void SelectionQueueUsesSelectedGroupAndOriginalCanvasContract()
    {
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var group = Guid.NewGuid();
        var art = new Layer
        {
            Kind = LayerKind.Image, Name = "选中曲绘", GroupId = group,
            AssetId = Guid.NewGuid(), Bounds = new RectD(60, 50, 240, 160)
        };
        var hidden = new Layer
        {
            Kind = LayerKind.Text, Name = "隐藏组员", GroupId = group, Visible = false,
            Text = "hidden", Bounds = new RectD(80, 80, 120, 30)
        };
        var outside = new Layer { Kind = LayerKind.Shape, Name = "外部图层",
            Bounds = new RectD(600, 400, 100, 100) };
        var document = new EditorDocument
        {
            CanvasWidth = 1000, CanvasHeight = 600, BackgroundArgb = 0xFF445566,
            Layers = [art, hidden, outside]
        };
        document.AddAsset(art.AssetId!.Value,
            TestImages.EncodePng(24, 16, TestImages.SyntheticBgra(24, 16)));
        var edit = new EditSession(document);
        edit.SetSelection([art.Id]);
        var parent = Directory.CreateDirectory(Path.Combine(_folder, "selection-out")).FullName;
        var window = new MainWindow(edit, recovery: EmptyRecovery(edit),
            exportDirectoryPicker: () => Task.FromResult<string?>(parent));
        try
        {
            window.Show();
            UiTestActions.ClickNamed(window, "BatchExportSelectionPng");
            PumpUntil(() => window.OwnedWindows.OfType<PngExportQueueDialog>().Any());
            var setup = window.OwnedWindows.OfType<PngExportQueueDialog>().Single();
            UiTestActions.ClickNamed(setup, "ChoosePngQueueDirectory");
            PumpUntil(() => setup.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "StartPngQueueExport").IsEnabled);
            UiTestActions.ClickNamed(setup, "StartPngQueueExport");
            PumpUntil(() => window.OwnedWindows.OfType<PngExportProgressDialog>().Any());
            var progress = window.OwnedWindows.OfType<PngExportProgressDialog>().Single();
            PumpUntil(() => progress.Result is not null);
            var result = progress.Result!;
            Assert.That(result.Items.Single().Status, Is.EqualTo(PngExportTaskStatus.Completed));
            var png = File.ReadAllBytes(Path.Combine(result.Directory, result.Items.Single().FileName));
            var (width, height, _) = TestImages.DecodePng(png);
            Assert.That((width, height), Is.EqualTo((1000, 600)));
            Assert.That(edit.Document.StateEquals(document), Is.True);
            UiTestActions.ClickNamed(progress, "ClosePngQueueProgress");
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private string Project(string name, int width, int height)
    {
        var path = Path.Combine(_folder, name + ".ppgproj");
        NativePackageStore.Save(path, new EditorDocument
        {
            CanvasWidth = width, CanvasHeight = height,
            Layers = [new Layer { Kind = LayerKind.Text, Name = name, Text = name,
                Bounds = new RectD(20, 20, 160, 50) }]
        }, "r2-batch-ui");
        return path;
    }

    private static AutoRecoveryService EmptyRecovery(EditSession edit) =>
        new(edit, Path.Combine(Path.GetTempPath(), "r2-batch-recovery-" + Guid.NewGuid().ToString("N")[..8]),
            "r2-test", () => DateTimeOffset.Now, (_, _) => false);

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(condition(), Is.True, "UI operation did not complete.");
    }
}
