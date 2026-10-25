using Editor.Application;
using Editor.Core;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5ImageWorkflowTests
{
    [TestCase(ImageImportAction.Add)]
    [TestCase(ImageImportAction.Replace)]
    [TestCase(ImageImportAction.Background)]
    public async Task SuccessfulImportPreservesGeometryAndIsOneUndoableEdit(ImageImportAction action)
    {
        var edit = Session(); var before = edit.Document.Copy(); var id = edit.SelectedLayerId!.Value;
        var sources = new ProtectedSourcePaths();
        var workflow = Workflow(edit, sources, _ => Task.FromResult(new LoadedImage(new([2]), 1200, 400)));
        Assert.That(await workflow.ImportAsync(action, () => Task.FromResult<string?>("新 曲绘.png")), Is.True);
        var after = edit.Document.Copy();
        Assert.That(edit.UndoCount, Is.EqualTo(1)); Assert.That(edit.IsDirty, Is.True);
        Assert.That(sources.Contains("新 曲绘.png"), Is.True);
        if (action == ImageImportAction.Add)
        {
            var added = after.Layers.Last();
            Assert.That(added.Bounds, Is.EqualTo(new RectD(90, 90, 600, 200)));
            Assert.That(added.Name, Is.EqualTo("新 曲绘.png"));
            Assert.That(after.Find(id).StateEquals(before.Find(id)), Is.True);
        }
        else
        {
            Assert.That(after.Layers.Select(l => l.Id), Is.EqualTo(before.Layers.Select(l => l.Id)));
            Assert.That(after.Find(id).Bounds, Is.EqualTo(before.Find(id).Bounds));
            Assert.That(after.Find(id).GroupId, Is.EqualTo(before.Find(id).GroupId));
            Assert.That(after.Find(id).Visible, Is.EqualTo(before.Find(id).Visible));
            Assert.That(after.Find(id).Locked, Is.EqualTo(before.Find(id).Locked));
        }
        edit.Undo(); Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(edit.IsDirty, Is.False);
        edit.Redo(); Assert.That(edit.Document.StateEquals(after), Is.True);
    }

    [TestCase(ImageImportAction.Add, "picker")]
    [TestCase(ImageImportAction.Add, "load")]
    [TestCase(ImageImportAction.Add, "replacement")]
    [TestCase(ImageImportAction.Replace, "picker")]
    [TestCase(ImageImportAction.Replace, "load")]
    [TestCase(ImageImportAction.Replace, "replacement")]
    [TestCase(ImageImportAction.Background, "picker")]
    [TestCase(ImageImportAction.Background, "load")]
    [TestCase(ImageImportAction.Background, "replacement")]
    public void ChangedProjectRejectsTheImageWithoutPathsOrHistory(ImageImportAction action, string stage)
    {
        var edit = Session(); var sources = new ProtectedSourcePaths(); var reads = 0;
        void Change()
        {
            if (stage == "replacement") edit.ReplaceDocument(edit.Document.Copy());
            else edit.UpdateDocument(d => d.BackgroundArgb = 0xFF112233);
        }
        var workflow = Workflow(edit, sources, _ =>
        {
            reads++; if (stage == "load") Change();
            return Task.FromResult(new LoadedImage(new([2]), 40, 20));
        });
        Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ImportAsync(action, () =>
        {
            if (stage != "load") Change();
            return Task.FromResult<string?>("image.png");
        }));
        Assert.That(sources.Count, Is.Zero); Assert.That(edit.Document.Assets.Values.Single().ToArray(), Is.EqualTo(new byte[] { 1 }));
        Assert.That(edit.UndoCount, Is.EqualTo(stage == "replacement" ? 0 : 1));
        Assert.That(reads, Is.EqualTo(stage == "load" ? 1 : 0));
    }

    [TestCase(ImageImportAction.Add)]
    [TestCase(ImageImportAction.Replace)]
    [TestCase(ImageImportAction.Background)]
    public void SaveTargetChangeDuringReadingRejectsTheOldRequest(ImageImportAction action)
    {
        var edit = Session(); var before = edit.Document.Copy(); var sources = new ProtectedSourcePaths();
        var path = "first.ppgproj";
        var workflow = new ImageImportWorkflow(edit, _ =>
        {
            path = "second.ppgproj"; return Task.FromResult(new LoadedImage(new([2]), 40, 20));
        }, sources, () => path);
        Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ImportAsync(action,
            () => Task.FromResult<string?>("image.png")));
        Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(sources.Count, Is.Zero);
    }

    [TestCase(ImageImportAction.Add, false)]
    [TestCase(ImageImportAction.Replace, false)]
    [TestCase(ImageImportAction.Background, false)]
    [TestCase(ImageImportAction.Add, true)]
    [TestCase(ImageImportAction.Replace, true)]
    [TestCase(ImageImportAction.Background, true)]
    public void ReadOrDecodeFailurePreservesRedoAndAllAssets(ImageImportAction action, bool decode)
    {
        var edit = Session(); var id = edit.SelectedLayerId!.Value;
        edit.UpdateLayer(id, l => l.Name = "redo"); edit.Undo();
        var before = edit.Document.Copy(); var sources = new ProtectedSourcePaths();
        var workflow = Workflow(edit, sources, _ => decode
            ? throw new InvalidDataException("corrupt image") : throw new IOException("read failed"));
        Assert.That(async () => await workflow.ImportAsync(action, () => Task.FromResult<string?>("bad.png")), Throws.Exception);
        Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(edit.RedoCount, Is.EqualTo(1)); Assert.That(sources.Count, Is.Zero);
    }

    [TestCase(ImageImportAction.Add, false)]
    [TestCase(ImageImportAction.Replace, false)]
    [TestCase(ImageImportAction.Background, false)]
    [TestCase(ImageImportAction.Add, true)]
    [TestCase(ImageImportAction.Replace, true)]
    [TestCase(ImageImportAction.Background, true)]
    public void ActiveGestureBeforeOrDuringReadingNeverCommits(ImageImportAction action, bool duringLoad)
    {
        var edit = Session(); var sources = new ProtectedSourcePaths(); var calls = 0;
        var id = edit.SelectedLayerId!.Value;
        var workflow = Workflow(edit, sources, _ =>
        {
            edit.BeginGesture(id); edit.MoveBy(30, 40);
            return Task.FromResult(new LoadedImage(new([2]), 40, 20));
        });
        if (!duringLoad) edit.BeginGesture(id);
        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ImportAsync(action, () =>
            {
                calls++; return Task.FromResult<string?>("image.png");
            }));
            Assert.That(calls, Is.EqualTo(duringLoad ? 1 : 0)); Assert.That(sources.Count, Is.Zero);
            Assert.That(edit.UndoCount, Is.Zero);
        }
        finally { edit.CancelGesture(); }
    }

    [TestCase(ImageImportAction.Add)]
    [TestCase(ImageImportAction.Replace)]
    [TestCase(ImageImportAction.Background)]
    public async Task CancellationDoesNotRead(ImageImportAction action)
    {
        var edit = Session(); var before = edit.Document.Copy();
        var workflow = Workflow(edit, new(), _ => throw new AssertionException("Unexpected read"));
        Assert.That(await workflow.ImportAsync(action, () => Task.FromResult<string?>(null)), Is.False);
        Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(edit.UndoCount, Is.Zero);
    }

    [TestCase(ImageImportAction.Add)]
    [TestCase(ImageImportAction.Replace)]
    [TestCase(ImageImportAction.Background)]
    public async Task SelectionChangeOnlyInvalidatesReplacement(ImageImportAction action)
    {
        var edit = Session(); var sources = new ProtectedSourcePaths();
        var workflow = Workflow(edit, sources, _ =>
        {
            edit.SelectedLayerId = null; return Task.FromResult(new LoadedImage(new([2]), 40, 20));
        });
        Task<bool> Run() => workflow.ImportAsync(action, () => Task.FromResult<string?>("image.png"));
        if (action == ImageImportAction.Replace)
        {
            Assert.ThrowsAsync<InvalidOperationException>(Run); Assert.That(sources.Count, Is.Zero);
        }
        else Assert.That(await Run(), Is.True);
    }

    [Test]
    public void LayerLimitFailureRollsBackImageAndSourceProtection()
    {
        var edit = Session();
        edit.Document.Layers.AddRange(Enumerable.Range(0, 199).Select(_ => new Layer { Kind = LayerKind.Shape }));
        var before = edit.Document.Copy(); var sources = new ProtectedSourcePaths();
        var workflow = Workflow(edit, sources, _ => Task.FromResult(new LoadedImage(new([2]), 40, 20)));
        Assert.ThrowsAsync<InvalidDataException>(() => workflow.ImportAsync(ImageImportAction.Add,
            () => Task.FromResult<string?>("image.png")));
        Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(sources.Count, Is.Zero);
        Assert.That(edit.UndoCount, Is.Zero);
    }

    private static ImageImportWorkflow Workflow(EditSession edit, ProtectedSourcePaths sources,
        Func<string, Task<LoadedImage>> load) => new(edit, load, sources, () => null);
    private static EditSession Session()
    {
        var image = new Layer { Kind = LayerKind.Image, Name = "existing", AssetId = Guid.NewGuid(),
            GroupId = Guid.NewGuid(), Bounds = new(13, 27, 240, 120), Visible = false };
        var document = new EditorDocument { CanvasWidth = 1440, CanvasHeight = 1080, Layers = [image] };
        document.AddAsset(image.AssetId!.Value, [1]);
        return new(document) { SelectedLayerId = image.Id };
    }
}
