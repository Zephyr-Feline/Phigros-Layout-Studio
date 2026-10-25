using Editor.Application;
using Editor.Core;
using NUnit.Framework;
using Avalonia;

namespace Editor.Tests;

public sealed class CoreAndEditTests
{
    [TestCase(1920, 1080, 900, 600, 1.0)]
    [TestCase(2740, 1080, 800, 500, 1.75)]
    [TestCase(640, 640, 1200, 700, 0.5)]
    public void ViewportForwardInverseArePaired(double width, double height, double viewWidth,
        double viewHeight, double zoom)
    {
        var transform = new ViewportTransform(width, height, viewWidth, viewHeight, zoom,
            new PointD(37, -21));
        var point = new PointD(width * 0.36, height * 0.83);
        var roundTrip = transform.ToLogical(transform.ToView(point));
        Assert.Multiple(() =>
        {
            Assert.That(roundTrip.X, Is.EqualTo(point.X).Within(1e-9));
            Assert.That(roundTrip.Y, Is.EqualTo(point.Y).Within(1e-9));
        });
    }

    [Test]
    public void BadgeAspectIsValidated()
    {
        var doc = new EditorDocument();
        var badge = EditorDocument.NewBadge(new PointD(10, 20));
        doc.Layers.Add(badge);
        Assert.DoesNotThrow(doc.Validate);
        badge.Bounds = badge.Bounds with { Height = 100 };
        Assert.Throws<InvalidDataException>(doc.Validate);
    }

    [Test]
    public void MoveAndResizeEachProduceOneHistoryEntry()
    {
        var doc = new EditorDocument();
        var badge = EditorDocument.NewBadge(new PointD(10, 20));
        doc.Layers.Add(badge);
        var edit = new EditSession(doc);

        edit.BeginGesture(badge.Id);
        edit.MoveBy(20, 30);
        edit.MoveBy(40, 60);
        edit.CommitGesture();
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.Document.Find(badge.Id).Bounds.X, Is.EqualTo(50));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Find(badge.Id).Bounds.X, Is.EqualTo(10));
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(badge.Id).Bounds.X, Is.EqualTo(50));

        edit.BeginGesture(badge.Id);
        edit.ResizeBy(30, 4);
        edit.ResizeBy(64, 50);
        edit.CommitGesture();
        Assert.That(edit.UndoCount, Is.EqualTo(2));
        Assert.That(edit.Document.Find(badge.Id).Bounds.Width, Is.EqualTo(192));
        Assert.That(edit.Document.Find(badge.Id).Bounds.Height,
            Is.EqualTo(192 / EditorDocument.BadgeRatio).Within(1e-9));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Find(badge.Id).Bounds.Width, Is.EqualTo(128));
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(badge.Id).Bounds.Width, Is.EqualTo(192));
    }

    [Test]
    public void NewEditClearsRedoAndSelectionIsSessionOnly()
    {
        var doc = new EditorDocument();
        var layer = new Layer { Kind = LayerKind.Text, Text = "中文", Bounds = new RectD(1, 2, 180, 60) };
        doc.Layers.Add(layer);
        var edit = new EditSession(doc) { SelectedLayerId = layer.Id };
        edit.BeginGesture(layer.Id);
        edit.MoveBy(5, 0);
        edit.CommitGesture();
        edit.Undo();
        Assert.That(edit.RedoCount, Is.EqualTo(1));

        edit.BeginGesture(layer.Id);
        edit.MoveBy(9, 0);
        edit.CommitGesture();
        Assert.That(edit.RedoCount, Is.Zero);
        Assert.That(edit.Document.Layers[0].Id, Is.EqualTo(layer.Id));
        Assert.That(edit.SelectedLayerId, Is.EqualTo(layer.Id));
    }

    [TestCase(1.0)]
    [TestCase(0.75)]
    [TestCase(1.25)]
    [TestCase(1.5)]
    [TestCase(1.75)]
    [TestCase(2.0)]
    [TestCase(2.5)]
    public void PreviewMatrixAndInputTransformStayPairedAcrossRenderScale(double renderScale)
    {
        var transform = new ViewportTransform(2740, 1080, 1100, 700, 1.4, new PointD(35, -12));
        var logical = new PointD(1030, 670);
        var view = transform.ToView(logical);
        var matrix = Matrix.CreateScale(transform.Scale, transform.Scale) *
                     Matrix.CreateTranslation(transform.Origin.X, transform.Origin.Y);
        var painted = matrix.Transform(new Point(logical.X, logical.Y));
        var logicalThreshold = transform.ScreenPixelsToLogical(8, renderScale);
        Assert.Multiple(() =>
        {
            Assert.That(painted.X, Is.EqualTo(view.X).Within(1e-9));
            Assert.That(painted.Y, Is.EqualTo(view.Y).Within(1e-9));
            Assert.That(transform.ToLogical(view).X, Is.EqualTo(logical.X).Within(1e-9));
            Assert.That(logicalThreshold * transform.Scale * renderScale, Is.EqualTo(8).Within(1e-9));
        });
    }

    [Test]
    public void UndoImageImportRemovesAssetAndClearsStaleSelection()
    {
        var edit = new EditSession(new EditorDocument());
        var assetId = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = assetId,
            Bounds = new RectD(0, 0, 80, 40) };
        edit.AddImageLayer(image, [1, 2, 3]);
        Assert.That(edit.SelectedLayerId, Is.EqualTo(image.Id));
        Assert.That(edit.Undo(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Layers, Is.Empty);
            Assert.That(edit.Document.Assets, Is.Empty);
            Assert.That(edit.SelectedLayerId, Is.Null);
        });
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Assets[assetId].ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }
}
