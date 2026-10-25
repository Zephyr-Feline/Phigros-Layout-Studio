using Editor.Application;
using Editor.Core;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class CanvasBoundaryPrecisionTests
{
    [TestCase(1920, 1080, 10, ArrangeAction.Left)]
    [TestCase(1440, 1080, 10, ArrangeAction.Left)]
    [TestCase(1920, 1080, 23, ArrangeAction.Right)]
    [TestCase(1440, 1080, 23, ArrangeAction.Right)]
    [TestCase(1920, 1080, 33, ArrangeAction.Top)]
    [TestCase(1440, 1080, 33, ArrangeAction.Top)]
    [TestCase(1920, 1080, 60, ArrangeAction.Bottom)]
    [TestCase(1440, 1080, 60, ArrangeAction.Bottom)]
    public void ArbitraryAngleCanvasAlignmentCanBeCapturedWithoutAFalseClippingWarning(
        int width, int height, double angle, ArrangeAction action)
    {
        var layer = new Layer { Kind = LayerKind.Shape, Name = "旋转贴边", Bounds = new(100, 100, 200, 100),
            RotationDegrees = angle };
        var edit = new EditSession(new EditorDocument { CanvasWidth = width, CanvasHeight = height, Layers = [layer] });
        edit.SetSelection([layer.Id]);
        edit.ArrangeSelection(action, ArrangeReference.Canvas, false);
        var aligned = LayerGeometry.VisualBounds(edit.Document.Find(layer.Id));
        var actualEdge = action switch
        {
            ArrangeAction.Left => aligned.X,
            ArrangeAction.Right => aligned.X + aligned.Width,
            ArrangeAction.Top => aligned.Y,
            _ => aligned.Y + aligned.Height
        };
        var expectedEdge = action switch { ArrangeAction.Right => width, ArrangeAction.Bottom => height, _ => 0 };
        Assert.That(actualEdge, Is.EqualTo(expectedEdge).Within(1e-9), "the alignment command placed the visual edge on the canvas");
        var assertUnchanged = Observe(edit);
        LayoutTemplate? captured = null;

        Assert.Multiple(() =>
        {
            Assert.That(ExportPreflight.Inspect(edit.Document, _ => false), Is.Empty);
            Assert.DoesNotThrow(() => captured = edit.CaptureLayoutTemplate("贴边布局", edit.SelectedLayerIds));
            if (captured is not null)
            {
                var slot = captured.Slots.Single();
                var capturedEdge = action switch
                {
                    ArrangeAction.Left => slot.X,
                    ArrangeAction.Right => slot.X + slot.Width,
                    ArrangeAction.Top => slot.Y,
                    _ => slot.Y + slot.Height
                };
                Assert.That(capturedEdge, Is.EqualTo(expectedEdge == 0 ? 0 : 1), "captured edge uses the exact normalized boundary");
                Assert.DoesNotThrow(captured.Validate);
            }
            assertUnchanged();
        });
    }

    [TestCase(-0.000001, ExportWarningKind.ClippedByCanvas)]
    [TestCase(1720.000001, ExportWarningKind.ClippedByCanvas)]
    [TestCase(-200.000001, ExportWarningKind.OutsideCanvas)]
    public void GenuineSmallOverflowStillRejectsCaptureAndKeepsItsExportWarning(double x, ExportWarningKind warning)
    {
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new(x, 100, 200, 100) };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1920, CanvasHeight = 1080, Layers = [layer] });
        edit.SetSelection([layer.Id]);
        edit.UpdateLayer(layer.Id, value => value.Name = "redo checkpoint");
        Assert.That(edit.Undo(), Is.True);
        var assertUnchanged = Observe(edit);

        Assert.That(ExportPreflight.Inspect(edit.Document, _ => false).Select(item => item.Kind), Is.EqualTo(new[] { warning }));
        Assert.Throws<InvalidOperationException>(() => edit.CaptureLayoutTemplate("越界布局", edit.SelectedLayerIds));
        assertUnchanged();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void PersistedTemplateValidationStillRejectsEvenATinyNegativeOrigin(bool negativeX)
    {
        var slot = new RectD(negativeX ? -1e-15 : 0, negativeX ? 0 : -1e-15, .25, .25);
        var template = new LayoutTemplate("strict-negative", "严格负坐标验证", [slot]);
        Assert.Throws<InvalidOperationException>(template.Validate,
            "normalizing derived editor geometry must not relax the template-file validation contract");
    }

    [Test]
    public void CapturePreservesFirstSelectedGroupOrderAndIncludesHiddenLockedMembersWithoutChangingHistory()
    {
        var group = Guid.NewGuid();
        var first = new Layer { Kind = LayerKind.Shape, GroupId = group, Bounds = new(100, 100, 200, 100), RotationDegrees = 10 };
        var hiddenBadge = EditorDocument.NewBadge(new(280, 130), 32);
        hiddenBadge.GroupId = group;
        hiddenBadge.Visible = false;
        hiddenBadge.Locked = true;
        var independent = new Layer { Kind = LayerKind.Shape, Bounds = new(600, 300, 160, 80), RotationDegrees = -17 };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1440, CanvasHeight = 1080,
            Layers = [first, hiddenBadge, independent] });
        edit.SetSelection([independent.Id, hiddenBadge.Id, first.Id]);
        edit.UpdateLayer(independent.Id, value => value.Name = "redo checkpoint");
        Assert.That(edit.Undo(), Is.True);
        var assertUnchanged = Observe(edit);

        var captured = edit.CaptureLayoutTemplate("分组选中顺序", edit.SelectedLayerIds);
        Assert.That(captured.Slots.Count, Is.EqualTo(2), "selecting two members of a group still captures one slot");
        AssertSlot(captured.Slots[0], LayerGeometry.VisualBounds(independent), 1440, 1080);
        AssertSlot(captured.Slots[1], MoveSnapping.BoundsOf(new[]
            { LayerGeometry.VisualBounds(first), LayerGeometry.VisualBounds(hiddenBadge) }), 1440, 1080);
        Assert.That(ExportPreflight.Inspect(edit.Document, _ => false).Select(item => (item.Kind, item.LayerId)),
            Is.EqualTo(new[] { (ExportWarningKind.HiddenLayer, hiddenBadge.Id) }));
        assertUnchanged();

        Assert.Throws<InvalidOperationException>(() => edit.ApplyLayoutTemplate(captured, edit.SelectedLayerIds),
            "recording locked geometry is allowed, but applying a template must retain the group-lock policy");
        assertUnchanged();
    }

    private static void AssertSlot(RectD slot, RectD expected, int width, int height)
    {
        Assert.Multiple(() =>
        {
            Assert.That(slot.X, Is.EqualTo(expected.X / width).Within(1e-12));
            Assert.That(slot.Y, Is.EqualTo(expected.Y / height).Within(1e-12));
            Assert.That(slot.Width, Is.EqualTo(expected.Width / width).Within(1e-12));
            Assert.That(slot.Height, Is.EqualTo(expected.Height / height).Within(1e-12));
        });
    }

    private static Action Observe(EditSession edit)
    {
        var document = edit.Document.Copy();
        var history = (edit.UndoCount, edit.RedoCount, edit.IsDirty);
        var selection = edit.SelectedLayerIds.ToArray();
        var png = TestImages.RenderPng(edit.Document, true, 320);
        return () => Assert.Multiple(() =>
        {
            Assert.That(edit.Document.StateEquals(document), Is.True, "capture and preflight never edit the model");
            Assert.That((edit.UndoCount, edit.RedoCount, edit.IsDirty), Is.EqualTo(history));
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection), "ordered selection remains unchanged");
            Assert.That(TestImages.RenderPng(edit.Document, true, 320), Is.EqualTo(png), "rendered geometry remains unchanged");
        });
    }
}
