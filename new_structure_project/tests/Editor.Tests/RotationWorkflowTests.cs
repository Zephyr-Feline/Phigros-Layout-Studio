using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class RotationWorkflowTests
{
    [TestCase(0, ExportWarningKind.ClippedByCanvas)]
    [TestCase(40, null)]
    [TestCase(-150, ExportWarningKind.OutsideCanvas)]
    public void ExportPreflightChecksRotatedEdges(double x, ExportWarningKind? expected)
    {
        // At 90° this wide box extends 90 units above its unrotated top.
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new(x, 100, 240, 60), RotationDegrees = 90 };
        var doc = new EditorDocument { CanvasWidth = 640, CanvasHeight = 480, Layers = [layer] };
        if (x == 0) layer.Bounds = layer.Bounds with { Y = 20 };
        if (x == -150) layer.Bounds = layer.Bounds with { X = -240 };
        var before = doc.Copy();
        var warnings = ExportPreflight.Inspect(doc, _ => false);
        Assert.That(warnings.Select(w => w.Kind), Is.EqualTo(expected is null ? Array.Empty<ExportWarningKind>() : new[] { expected.Value }));
        Assert.That(doc.StateEquals(before), Is.True);
    }

    [TestCase(ArrangeAction.Left)]
    [TestCase(ArrangeAction.Right)]
    [TestCase(ArrangeAction.Top)]
    [TestCase(ArrangeAction.Bottom)]
    public void CanvasAlignmentUsesRotatedEdgesAndRemainsOneUndo(ArrangeAction action)
    {
        // A 120 × 60 rectangle at 90° occupies 60 × 120 around the same centre.
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new(100, 100, 120, 60), RotationDegrees = 90 };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 640, CanvasHeight = 480, Layers = [layer] });
        edit.SetSelection([layer.Id]);
        var before = edit.Document.Copy();
        edit.ArrangeSelection(action, ArrangeReference.Canvas, false);
        var actual = edit.Document.Find(layer.Id);
        var b = actual.Bounds;
        var visualLeft = b.X + 30; var visualTop = b.Y - 30;
        var edge = action switch
        {
            ArrangeAction.Left => visualLeft,
            ArrangeAction.Right => visualLeft + 60,
            ArrangeAction.Top => visualTop,
            _ => visualTop + 120
        };
        var expected = action switch { ArrangeAction.Right => 640, ArrangeAction.Bottom => 480, _ => 0 };
        Assert.That(edge, Is.EqualTo(expected).Within(1e-8));
        Assert.That(actual.RotationDegrees, Is.EqualTo(90));
        Assert.That((b.Width, b.Height), Is.EqualTo((120d, 60d)));
        edit.ArrangeSelection(action, ArrangeReference.Canvas, false);
        Assert.That(edit.UndoCount, Is.EqualTo(1), "Repeated alignment must not add floating-point history.");
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
        edit.Redo();
        Assert.That(edit.Document.Find(layer.Id).RotationDegrees, Is.EqualTo(90));
    }

    [Test]
    public void EqualGapsUseRotatedWidths()
    {
        var layers = new List<Layer>
        {
            new() { Kind = LayerKind.Shape, Bounds = new(40, 100, 120, 60), RotationDegrees = 90 },
            new() { Kind = LayerKind.Shape, Bounds = new(220, 100, 80, 40), RotationDegrees = -90 },
            new() { Kind = LayerKind.Shape, Bounds = new(460, 100, 100, 50), RotationDegrees = 90 }
        };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 640, CanvasHeight = 480, Layers = layers });
        edit.SetSelection(layers.Select(l => l.Id));
        edit.ArrangeSelection(ArrangeAction.HorizontalGaps, ArrangeReference.Canvas, false);
        var boxes = edit.Document.Layers.Select(l =>
            new RectD(l.Bounds.X + (l.Bounds.Width - l.Bounds.Height) / 2,
                l.Bounds.Y, l.Bounds.Height, l.Bounds.Width)).ToArray();
        Assert.That(boxes[0].X, Is.EqualTo(0).Within(1e-8));
        Assert.That(boxes[2].X + boxes[2].Width, Is.EqualTo(640).Within(1e-8));
        var expectedGap = (640 - 60 - 40 - 50) / 2d;
        for (var i = 1; i < boxes.Length; i++)
            Assert.That(boxes[i].X - boxes[i - 1].X - boxes[i - 1].Width,
                Is.EqualTo(expectedGap).Within(1e-8));
    }

    [Test]
    public void TemplateCaptureAndPlacementFitActualRotatedRectangle()
    {
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new(100, 100, 120, 60), RotationDegrees = 90 };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 640, CanvasHeight = 480, Layers = [layer] });
        var captured = edit.CaptureLayoutTemplate("旋转布局", [layer.Id]);
        var slot = captured.Slots.Single();
        Assert.That(slot.X, Is.EqualTo(130d / 640).Within(1e-8));
        Assert.That(slot.Y, Is.EqualTo(70d / 480).Within(1e-8));
        Assert.That(slot.Width, Is.EqualTo(60d / 640).Within(1e-8));
        Assert.That(slot.Height, Is.EqualTo(120d / 480).Within(1e-8));

        var small = new LayoutTemplate("small", "小框", [new(.5, .5, 30d / 640, 60d / 480)]);
        var before = edit.Document.Copy();
        edit.ApplyLayoutTemplate(small, [layer.Id]);
        var actual = edit.Document.Find(layer.Id);
        Assert.That(actual.Bounds.Width, Is.EqualTo(60).Within(1e-8));
        Assert.That(actual.Bounds.Height, Is.EqualTo(30).Within(1e-8));
        Assert.That(actual.Bounds.X + 15, Is.EqualTo(320).Within(1e-8));
        Assert.That(actual.Bounds.Y - 15, Is.EqualTo(240).Within(1e-8));
        Assert.That(actual.RotationDegrees, Is.EqualTo(90));
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
    }

    [Test]
    public void SavingAndImportingSelectionKeepRotationAndExportPixels()
    {
        AvaloniaTestHost.EnsureStarted();
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new(100, 100, 120, 60),
            RotationDegrees = 37, ColorArgb = 0xFFCF1313 };
        var source = new EditorDocument { CanvasWidth = 640, CanvasHeight = 480, Layers = [layer] };
        var before = source.Copy();
        var selected = SelectionProject.Create(source, [layer.Id], false);
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, selected, "test");
            var reloaded = NativePackageStore.Read(path);
            Assert.That(reloaded.Find(layer.Id).RotationDegrees, Is.EqualTo(37));
            Assert.That(TestImages.RenderPng(reloaded, true), Is.EqualTo(TestImages.RenderPng(source, true)));
            Assert.That(source.StateEquals(before), Is.True);
        }
        finally { File.Delete(path); }
    }
}
