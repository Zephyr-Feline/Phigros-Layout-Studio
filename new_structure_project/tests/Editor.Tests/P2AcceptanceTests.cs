using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P2AcceptanceTests
{
    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p2-acceptance",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void SavepointTracksUndoRedoBranchNoOpAndFailedEdit()
    {
        var layer = new Layer { Kind = LayerKind.Text, Text = "one" };
        var edit = new EditSession(new EditorDocument());
        Assert.That(edit.IsDirty, Is.False);
        edit.AddLayer(layer);
        Assert.That(edit.IsDirty, Is.True);
        edit.MarkSaved();
        Assert.That(edit.IsDirty, Is.False);
        edit.UpdateLayer(layer.Id, item => item.Text = "two");
        Assert.That(edit.IsDirty, Is.True);
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.IsDirty, Is.False);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.IsDirty, Is.True);
        var undoCount = edit.UndoCount;
        edit.UpdateLayer(layer.Id, item => item.Text = "two");
        Assert.That(edit.UndoCount, Is.EqualTo(undoCount));
        Assert.Throws<InvalidDataException>(() => edit.UpdateLayer(layer.Id,
            item => item.Bounds = item.Bounds with { Width = double.NaN }));
        Assert.That(edit.UndoCount, Is.EqualTo(undoCount));
        Assert.That(edit.Document.Find(layer.Id).Text, Is.EqualTo("two"));
        edit.Undo();
        edit.UpdateLayer(layer.Id, item => item.Text = "branch");
        Assert.That(edit.RedoCount, Is.Zero);
        Assert.That(edit.IsDirty, Is.True);
    }

    [Test]
    public void HistoryDepthIsBoundedAndCurrentStateSurvivesEviction()
    {
        var edit = new EditSession(new EditorDocument(), maxHistoryEntries: 3);
        var layer = new Layer { Kind = LayerKind.Text, Text = "0" };
        edit.AddLayer(layer);
        for (var i = 1; i <= 8; i++) edit.UpdateLayer(layer.Id, item => item.Text = i.ToString());
        Assert.That(edit.UndoCount, Is.EqualTo(3));
        for (var i = 0; i < 3; i++) Assert.That(edit.Undo(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(layer.Id).Text, Is.EqualTo("5"));
            Assert.That(edit.Undo(), Is.False);
        });
        for (var i = 0; i < 3; i++) Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(layer.Id).Text, Is.EqualTo("8"));
        Assert.That(edit.Redo(), Is.False);
    }

    [Test]
    public void GestureCancelNoOpReorderAndDocumentMetadataHaveCorrectTransactions()
    {
        var edit = new EditSession(new EditorDocument());
        var first = new Layer { Kind = LayerKind.Text, Name = "first" };
        var second = new Layer { Kind = LayerKind.Shape, Name = "second" };
        edit.AddLayer(first);
        edit.AddLayer(second);
        var count = edit.UndoCount;
        edit.BeginGesture(first.Id);
        edit.MoveBy(50, 20);
        edit.CancelGesture();
        Assert.That(edit.UndoCount, Is.EqualTo(count));
        Assert.That(edit.Document.Find(first.Id).Bounds.X, Is.Zero);
        edit.BeginGesture(first.Id);
        edit.MoveBy(0, 0);
        edit.CommitGesture();
        Assert.That(edit.UndoCount, Is.EqualTo(count));
        edit.MoveLayerToIndex(first.Id, 1);
        Assert.That(edit.Document.Layers[1].Id, Is.EqualTo(first.Id));
        edit.UpdateDocument(doc => doc.BackgroundArgb = 0xFF112233);
        Assert.That(edit.Document.BackgroundArgb, Is.EqualTo(0xFF112233));
        edit.Undo();
        Assert.That(edit.Document.BackgroundArgb, Is.EqualTo(0xFF233B47));
        edit.Undo();
        Assert.That(edit.Document.Layers[0].Id, Is.EqualTo(first.Id));
        edit.Redo();
        Assert.That(edit.Document.Layers[1].Id, Is.EqualTo(first.Id));
        edit.ReplaceDocument(new EditorDocument());
        Assert.Multiple(() =>
        {
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.RedoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
        });
    }

    [Test]
    public void NativeWorkflowSaveOpenSaveAsAndFailedOpenPreserveStateAndHandles()
    {
        var edit = new EditSession(new EditorDocument());
        var workflow = new NativeProjectWorkflow(edit, _ => { }, "p2-test");
        var first = Path.Combine(_directory, "first native 工程");
        var secondDirectory = Path.Combine(_directory, "different directory 空 格");
        Directory.CreateDirectory(secondDirectory);
        var second = Path.Combine(secondDirectory, "另存 #2.project");
        var layer = new Layer { Kind = LayerKind.Text, Text = "初始" };
        edit.AddLayer(layer);
        workflow.SaveAs(first);
        Assert.That(workflow.IsDirty, Is.False);
        Assert.That(workflow.CurrentPath, Is.EqualTo(Path.GetFullPath(first)));
        edit.UpdateLayer(layer.Id, item => item.Text = "changed");
        Assert.That(workflow.IsDirty, Is.True);
        workflow.Save();
        Assert.That(workflow.IsDirty, Is.False);
        edit.UpdateLayer(layer.Id, item => item.Text = "again");
        workflow.SaveAs(second);
        Assert.That(workflow.CurrentPath, Is.EqualTo(Path.GetFullPath(second)));
        Assert.That(NativePackageStore.Read(second).Find(layer.Id).Text, Is.EqualTo("again"));

        edit.UpdateLayer(layer.Id, item => item.Text = "unsaved");
        var undo = edit.UndoCount;
        var bad = Path.Combine(_directory, "corrupt.package");
        File.WriteAllText(bad, "not a zip");
        Assert.Throws<InvalidDataException>(() => workflow.Open(bad));
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(layer.Id).Text, Is.EqualTo("unsaved"));
            Assert.That(edit.UndoCount, Is.EqualTo(undo));
            Assert.That(workflow.IsDirty, Is.True);
            Assert.That(workflow.CurrentPath, Is.EqualTo(Path.GetFullPath(second)));
        });
        workflow.Open(first);
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(layer.Id).Text, Is.EqualTo("changed"));
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(workflow.IsDirty, Is.False);
        });
        var moved = Path.Combine(_directory, "released.package");
        File.Move(first, moved); // reader must have released its ZIP/file handle.
        File.Move(moved, first);
        workflow.New();
        Assert.That(workflow.CurrentPath, Is.Null);
        Assert.That(workflow.IsDirty, Is.False);
        workflow.SaveAs(first);
        edit.UpdateDocument(doc => doc.BackgroundArgb = 0xFF445566);
        workflow.MarkOtherFormatSaved();
        Assert.Multiple(() =>
        {
            Assert.That(workflow.CurrentPath, Is.Null);
            Assert.That(workflow.IsDirty, Is.False);
        });
    }

    [Test]
    public void NativeSaveRejectsActiveGestureBeforeWritingEitherTarget()
    {
        var edit = new EditSession(new EditorDocument());
        var workflow = new NativeProjectWorkflow(edit, _ => { }, "p2-test");
        var layer = new Layer { Kind = LayerKind.Text, Text = "before" };
        edit.AddLayer(layer);
        var current = Path.Combine(_directory, "current.ppgproj");
        var other = Path.Combine(_directory, "other.ppgproj");
        workflow.SaveAs(current);
        var original = File.ReadAllBytes(current);
        var undoCount = edit.UndoCount;

        edit.BeginGesture(layer.Id);
        edit.MoveBy(80, 20);
        Assert.Throws<InvalidOperationException>(workflow.Save);
        Assert.Throws<InvalidOperationException>(() => workflow.SaveAs(other));
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(current), Is.EqualTo(original));
            Assert.That(File.Exists(other), Is.False);
            Assert.That(workflow.CurrentPath, Is.EqualTo(Path.GetFullPath(current)));
            Assert.That(edit.UndoCount, Is.EqualTo(undoCount));
            Assert.That(edit.IsDirty, Is.False);
        });

        edit.CancelGesture();
        Assert.That(edit.Document.Find(layer.Id).Bounds.X, Is.Zero);
        edit.BeginGesture(layer.Id);
        edit.MoveBy(80, 20);
        edit.CommitGesture();
        workflow.Save();
        Assert.That(NativePackageStore.Read(current).Find(layer.Id).Bounds.X, Is.EqualTo(80));
        Assert.That(edit.IsDirty, Is.False);
    }

    [Test]
    [Platform("Win")]
    public void FailedNativeSaveKeepsSavepointAndPreviousPackage()
    {
        var edit = new EditSession(new EditorDocument());
        var workflow = new NativeProjectWorkflow(edit, _ => { }, "p2-test");
        var path = Path.Combine(_directory, "locked.package");
        workflow.SaveAs(path);
        var before = File.ReadAllBytes(path);
        edit.UpdateDocument(doc => doc.BackgroundArgb = 0xFF010203);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(workflow.Save);
        Assert.Multiple(() =>
        {
            Assert.That(workflow.IsDirty, Is.True);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(NativePackageStore.Read(path).BackgroundArgb, Is.EqualTo(0xFF233B47));
        });
    }

    [TestCase(1920, 1080, 900, 600, 1.0)]
    [TestCase(2740, 1080, 800, 500, 2.5)]
    [TestCase(640, 640, 1200, 700, 0.6)]
    public void GeometrySurvivesZoomPanGestureUndoRedoAndNativeRoundTrip(
        int canvasW, int canvasH, int viewW, int viewH, double zoom)
    {
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(11.25, 19.5, 200.75, 91.125) };
        var doc = new EditorDocument { CanvasWidth = canvasW, CanvasHeight = canvasH };
        doc.Layers.Add(layer);
        var edit = new EditSession(doc);
        var origin = edit.Document.Find(layer.Id).Bounds;
        var view = new ViewportTransform(canvasW, canvasH, viewW, viewH, zoom, new PointD(30, -18));
        var corner = view.ToView(new PointD(origin.X, origin.Y));
        var logical = view.ToLogical(corner);
        Assert.That(logical.X, Is.EqualTo(origin.X).Within(1e-9));
        Assert.That(logical.Y, Is.EqualTo(origin.Y).Within(1e-9));
        Assert.That(edit.Document.Find(layer.Id).Bounds, Is.EqualTo(origin));
        edit.BeginGesture(layer.Id);
        edit.MoveBy(70.25, -8.5);
        edit.CommitGesture();
        edit.BeginGesture(layer.Id);
        edit.ResizeBy(33.125, 17.25);
        edit.CommitGesture();
        var transformed = edit.Document.Find(layer.Id).Bounds;
        var path = Path.Combine(_directory, "geometry.package");
        NativePackageStore.Save(path, edit.Document, "p2-test");
        Assert.That(NativePackageStore.Read(path).Find(layer.Id).Bounds, Is.EqualTo(transformed));
        edit.Undo();
        edit.Undo();
        Assert.That(edit.Document.Find(layer.Id).Bounds, Is.EqualTo(origin));
        edit.Redo();
        edit.Redo();
        Assert.That(edit.Document.Find(layer.Id).Bounds, Is.EqualTo(transformed));
    }
}
