using System.Diagnostics;
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

public sealed class R1BatchImageImportTests
{
    [Test]
    public void DecodeAndRenderOneGeneratedPngDoesNotHang()
    {
        AvaloniaTestHost.EnsureStarted();
        var folder = ShortFolder();
        try
        {
            var path = Path.Combine(folder, "one.png");
            File.WriteAllBytes(path,
                TestImages.EncodePng(24, 12, TestImages.SyntheticBgra(24, 12)));
            var bytes = File.ReadAllBytes(path);
            using var bitmap = TestImages.Decoder.Decode(new AssetBlob(bytes));
            Assert.That(bitmap.PixelSize, Is.EqualTo(new Avalonia.PixelSize(24, 12)));
            var document = new EditorDocument
            {
                CanvasWidth = 320, CanvasHeight = 180,
                Layers =
                [
                    new Layer
                    {
                        Kind = LayerKind.Image, AssetId = Guid.NewGuid(),
                        Bounds = new RectD(10, 10, 24, 12)
                    }
                ]
            };
            document.AddAsset(document.Layers[0].AssetId!.Value, bytes);
            Assert.That(TestImages.RenderPng(document), Is.Not.Empty);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    public async Task EverySuccessfulBatchIsOneUndoableOrderedImport(int count)
    {
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1200, CanvasHeight = 800 });
        var before = edit.Document.Copy();
        var sources = new ProtectedSourcePaths();
        var blobs = Enumerable.Range(0, count).Select(index =>
            new AssetBlob(TestImages.EncodePng(40 + index, 20, TestImages.SyntheticBgra(40 + index, 20)))).ToArray();
        var workflow = new BatchImageImportWorkflow(edit, path =>
        {
            var index = int.Parse(Path.GetFileNameWithoutExtension(path));
            return Task.FromResult(new LoadedImage(blobs[index], 40 + index, 20));
        }, TestImages.ValidateImages, sources, () => null);
        var paths = Enumerable.Range(0, count).Select(index =>
            Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{index}.png")).ToArray();
        var progress = new List<BatchImageImportProgress>();
        var imported = await workflow.ImportAsync(() => Task.FromResult<IReadOnlyList<string>>(paths),
            new SynchronousProgress<BatchImageImportProgress>(progress.Add));
        var after = edit.Document.Copy();
        Assert.That(imported, Is.EqualTo(count));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(after.Layers.Select(layer => layer.Id).ToArray()));
        Assert.That(after.Layers.Select(layer => layer.Name), Is.EqualTo(paths.Select(Path.GetFileName)));
        Assert.That(after.Layers.Select(layer => layer.Bounds.X),
            Is.EqualTo(Enumerable.Range(0, count).Select(index => 90 + 12.0 * index)));
        Assert.That(after.Layers.Select(layer => layer.Bounds.Y),
            Is.EqualTo(Enumerable.Range(0, count).Select(index => 90 + 12.0 * index)));
        Assert.That(progress.Select(item => item.Completed), Is.EqualTo(Enumerable.Range(1, count)));
        foreach (var path in paths) Assert.That(sources.Contains(path), Is.True);
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(sources.Count, Is.EqualTo(count), "successful source protection outlives undo");
        edit.Redo();
        Assert.That(edit.Document.StateEquals(after), Is.True);
    }

    [Test]
    public async Task IdenticalBytesShareOneAssetWhileEveryLayerKeepsItsOwnIdentity()
    {
        var edit = new EditSession(new EditorDocument());
        var payload = new AssetBlob(TestImages.EncodePng(8, 8, TestImages.SyntheticBgra(8, 8)));
        var workflow = new BatchImageImportWorkflow(edit, _ =>
            Task.FromResult(new LoadedImage(payload, 8, 8)), TestImages.ValidateImages, new(), () => null);
        await workflow.ImportAsync(() => Task.FromResult<IReadOnlyList<string>>(["a.png", "b.png", "c.png"]));
        Assert.That(edit.Document.Assets.Values.Select(asset => asset.Sha256).Distinct().Count(), Is.EqualTo(1));
        Assert.That(edit.Document.Layers.Select(layer => layer.AssetId).Distinct().Count(), Is.EqualTo(3));
        Assert.That(edit.Document.Assets.Values.All(asset => asset.ContentEquals(payload)), Is.True);
    }

    [TestCase("limit")]
    [TestCase("asset-limit")]
    [TestCase("last-file")]
    [TestCase("decode")]
    public void FailureNeverPartialImportsOrRegistersSources(string failure)
    {
        var edit = Session();
        var before = edit.Document.Copy();
        var selection = edit.SelectedLayerIds.ToArray();
        var sources = new ProtectedSourcePaths();
        var reads = 0;
        if (failure == "limit")
        {
            edit.Document.Layers.AddRange(Enumerable.Range(0, 199).Select(_ =>
                new Layer { Kind = LayerKind.Shape }));
        }
        if (failure == "asset-limit")
        {
            for (var index = 0; index < 199; index++) edit.Document.AddAsset(Guid.NewGuid(), [(byte)(index % 250 + 1)]);
        }
        var baseline = edit.Document.Copy();
        var workflow = new BatchImageImportWorkflow(edit, path =>
        {
            reads++;
            if (failure == "last-file" && reads == 2) throw new IOException("locked");
            if (failure == "decode" && reads == 2) throw new InvalidDataException("corrupt");
            return Task.FromResult(new LoadedImage(new AssetBlob(Enumerable.Repeat((byte)(reads + 40), 32).ToArray()), 10, 10));
        }, _ => { }, sources, () => null);
        Assert.That(async () => await workflow.ImportAsync(() =>
            Task.FromResult<IReadOnlyList<string>>(["one.png", "two.png"])), Throws.Exception);
        Assert.That(edit.Document.StateEquals(baseline), Is.True);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(sources.Count, Is.Zero);
        if (failure == "limit") Assert.That(reads, Is.Zero);
        if (failure == "asset-limit") Assert.That(reads, Is.EqualTo(2));
    }

    [Test]
    public void CancellationBeforeOrBetweenReadsNeverCommits()
    {
        var edit = Session();
        var before = edit.Document.Copy();
        var sources = new ProtectedSourcePaths();
        var reads = 0;
        var workflow = new BatchImageImportWorkflow(edit, _ =>
        {
            reads++;
            return Task.FromResult(new LoadedImage(new AssetBlob([(byte)(reads + 1)]), 10, 10));
        }, _ => { }, sources, () => null);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.That(async () => await workflow.ImportAsync(() =>
            Task.FromResult<IReadOnlyList<string>>(["a.png", "b.png"]), cancellationToken: cancel.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(reads, Is.Zero);
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(sources.Count, Is.Zero);
    }

    [Test]
    public async Task ChangedProjectDuringReadRejectsTheWholeBatch()
    {
        var edit = Session();
        var before = edit.Document.Copy();
        var sources = new ProtectedSourcePaths();
        var reads = 0;
        var workflow = new BatchImageImportWorkflow(edit, _ =>
        {
            reads++;
            if (reads == 2) edit.UpdateDocument(document => document.BackgroundArgb = 0xFF123456);
            return Task.FromResult(new LoadedImage(new AssetBlob([(byte)(reads + 5)]), 10, 10));
        }, _ => { }, sources, () => null);
        Assert.That(async () => await workflow.ImportAsync(() =>
            Task.FromResult<IReadOnlyList<string>>(["a.png", "b.png"])),
            Throws.InstanceOf<InvalidOperationException>());
        Assert.That(edit.Document.Layers.Count, Is.EqualTo(before.Layers.Count));
        Assert.That(edit.Document.Find(edit.Document.Layers[0].Id).StateEquals(before.Layers[0]), Is.True);
        Assert.That(sources.Count, Is.Zero);
    }

    [Test]
    public async Task RetryAfterFailureCommitsOnlyTheSuccessfulBatch()
    {
        var edit = Session();
        var sources = new ProtectedSourcePaths();
        var fail = true;
        var workflow = new BatchImageImportWorkflow(edit, path =>
        {
            if (fail && path.EndsWith("b.png", StringComparison.Ordinal)) throw new IOException("locked");
            return Task.FromResult(new LoadedImage(new AssetBlob([1, 2, 3]), 20, 10));
        }, _ => { }, sources, () => null);
        Assert.That(async () => await workflow.ImportAsync(() =>
            Task.FromResult<IReadOnlyList<string>>(["a.png", "b.png"])), Throws.Exception);
        Assert.That(edit.Document.Layers, Has.Count.EqualTo(1));
        Assert.That(sources.Count, Is.Zero);
        fail = false;
        Assert.That(await workflow.ImportAsync(() =>
            Task.FromResult<IReadOnlyList<string>>(["a.png", "b.png"])), Is.EqualTo(2));
        Assert.That(edit.Document.Layers, Has.Count.EqualTo(3));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(sources.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task RealFilesSaveReopenAndRenderTheSameComposition()
    {
        AvaloniaTestHost.EnsureStarted();
        var folder = ShortFolder();
        try
        {
            var paths = Enumerable.Range(0, 2).Select(index =>
                Path.Combine(folder, $"图片 {index}.png")).ToArray();
            for (var index = 0; index < paths.Length; index++)
            {
                var width = 24 + index;
                File.WriteAllBytes(paths[index],
                    TestImages.EncodePng(width, 12, TestImages.SyntheticBgra(width, 12)));
            }
            var edit = new EditSession(new EditorDocument { CanvasWidth = 900, CanvasHeight = 600 });
            var workflow = new BatchImageImportWorkflow(edit, path =>
            {
                // Keep this service test synchronous: Avalonia's headless setup can install a
                // synchronization context that only the GUI dispatcher pumps.
                var bytes = File.ReadAllBytes(path);
                using var bitmap = TestImages.Decoder.Decode(new AssetBlob(bytes));
                return Task.FromResult(new LoadedImage(
                    new AssetBlob(bytes), bitmap.PixelSize.Width, bitmap.PixelSize.Height));
            }, TestImages.ValidateImages, new(), () => null);
            Assert.That(await workflow.ImportAsync(() =>
                Task.FromResult<IReadOnlyList<string>>(paths)), Is.EqualTo(2));
            var pixels = TestImages.RenderPng(edit.Document);
            var project = Path.Combine(folder, "batch.ppgproj");
            NativePackageStore.Save(project, edit.Document, "r1-batch-import");
            Assert.That(TestImages.RenderPng(NativePackageStore.Read(project)), Is.EqualTo(pixels));
            Assert.That(edit.Document.Layers.Select(layer => layer.Name),
                Is.EqualTo(paths.Select(Path.GetFileName)));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public void DesktopBatchEntryImportsRealFilesAsOneEdit()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var folder = ShortFolder();
        var edit = new EditSession(new EditorDocument { CanvasWidth = 900, CanvasHeight = 600 });
        var window = new MainWindow(edit, batchImagePathsPicker: () =>
            Task.FromResult<IReadOnlyList<string>>(Directory.GetFiles(folder).OrderBy(path => path).ToArray()));
        try
        {
            for (var index = 0; index < 3; index++)
                File.WriteAllBytes(Path.Combine(folder, $"desktop-{index}.png"),
                    TestImages.EncodePng(16 + index * 4, 12, TestImages.SyntheticBgra(16 + index * 4, 12)));
            window.Show();
            UiTestActions.ClickNamed(window, "ImportImagesBatch");
            var until = DateTime.UtcNow.AddSeconds(10);
            while (edit.UndoCount == 0 && DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Assert.That(edit.Document.Layers, Has.Count.EqualTo(3));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.SelectedLayerIds, Has.Count.EqualTo(3));
            Assert.That(window.OwnedWindows, Is.Empty);
        }
        finally
        {
            edit.MarkSaved();
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void DesktopCancelDuringReadLeavesTheDocumentUntouched()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var folder = ShortFolder();
        var edit = new EditSession(new EditorDocument());
        var window = new MainWindow(edit,
            batchImagePathsPicker: () => Task.FromResult<IReadOnlyList<string>>(
                Directory.GetFiles(folder).OrderBy(path => path).ToArray()),
            imageLoader: async path =>
            {
                await Task.Delay(path.EndsWith("slow.png", StringComparison.Ordinal) ? 700 : 10);
                return new LoadedImage(new AssetBlob([1, 2, 3]), 20, 10);
            });
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "fast.png"), [1]);
            File.WriteAllBytes(Path.Combine(folder, "slow.png"), [2]);
            window.Show();
            UiTestActions.ClickNamed(window, "ImportImagesBatch");
            var until = DateTime.UtcNow.AddSeconds(5);
            BatchImageProgressDialog? dialog = null;
            while (dialog is null && DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
                dialog = window.OwnedWindows.OfType<BatchImageProgressDialog>().FirstOrDefault();
            }
            Assert.That(dialog, Is.Not.Null);
            UiTestActions.ClickNamed(dialog!, "CancelBatchImport");
            until = DateTime.UtcNow.AddSeconds(5);
            while (window.OwnedWindows.OfType<BatchImageProgressDialog>().Any() && DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Assert.That(edit.Document.Layers, Is.Empty);
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
        }
        finally
        {
            edit.MarkSaved();
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void LongUnicodeNamesTruncateOnlyTheAutomaticLayerName()
    {
        var stem = string.Concat(Enumerable.Repeat("曲绘😀", 120));
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, stem + ".png");
        var name = ImageLayerNames.FromPath(path);
        Assert.That(name.Length, Is.LessThanOrEqualTo(200));
        Assert.That(char.IsHighSurrogate(name[^1]), Is.False);
        Assert.That(ImageLayerNames.FromPath("short.png"), Is.EqualTo("short.png"));
    }

    private static EditSession Session()
    {
        var existing = new Layer { Kind = LayerKind.Shape, Name = "existing" };
        return new EditSession(new EditorDocument
        {
            CanvasWidth = 900, CanvasHeight = 600, Layers = [existing]
        }) { SelectedLayerId = existing.Id };
    }

    private static string ShortFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "r1i-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
