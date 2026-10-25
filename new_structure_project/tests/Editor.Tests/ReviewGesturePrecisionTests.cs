using Editor.Application;
using Editor.Core;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class ReviewGesturePrecisionTests
{
    [TestCase(ResizeHandle.TopLeft)] [TestCase(ResizeHandle.Top)]
    [TestCase(ResizeHandle.TopRight)] [TestCase(ResizeHandle.Right)]
    [TestCase(ResizeHandle.BottomRight)] [TestCase(ResizeHandle.Bottom)]
    [TestCase(ResizeHandle.BottomLeft)] [TestCase(ResizeHandle.Left)]
    public void ReturningAResizeToItsStartingPointKeepsExactBoundsAndHistory(ResizeHandle handle)
    {
        var first = FractionalLayer();
        first.RotationDegrees = 33;
        var edit = new EditSession(new EditorDocument { Layers = [first] });
        var original = edit.Document.Copy();
        edit.BeginGesture(first.Id);
        edit.ResizeFromHandle(17, 31, handle);
        Assert.That(edit.Document.StateEquals(original), Is.False);
        edit.ResizeFromHandle(0, 0, handle);
        edit.CommitGesture();
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.StateEquals(original), Is.True,
                "Returning to the starting point must not re-round persistent coordinates.");
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
        });
    }

    [TestCase(0)] [TestCase(360)] [TestCase(-720)]
    public void ReturningGroupRotationToAFullTurnRestoresExactState(double degrees)
    {
        var first = FractionalLayer(); first.RotationDegrees = 33;
        var second = FractionalLayer(); second.Bounds = new(350.1, 225.3, 70.7, 41.9);
        var edit = new EditSession(new EditorDocument { Layers = [first, second] });
        edit.SetSelection([first.Id, second.Id]);
        var original = edit.Document.Copy();
        edit.BeginGesture(first.Id);
        edit.RotateBy(29);
        Assert.That(edit.Document.StateEquals(original), Is.False);
        edit.RotateBy(degrees);
        edit.CommitGesture();
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.StateEquals(original), Is.True);
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
        });
    }

    [TestCase(-180)] [TestCase(-33.5)] [TestCase(45)] [TestCase(180)]
    public void RotatingOneLayerKeepsItsUnrotatedSavedRectangleExact(double degrees)
    {
        var layer = FractionalLayer(); layer.RotationDegrees = 33;
        var edit = new EditSession(new EditorDocument { Layers = [layer] });
        edit.BeginGesture(layer.Id);
        edit.RotateBy(degrees);
        edit.CommitGesture();
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(layer.Id).Bounds, Is.EqualTo(layer.Bounds));
            Assert.That(edit.Document.Find(layer.Id).RotationDegrees,
                Is.EqualTo(LayerGeometry.NormalizeDegrees(33 + degrees)));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Find(layer.Id).StateEquals(layer), Is.True);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(layer.Id).Bounds, Is.EqualTo(layer.Bounds));
    }

    private static Layer FractionalLayer() => new()
    {
        Kind = LayerKind.Shape,
        Bounds = new(0.1, 200.3, 200, 100.2)
    };
}
