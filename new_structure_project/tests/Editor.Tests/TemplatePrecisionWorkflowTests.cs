using Editor.Application;
using Editor.Core;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class TemplatePrecisionWorkflowTests
{
    private static readonly LayoutTemplate FitSlot = new("precision-fit", "精度适配", [new(.04, .16, .28, .70)]);

    [Test]
    public void ApplyingTheSameTemplateToAnAlreadyFittedRotatedLayerKeepsExactStateAndRedo()
    {
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new(100, 100, 200, 100), RotationDegrees = 10 };
        var edit = Session([layer]);
        edit.SetSelection([layer.Id]);
        edit.ApplyLayoutTemplate(FitSlot, edit.SelectedLayerIds);
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        SeedRedo(edit, layer.Id);
        var assertUnchanged = Observe(edit);

        edit.ApplyLayoutTemplate(FitSlot, edit.SelectedLayerIds);

        assertUnchanged();
    }

    [TestCase(0)]
    [TestCase(37)]
    public void ApplyingAnImmediatelyCapturedDecimalLayoutKeepsOriginalGeometryExactly(double angle)
    {
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new(100.1, 100.1, 200.1, 100.1), RotationDegrees = angle };
        var edit = Session([layer]);
        edit.SetSelection([layer.Id]);
        SeedRedo(edit, layer.Id);
        var assertUnchanged = Observe(edit);
        var template = edit.CaptureLayoutTemplate("原位小数布局", edit.SelectedLayerIds);

        edit.ApplyLayoutTemplate(template, edit.SelectedLayerIds);

        assertUnchanged();
    }

    [Test]
    public void RepeatedRotatedSongGroupFitKeepsMinimumFontBadgeGeometrySelectionAndRedo()
    {
        var layers = SongGroup(10, false);
        var edit = Session(layers);
        edit.SetSelection([layers[1].Id, layers[2].Id, layers[0].Id]);
        edit.ApplyLayoutTemplate(FitSlot, edit.SelectedLayerIds);
        Assert.That(edit.Document.Find(layers[1].Id).FontSize, Is.EqualTo(1), "existing font floor applies to the first real shrink");
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        SeedRedo(edit, layers[0].Id);
        var assertUnchanged = Observe(edit);

        edit.ApplyLayoutTemplate(FitSlot, edit.SelectedLayerIds);

        assertUnchanged();
        Assert.That(edit.Document.Find(layers[2].Id).Bounds.Width / edit.Document.Find(layers[2].Id).Bounds.Height,
            Is.EqualTo(EditorDocument.BadgeRatio).Within(1e-12));
    }

    [Test]
    public void ApplyingAnImmediatelyCapturedRotatedSongGroupKeepsEveryMemberExactly()
    {
        var layers = SongGroup(37, true);
        var edit = Session(layers);
        edit.SetSelection([layers[2].Id, layers[1].Id, layers[0].Id]);
        SeedRedo(edit, layers[0].Id);
        var assertUnchanged = Observe(edit);
        var template = edit.CaptureLayoutTemplate("原位歌曲组", edit.SelectedLayerIds);
        Assert.That(template.Slots.Count, Is.EqualTo(1));

        edit.ApplyLayoutTemplate(template, edit.SelectedLayerIds);

        assertUnchanged();
    }

    [Test]
    public void FortyNineUnitShapeAndTextCanFitTheLegalOneByOneMinimumTogether()
    {
        var group = Guid.NewGuid();
        var shape = new Layer { Kind = LayerKind.Shape, GroupId = group, Bounds = new(100, 100, 49, 49) };
        var text = new Layer { Kind = LayerKind.Text, GroupId = group, Bounds = shape.Bounds, Text = "最小字号", FontSize = 1 };
        var edit = Session([shape, text]);
        edit.SetSelection([shape.Id]);
        var before = edit.Document.Copy();
        var template = new LayoutTemplate("one-unit", "一单位", [new(0, 0, 1d / 640, 1d / 480)]);

        Assert.DoesNotThrow(() => edit.ApplyLayoutTemplate(template, edit.SelectedLayerIds));

        foreach (var layer in edit.Document.Layers)
        {
            Assert.That(layer.Bounds.Width, Is.GreaterThanOrEqualTo(1).And.EqualTo(1).Within(1e-12));
            Assert.That(layer.Bounds.Height, Is.GreaterThanOrEqualTo(1).And.EqualTo(1).Within(1e-12));
        }
        Assert.That(edit.Document.Find(shape.Id).Bounds, Is.EqualTo(edit.Document.Find(text.Id).Bounds), "one common scale retains coincident members");
        Assert.That(edit.Document.Find(text.Id).FontSize, Is.EqualTo(1));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(ExportPreflight.Inspect(edit.Document, _ => false), Is.Empty);
        var fitted = edit.Document.Copy();
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.StateEquals(fitted), Is.True);
    }

    [Test]
    public void FortyNineUnitBadgeCanFitTheLegalEightUnitWidthWithoutBreakingItsRatio()
    {
        var badge = EditorDocument.NewBadge(new(100, 100), 49);
        var edit = Session([badge]);
        edit.SetSelection([badge.Id]);
        var template = new LayoutTemplate("minimum-badge", "最小徽标", [new(0, 0, 8d / 640, 6.75 / 480)]);

        Assert.DoesNotThrow(() => edit.ApplyLayoutTemplate(template, edit.SelectedLayerIds));

        var fitted = edit.Document.Find(badge.Id);
        Assert.That(fitted.Bounds.Width, Is.GreaterThanOrEqualTo(8).And.EqualTo(8).Within(1e-12));
        Assert.That(fitted.Bounds.Height, Is.EqualTo(6.75).Within(1e-12));
        Assert.That(fitted.Bounds.Width / fitted.Bounds.Height, Is.EqualTo(EditorDocument.BadgeRatio).Within(1e-12));
        Assert.That(fitted.KeepAspect, Is.True);
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.DoesNotThrow(edit.Document.Validate);
        Assert.That(ExportPreflight.Inspect(edit.Document, _ => false), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GenuineMicrounitTranslationOrShrinkStillCreatesAnEffectiveEdit(bool shrink)
    {
        const double change = 1e-6;
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new(100, 100, 200, 100) };
        var edit = Session([layer]);
        edit.SetSelection([layer.Id]);
        SeedRedo(edit, layer.Id);
        var before = edit.Document.Copy();
        var template = new LayoutTemplate("real-small-edit", "真实微小调整",
            [new((100 + (shrink ? 0 : change)) / 640, 100d / 480,
                (200 - (shrink ? change : 0)) / 640, 100d / 480)]);

        edit.ApplyLayoutTemplate(template, edit.SelectedLayerIds);

        var actual = edit.Document.Find(layer.Id).Bounds;
        Assert.That(edit.Document.StateEquals(before), Is.False);
        Assert.That(shrink ? actual.Width : actual.X,
            Is.EqualTo(shrink ? 200 - change : 100 + change).Within(1e-11));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.RedoCount, Is.Zero);
        Assert.That(edit.IsDirty, Is.True);
        var after = edit.Document.Copy();
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.StateEquals(after), Is.True);
    }

    [TestCase(49, 1e-6)]
    [TestCase(8, 1e-6)]
    [TestCase(8, 1e-10)]
    public void ASlotGenuinelySmallerThanTheBadgeMinimumStillRollsBackAndKeepsRedo(double sourceWidth, double shortage)
    {
        var badge = EditorDocument.NewBadge(new(100, 100), sourceWidth);
        var edit = Session([badge]);
        edit.SetSelection([badge.Id]);
        SeedRedo(edit, badge.Id);
        var assertUnchanged = Observe(edit);
        // At width 8 the source is already at this location. The 1e-10 shortage is within
        // placement tolerance, so a no-op check must not bypass the strict size minimum.
        var slotOrigin = sourceWidth == 8 ? 100d : 0;
        var template = new LayoutTemplate("too-small-badge", "确实过小",
            [new(slotOrigin / 640, slotOrigin / 480, (8 - shortage) / 640, 6.75 / 480)]);

        Assert.Throws<InvalidOperationException>(() => edit.ApplyLayoutTemplate(template, edit.SelectedLayerIds));

        assertUnchanged();
    }

    private static Layer[] SongGroup(double angle, bool fractional)
    {
        var group = Guid.NewGuid();
        var origin = fractional ? 100.1 : 100;
        var extra = fractional ? .1 : 0;
        var shape = new Layer { Kind = LayerKind.Shape, GroupId = group, Name = "曲绘底板",
            Bounds = new(origin, origin, 200 + extra, 100 + extra), RotationDegrees = angle };
        var text = new Layer { Kind = LayerKind.Text, GroupId = group, Name = "曲名",
            Bounds = new(origin + 10, origin + 40, 130, 25), Text = "歌曲组", FontSize = 1.1, RotationDegrees = angle };
        var badge = EditorDocument.NewBadge(new(origin + 120, origin + 30), 49);
        badge.GroupId = group;
        badge.RotationDegrees = angle;
        return [shape, text, badge];
    }

    private static EditSession Session(Layer[] layers) => new(new EditorDocument
        { CanvasWidth = 640, CanvasHeight = 480, Layers = layers.ToList() });

    private static void SeedRedo(EditSession edit, Guid id)
    {
        var name = edit.Document.Find(id).Name;
        edit.UpdateLayer(id, layer => layer.Name = name + " redo checkpoint");
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.RedoCount, Is.EqualTo(1));
    }

    private static Action Observe(EditSession edit)
    {
        var document = edit.Document.Copy();
        var history = (edit.UndoCount, edit.RedoCount, edit.IsDirty);
        var selection = edit.SelectedLayerIds.ToArray();
        var png = TestImages.RenderPng(edit.Document, true, 320);
        return () => Assert.Multiple(() =>
        {
            Assert.That(edit.Document.StateEquals(document), Is.True, "a layout no-op preserves every persisted value exactly");
            Assert.That((edit.UndoCount, edit.RedoCount, edit.IsDirty), Is.EqualTo(history));
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection));
            Assert.That(TestImages.RenderPng(edit.Document, true, 320), Is.EqualTo(png));
        });
    }
}
