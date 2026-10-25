using System.Text.Json;
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

public sealed class R1PngExportQueueTests
{
    [Test]
    public void AtomicWriteNewNeverReplacesAnExistingFile()
    {
        var folder = ParentFolder();
        try
        {
            var path = Path.Combine(folder, "new.png");
            AtomicFile.WriteNew(path, stream => stream.Write([1, 2, 3]));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(() => AtomicFile.WriteNew(path, stream => stream.Write([9])),
                Throws.InstanceOf<IOException>());
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(Directory.GetFiles(folder, "*.tmp"), Is.Empty);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public void NormalizeValidatesAndDeduplicatesTasks()
    {
        var doc = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 };
        var tasks = new[] { new PngExportTask(320, false), new PngExportTask(320, false),
            new PngExportTask(320, true) };
        Assert.That(PngExportQueue.Normalize(doc, tasks, true), Has.Count.EqualTo(2));
        Assert.That(() => PngExportQueue.Normalize(doc, tasks, false),
            Throws.InstanceOf<InvalidOperationException>());
        Assert.That(() => PngExportQueue.Normalize(doc, [new PngExportTask(0, false)], true),
            Throws.InstanceOf<InvalidDataException>());
        Assert.That(() => PngExportQueue.Normalize(doc, [], true),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void RunDirectoryIsUniqueUnderTheChosenParent()
    {
        var parent = ParentFolder();
        try
        {
            var stamp = new DateTime(2026, 10, 17, 12, 34, 56);
            var first = PngExportQueue.CreateRunDirectory(parent, stamp);
            Directory.CreateDirectory(first);
            var second = PngExportQueue.CreateRunDirectory(parent, stamp);
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(Path.GetDirectoryName(first), Is.EqualTo(Path.GetFullPath(parent)));
            Assert.That(Path.GetFileName(first), Does.StartWith("PhigrosExport-20261017-123456"));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task QueueWritesEveryTaskFromOneSnapshotAndLeavesTheDocumentIntact(bool transparent)
    {
        var doc = new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, BackgroundArgb = 0xFF102030,
            Layers = [new Layer { Kind = LayerKind.Shape, Bounds = new RectD(20, 30, 180, 80),
                ColorArgb = 0xFF00FF00 }]
        };
        var before = doc.Copy();
        var parent = ParentFolder();
        try
        {
            var tasks = new[]
            {
                new PngExportTask(320, transparent), new PngExportTask(480, transparent),
                new PngExportTask(640, transparent)
            };
            var expected = tasks.ToDictionary(task => (task.Width, task.Transparent),
                task => TestImages.RenderPng(doc, task.Transparent, task.Width));
            var directory = PngExportQueue.CreateRunDirectory(parent, DateTime.Now);
            var result = await PngExportQueue.ExecuteAsync(doc, tasks, directory,
                TestImages.ValidateImages,
                (document, mode, width) =>
                {
                    Assert.That(document.StateEquals(before), Is.True);
                    return expected[(width!.Value, mode)];
                },
                (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
                new ProtectedSourcePaths(), () => null).ConfigureAwait(false);
            Assert.That(result.Cancelled, Is.False);
            Assert.That(result.Items.Select(item => item.Status),
                Is.All.EqualTo(PngExportTaskStatus.Completed));
            Assert.That(result.ManifestError, Is.Null);
            foreach (var task in tasks)
            {
                var path = Path.Combine(directory, task.FileName);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(expected[(task.Width, task.Transparent)]));
                var size = PngExportSize.Create(doc, task.Width);
                var (width, height, _) = TestImages.DecodePng(File.ReadAllBytes(path));
                Assert.That((width, height), Is.EqualTo((size.Width, size.Height)));
            }
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
            Assert.That(manifest.RootElement.GetProperty("formatVersion").GetInt32(), Is.EqualTo(2));
            Assert.That(manifest.RootElement.GetProperty("tasks").GetArrayLength(), Is.EqualTo(3));
            Assert.That(manifest.RootElement.GetProperty("tasks").EnumerateArray()
                .Select(item => item.GetProperty("status").GetString()),
                Is.All.EqualTo("Completed"));
            Assert.That(doc.StateEquals(before), Is.True);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Test]
    public async Task CancellationBetweenTasksKeepsCompletedFilesAndListsCancelledAndNotRun()
    {
        var doc = SmallDocument();
        var parent = ParentFolder();
        try
        {
            var tasks = new[]
            {
                new PngExportTask(320, false), new PngExportTask(400, false),
                new PngExportTask(480, false)
            };
            var expected = tasks.ToDictionary(task => (task.Width, task.Transparent),
                task => TestImages.RenderPng(doc, task.Transparent, task.Width));
            var directory = PngExportQueue.CreateRunDirectory(parent, DateTime.Now);
            using var cancel = new CancellationTokenSource();
            var renders = 0;
            var result = await PngExportQueue.ExecuteAsync(doc, tasks, directory,
                TestImages.ValidateImages,
                (_, mode, width) =>
                {
                    renders++;
                    if (renders == 2) cancel.Cancel();
                    return expected[(width!.Value, mode)];
                },
                (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
                new ProtectedSourcePaths(), () => null, cancellationToken: cancel.Token)
                .ConfigureAwait(false);
            Assert.That(result.Cancelled, Is.True);
            Assert.That(result.Items.Select(item => item.Status), Is.EqualTo(new[]
            {
                PngExportTaskStatus.Completed, PngExportTaskStatus.Cancelled,
                PngExportTaskStatus.NotRun
            }));
            Assert.That(File.Exists(Path.Combine(directory, tasks[0].FileName)), Is.True);
            Assert.That(File.Exists(Path.Combine(directory, tasks[1].FileName)), Is.False);
            Assert.That(File.Exists(Path.Combine(directory, tasks[2].FileName)), Is.False);
            Assert.That(File.Exists(result.ManifestPath), Is.True);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Test]
    public async Task OneWriteFailureDoesNotStopLaterTasksOrLeaveAPartialFile()
    {
        var doc = SmallDocument();
        var parent = ParentFolder();
        try
        {
            var tasks = new[]
            {
                new PngExportTask(320, false), new PngExportTask(320, true),
                new PngExportTask(480, false)
            };
            var expected = tasks.ToDictionary(task => (task.Width, task.Transparent),
                task => TestImages.RenderPng(doc, task.Transparent, task.Width));
            var directory = PngExportQueue.CreateRunDirectory(parent, DateTime.Now);
            var result = await PngExportQueue.ExecuteAsync(doc, tasks, directory,
                TestImages.ValidateImages,
                (_, mode, width) => expected[(width!.Value, mode)],
                (path, bytes) =>
                {
                    if (path.EndsWith("-transparent.png", StringComparison.Ordinal))
                        throw new IOException("simulated disk failure");
                    AtomicFile.WriteNew(path, stream => stream.Write(bytes));
                },
                new ProtectedSourcePaths(), () => null).ConfigureAwait(false);
            Assert.That(result.Items.Select(item => item.Status), Is.EqualTo(new[]
            {
                PngExportTaskStatus.Completed, PngExportTaskStatus.Failed,
                PngExportTaskStatus.Completed
            }));
            Assert.That(result.Items[1].Error, Does.Contain("simulated disk failure"));
            Assert.That(File.Exists(Path.Combine(directory, tasks[0].FileName)), Is.True);
            Assert.That(File.Exists(Path.Combine(directory, tasks[1].FileName)), Is.False);
            Assert.That(File.Exists(Path.Combine(directory, tasks[2].FileName)), Is.True);
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Test]
    public void SourceOrProjectConflictAndInvalidTasksAreRejectedBeforeAnyPng()
    {
        var doc = SmallDocument();
        var parent = ParentFolder();
        try
        {
            var directory = PngExportQueue.CreateRunDirectory(parent, DateTime.Now);
            var planned = Path.Combine(directory, new PngExportTask(320, false).FileName);
            var sources = new ProtectedSourcePaths();
            sources.Add(planned);
            var renders = 0;
            var writes = 0;
            Assert.That(async () => await PngExportQueue.ExecuteAsync(doc,
                [new PngExportTask(320, false)], directory, TestImages.ValidateImages,
                (_, _, _) => { renders++; return [1]; },
                (_, _) => writes++, sources, () => null).ConfigureAwait(false),
                Throws.InstanceOf<InvalidOperationException>());
            Assert.That((renders, writes), Is.EqualTo((0, 0)));
            Assert.That(Directory.Exists(directory), Is.False);

            Assert.That(async () => await PngExportQueue.ExecuteAsync(doc,
                [new PngExportTask(320, false)], directory, TestImages.ValidateImages,
                (_, _, _) => { renders++; return [1]; }, (_, _) => writes++,
                new ProtectedSourcePaths(), () => planned).ConfigureAwait(false),
                Throws.InstanceOf<InvalidOperationException>());
            Assert.That(Directory.Exists(directory), Is.False);

            Assert.That(async () => await PngExportQueue.ExecuteAsync(doc,
                [new PngExportTask(0, false)], directory, TestImages.ValidateImages,
                (_, _, _) => { renders++; return [1]; }, (_, _) => writes++,
                new ProtectedSourcePaths(), () => null).ConfigureAwait(false),
                Throws.InstanceOf<InvalidDataException>());
            Assert.That((renders, writes), Is.EqualTo((0, 0)));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Test]
    public void QueueDialogShowsFilesAndWarningsAndReturnsTheSelectedPlan()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var owner = new Window { Width = 300, Height = 200 };
        var parent = ParentFolder();
        var doc = new EditorDocument { CanvasWidth = 800, CanvasHeight = 600 };
        var warnings = new[]
        {
            new ExportWarning(ExportWarningKind.HiddenLayer, Guid.NewGuid(), "曲绘",
                "Hidden; omitted from PNG.")
        };
        var dialog = new PngExportQueueDialog(doc, warnings,
            () => Task.FromResult<string?>(parent));
        try
        {
            owner.Show();
            var pending = dialog.ShowDialog<PngExportQueueRequest?>(owner);
            dialog.UpdateLayout();
            Assert.That(dialog.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "PngQueueTaskList").Items, Has.Count.EqualTo(1));
            Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "PngQueueWarning").Text, Does.Contain("预检提示 1 项"));
            var width = dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "PngQueueWidth");
            width.Text = "800";
            UiTestActions.ClickNamed(dialog, "AddPngQueueTask");
            var list = dialog.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "PngQueueTaskList");
            Assert.That(list.Items, Has.Count.EqualTo(2));
            Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "PngQueueError").Text, Does.Contain("重复"));
            UiTestActions.ClickNamed(dialog, "DeduplicatePngQueueTasks");
            Assert.That(list.Items, Has.Count.EqualTo(1));
            UiTestActions.ClickNamed(dialog, "ChoosePngQueueDirectory");
            PumpUntil(() => dialog.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "StartPngQueueExport").IsEnabled);
            UiTestActions.ClickNamed(dialog, "StartPngQueueExport");
            PumpUntil(() => pending.IsCompleted);
            var request = pending.GetAwaiter().GetResult();
            Assert.That(request, Is.Not.Null);
            Assert.That(request!.Tasks, Has.Count.EqualTo(1));
            Assert.That(request.Tasks[0].Width, Is.EqualTo(800));
            Assert.That(request.ParentDirectory, Is.EqualTo(Path.GetFullPath(parent)));
        }
        finally
        {
            foreach (var window in owner.OwnedWindows.ToArray()) window.Close();
            owner.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Test]
    public void ProgressDialogListsCompletedFailedAndCancelledStatusesAndTheManifest()
    {
        var dialog = new PngExportProgressDialog();
        var size = new PngExportSize(320, 180, 1, 0);
        var result = new PngExportQueueResult("C:\\输出", [
            new PngExportTaskResult(new PngExportTask(320, false), "preview-320.png", size,
                PngExportTaskStatus.Completed, null),
            new PngExportTaskResult(new PngExportTask(320, true), "preview-320-transparent.png", size,
                PngExportTaskStatus.Failed, "写入失败"),
            new PngExportTaskResult(new PngExportTask(480, false), "preview-480.png", size,
                PngExportTaskStatus.Cancelled, null)
        ], "C:\\输出\\export-manifest.json", true);
        dialog.Complete(result);
        Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
            .Single(block => block.Name == "PngQueueProgressText").Text,
            Does.Contain("成功 1，失败 1，取消/未执行 1").And.Contain("C:\\输出"));
        var items = dialog.GetLogicalDescendants().OfType<ListBox>()
            .Single(box => box.Name == "PngQueueResultList").Items.Cast<string>().ToArray();
        Assert.That(items, Has.Some.Matches<string>(item => item.Contains("成功", StringComparison.Ordinal)));
        Assert.That(items, Has.Some.Matches<string>(item => item.Contains("失败", StringComparison.Ordinal)));
        Assert.That(items, Has.Some.Matches<string>(item => item.Contains("已取消", StringComparison.Ordinal)));
        Assert.That(items, Has.Some.Matches<string>(item =>
            item.Contains("export-manifest.json", StringComparison.Ordinal)));
        dialog.Close();
    }

    [Test]
    public void DesktopBatchExportRunsTheQueueAndShowsTheResultList()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var parent = ParentFolder();
        var edit = new EditSession(SmallDocument());
        var window = new MainWindow(edit,
            exportDirectoryPicker: () => Task.FromResult<string?>(parent));
        try
        {
            window.Show();
            var draft = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerName");
            draft.Text = "批量导出期间保留的草稿";
            UiTestActions.ClickNamed(window, "BatchExportPng");
            PumpUntil(() => window.OwnedWindows.OfType<PngExportQueueDialog>().Any());
            var setup = window.OwnedWindows.OfType<PngExportQueueDialog>().Single();
            var width = setup.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "PngQueueWidth");
            width.Text = "480";
            setup.GetLogicalDescendants().OfType<ComboBox>()
                .Single(box => box.Name == "PngQueueMode").SelectedIndex = 1;
            UiTestActions.ClickNamed(setup, "AddPngQueueTask");
            Assert.That(setup.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "PngQueueTaskCount").Text, Is.EqualTo("任务数：2"));
            UiTestActions.ClickNamed(setup, "ChoosePngQueueDirectory");
            PumpUntil(() => setup.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "StartPngQueueExport").IsEnabled);
            UiTestActions.ClickNamed(setup, "StartPngQueueExport");
            PumpUntil(() => window.OwnedWindows.OfType<PngExportProgressDialog>().Any());
            var progress = window.OwnedWindows.OfType<PngExportProgressDialog>().Single();
            PumpUntil(() => progress.Result is not null);
            PumpUntil(() => progress.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "PngQueueProgressText").Text?
                .Contains("成功 2", StringComparison.Ordinal) == true);
            var result = progress.Result!;
            Assert.That(result.Items.Select(item => item.Status),
                Is.All.EqualTo(PngExportTaskStatus.Completed));
            Assert.That(result.Items, Has.Count.EqualTo(2));
            Assert.That(result.Items.Select(item => File.Exists(Path.Combine(result.Directory, item.FileName))),
                Is.All.True);
            Assert.That(File.Exists(result.ManifestPath), Is.True);
            Assert.That(edit.IsDirty, Is.False);
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(draft.Text, Is.EqualTo("批量导出期间保留的草稿"));
            Assert.That(progress.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "PngQueueProgressText").Text, Does.Contain("成功 2"));
            UiTestActions.ClickNamed(progress, "ClosePngQueueProgress");
            PumpUntil(() => window.OwnedWindows.Count == 0);
            PumpUntil(() => UiTestActions.Status(window)?.Contains("批量导出完成", StringComparison.Ordinal) == true);
        }
        finally
        {
            edit.MarkSaved();
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            Directory.Delete(parent, recursive: true);
        }
    }

    private static EditorDocument SmallDocument() => new()
    {
        CanvasWidth = 640, CanvasHeight = 360,
        Layers = [new Layer { Kind = LayerKind.Shape, Bounds = new RectD(20, 20, 100, 80) }]
    };

    private static string ParentFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "r1q-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(condition(), Is.True, "The UI operation did not complete.");
    }
}
