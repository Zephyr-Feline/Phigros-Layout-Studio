using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5SongBatchTests
{
    [TestCase(3, 1920, 1080)]
    [TestCase(3, 1440, 1080)]
    [TestCase(5, 2740, 1080)]
    [TestCase(5, 1440, 1080)]
    [TestCase(5, 320, 180)]
    [TestCase(5, 1080, 1920)]
    public void BuildsIndependentSixLayerSongsInExistingTemplateSlots(int count, int width, int height)
    {
        var blob = new AssetBlob([1]);
        var doc = SongBatch.Create(width, height, Enumerable.Range(0, count)
            .Select(i => new SongArtwork($"歌曲 {i}", blob)).ToArray());
        doc.Validate();
        Assert.That(doc.Layers.Count, Is.EqualTo(count * 6));
        Assert.That(doc.Assets.Count, Is.EqualTo(1));
        var groups = doc.Layers.GroupBy(l => l.GroupId).ToArray();
        Assert.That(groups.Length, Is.EqualTo(count));
        var template = LayoutTemplates.All.Single(t => t.Id == (count == 3 ? "three-columns" : "five-point"));
        for (var i = 0; i < count; i++)
        {
            var layers = groups[i].ToArray(); var slot = template.Slots[i];
            Assert.That(layers.Select(l => l.Kind), Is.EqualTo(new[] { LayerKind.Shape, LayerKind.Image,
                LayerKind.Text, LayerKind.Text, LayerKind.Text, LayerKind.Badge }));
            Assert.That(layers[2].Text, Is.EqualTo($"歌曲 {i}"));
            Assert.That(layers[1].Slanted && layers[1].KeepAspect, Is.True);
            Assert.That(layers[5].Bounds.Width / layers[5].Bounds.Height, Is.EqualTo(EditorDocument.BadgeRatio).Within(1e-10));
            var box = MoveSnapping.BoundsOf(layers.Select(l => l.Bounds));
            Assert.That(box.X, Is.GreaterThanOrEqualTo(slot.X * width - 1e-8));
            Assert.That(box.Y, Is.GreaterThanOrEqualTo(slot.Y * height - 1e-8));
            Assert.That(box.X + box.Width, Is.LessThanOrEqualTo((slot.X + slot.Width) * width + 1e-8));
            Assert.That(box.Y + box.Height, Is.LessThanOrEqualTo((slot.Y + slot.Height) * height + 1e-8));
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(6)]
    public void WrongFileCountRejectsBeforeReading(int count)
    {
        var edit = new EditSession(new EditorDocument());
        var workflow = new SongBatchWorkflow(edit, _ => throw new AssertionException("Unexpected read"),
            _ => Assert.Fail("Unexpected validation"), new());
        Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ImportAsync(() =>
            Task.FromResult<IReadOnlyList<string>>(Enumerable.Range(0, count).Select(i => $"{i}.png").ToArray())));
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [TestCase(3)]
    [TestCase(5)]
    public async Task BatchIsOneUndoAndPreservesExistingContentAndSourceProtection(int count)
    {
        var edit = Session(); var before = edit.Document.Copy(); var paths = new ProtectedSourcePaths();
        var blob = Picture(); var names = Enumerable.Range(0, count).Select(i => $"歌曲 {i}.png").ToArray();
        var workflow = new SongBatchWorkflow(edit, _ => Task.FromResult(blob), TestImages.ValidateImages, paths);
        Assert.That(await workflow.ImportAsync(() => Task.FromResult<IReadOnlyList<string>>(names)), Is.EqualTo(count));
        var after = edit.Document.Copy();
        Assert.That(edit.UndoCount, Is.EqualTo(1)); Assert.That(edit.SelectedLayerIds.Count, Is.EqualTo(count * 6));
        Assert.That(edit.Document.Layers[0].StateEquals(before.Layers[0]), Is.True);
        Assert.That(edit.Document.BackgroundBlend, Is.EqualTo(before.BackgroundBlend));
        Assert.That(edit.Document.BackgroundArgb, Is.EqualTo(before.BackgroundArgb));
        Assert.That(edit.Document.ExtensionData, Is.EqualTo(before.ExtensionData));
        foreach (var name in names) Assert.That(paths.Contains(name), Is.True);
        edit.Undo(); Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(edit.IsDirty, Is.False);
        edit.Redo(); Assert.That(edit.Document.StateEquals(after), Is.True);
        var file = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".ppgproj");
        try
        {
            NativePackageStore.Save(file, after, "test");
            Assert.That(TestImages.RenderPng(NativePackageStore.Read(file)), Is.EqualTo(TestImages.RenderPng(after)));
        }
        finally { File.Delete(file); }
    }

    [TestCase("read")]
    [TestCase("decode")]
    [TestCase("limit")]
    public void FailureNeverLeavesPartialCardsPathsOrHistory(string failure)
    {
        var edit = Session(); var id = edit.Document.Layers[0].Id;
        edit.UpdateLayer(id, l => l.Name = "redo"); edit.Undo();
        if (failure == "limit") edit.Document.Layers.AddRange(Enumerable.Range(0, 190).Select(_ => new Layer { Kind = LayerKind.Shape }));
        var before = edit.Document.Copy(); var sources = new ProtectedSourcePaths(); var reads = 0;
        var workflow = new SongBatchWorkflow(edit, _ =>
        {
            reads++; if (failure == "read" && reads == 2) throw new IOException("missing image");
            return Task.FromResult(new AssetBlob([1]));
        }, _ => { if (failure == "decode") throw new InvalidDataException("corrupt image"); }, sources);
        Assert.That(async () => await workflow.ImportAsync(() => Task.FromResult<IReadOnlyList<string>>(["a.png", "b.png", "c.png"])), Throws.Exception);
        Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(edit.SelectedLayerId, Is.EqualTo(id));
        Assert.That(edit.UndoCount, Is.Zero); Assert.That(edit.RedoCount, Is.EqualTo(1)); Assert.That(sources.Count, Is.Zero);
        if (failure == "limit") Assert.That(reads, Is.Zero);
    }

    [TestCase("picker")]
    [TestCase("load")]
    [TestCase("replace")]
    public void ChangedDocumentDuringAsyncInputIsNotModifiedByBatch(string stage)
    {
        var edit = Session(); var blob = new AssetBlob([1]);
        var workflow = new SongBatchWorkflow(edit, _ =>
        {
            if (stage == "load") edit.UpdateDocument(d => d.BackgroundArgb = 0xFF123456);
            return Task.FromResult(blob);
        }, _ => { }, new());
        Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ImportAsync(() =>
        {
            if (stage == "picker") edit.UpdateDocument(d => d.BackgroundArgb = 0xFF123456);
            if (stage == "replace") edit.ReplaceDocument(edit.Document.Copy());
            return Task.FromResult<IReadOnlyList<string>>(["a.png", "b.png", "c.png"]);
        }));
        Assert.That(edit.Document.Layers.Count, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CancelOrActiveGestureDoesNotRead(bool gesture)
    {
        var edit = Session();
        if (gesture) { edit.Document.Layers[0].Locked = false; edit.BeginGesture(edit.Document.Layers[0].Id); }
        var workflow = new SongBatchWorkflow(edit, _ => throw new AssertionException("Unexpected read"), _ => { }, new());
        Task<int> Run() => workflow.ImportAsync(() =>
        {
            if (gesture) Assert.Fail("Unexpected picker");
            return Task.FromResult<IReadOnlyList<string>>([]);
        });
        if (gesture) { Assert.ThrowsAsync<InvalidOperationException>(Run); edit.CancelGesture(); }
        else Assert.That(Run().GetAwaiter().GetResult(), Is.Zero);
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void DesktopBuildsThreeSongsFromRealFiles()
    {
        AvaloniaTestHost.EnsureStarted(); var edit = Session();
        var files = Enumerable.Range(0, 3).Select(i => Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"曲绘 {i}-{Guid.NewGuid():N}.png")).ToArray();
        var theme = new Avalonia.Themes.Fluent.FluentTheme(); Avalonia.Application.Current!.Styles.Add(theme);
        var window = new MainWindow(edit, songImagePathsPicker: () => Task.FromResult<IReadOnlyList<string>>(files));
        try
        {
            foreach (var path in files) File.WriteAllBytes(path, Picture().ToArray());
            window.Show();
            UiTestActions.Click(UiTestActions.Find(window, "Build 3 / 5 songs"));
            // Local reads are asynchronous; pump the UI until the operation completes.
            var until = DateTime.UtcNow.AddSeconds(10);
            while (edit.UndoCount == 0 && DateTime.UtcNow < until)
            { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(19)); Assert.That(edit.UndoCount, Is.EqualTo(1));
            foreach (var path in files) Assert.That(File.ReadAllBytes(path), Is.EqualTo(Picture().ToArray()));
        }
        finally { edit.MarkSaved(); window.Close(); Avalonia.Application.Current!.Styles.Remove(theme); foreach (var path in files) File.Delete(path); }
    }

    private static EditSession Session()
    {
        var doc = new EditorDocument { CanvasWidth = 1440, CanvasHeight = 1080, BackgroundBlend = new(true),
            Layers = [new Layer { Kind = LayerKind.Shape, Name = "existing", Locked = true, Visible = false }] };
        doc.ExtensionData["keep"] = "original";
        var edit = new EditSession(doc); edit.SelectedLayerId = doc.Layers[0].Id; return edit;
    }
    private static AssetBlob Picture() => new(TestImages.RenderPng(new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 }));
}
