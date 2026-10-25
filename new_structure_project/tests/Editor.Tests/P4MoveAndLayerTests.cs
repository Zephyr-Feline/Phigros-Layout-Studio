using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4MoveAndLayerTests
{
    private static Layer Shape(double x, double y, double width = 100, double height = 60) => new()
    {
        Kind = LayerKind.Shape, Bounds = new RectD(x, y, width, height)
    };

    [Test]
    public void CanvasAndComponentGuidesUseNearestCorrectionWithCanvasFirstTies()
    {
        var moving = Shape(100, 80, 200, 100);
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving] };
        var left = MoveSnapping.Solve(document, [moving.Id], moving.Bounds, -95, 0, 5, 5);
        Assert.Multiple(() =>
        {
            Assert.That(left.Delta.X, Is.EqualTo(-100));
            Assert.That(left.VerticalGuide, Is.EqualTo(0));
            Assert.That(left.HorizontalGuide, Is.Null);
        });

        var stationary = Shape(10, 300, 90, 40);
        stationary.Locked = true;
        document.Layers.Add(stationary);
        var tie = MoveSnapping.Solve(document, [moving.Id], moving.Bounds, -95, 0, 5, 5);
        Assert.That(tie.VerticalGuide, Is.EqualTo(0), "canvas wins an equal correction over a locked layer");

        stationary.Bounds = new RectD(340, 300, 100, 40);
        var component = MoveSnapping.Solve(document, [moving.Id], moving.Bounds, 235, 0, 6, 6);
        Assert.Multiple(() =>
        {
            Assert.That(component.Delta.X, Is.EqualTo(240));
            Assert.That(component.VerticalGuide, Is.EqualTo(340));
        });

        stationary.Visible = false;
        var hidden = MoveSnapping.Solve(document, [moving.Id], moving.Bounds, 235, 0, 6, 6);
        Assert.That(hidden.VerticalGuide, Is.Null, "hidden stationary layers offer no guide");
    }

    [Test]
    public void MovingSetExcludesItselfByIdAndCanSnapToAnIdenticalStationaryLayer()
    {
        var moving = Shape(100, 100);
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving] };
        var alone = MoveSnapping.Solve(document, [moving.Id], moving.Bounds, 7, 0, 8, 8);
        Assert.That(alone.VerticalGuide, Is.Null);
        var identical = Shape(100, 100);
        document.Layers.Add(identical);
        var withOther = MoveSnapping.Solve(document, [moving.Id], moving.Bounds, 7, 0, 8, 8);
        Assert.Multiple(() =>
        {
            Assert.That(withOther.Delta.X, Is.Zero);
            Assert.That(withOther.VerticalGuide, Is.EqualTo(100));
        });
    }

    [TestCase(2740, 1080, 1.0)]
    [TestCase(1440, 1080, 1.5)]
    [TestCase(1920, 1080, 2.0)]
    public void EightPhysicalPixelThresholdIsIndependentOfCanvasRatioAndRenderScale(
        int width, int height, double renderScale)
    {
        var moving = Shape(100, 100);
        var document = new EditorDocument { CanvasWidth = width, CanvasHeight = height,
            Layers = [moving] };
        var transform = new ViewportTransform(width, height, 900, 540);
        var tolerance = transform.ScreenPixelsToLogical(MoveSnapping.ScreenPixelThreshold, renderScale);
        var inside = MoveSnapping.Solve(document, [moving.Id], moving.Bounds,
            -100 + tolerance, 0, tolerance, tolerance);
        var outside = MoveSnapping.Solve(document, [moving.Id], moving.Bounds,
            -100 + tolerance + 0.01, 0, tolerance, tolerance);
        Assert.Multiple(() =>
        {
            Assert.That(inside.Delta.X, Is.EqualTo(-100).Within(1e-9));
            Assert.That(inside.VerticalGuide, Is.EqualTo(0));
            Assert.That(outside.VerticalGuide, Is.Null);
        });
    }

    [Test]
    public void GroupMoveKeepsRelativeLayoutExcludesLockedAndUsesOneUndoEntry()
    {
        var group = Guid.NewGuid();
        var first = Shape(100, 90); first.GroupId = group;
        var second = Shape(260, 150); second.GroupId = group; second.Visible = false;
        var locked = Shape(440, 210); locked.GroupId = group; locked.Locked = true;
        var other = Shape(700, 300);
        var edit = new EditSession(new EditorDocument { Layers = [first, second, locked, other] });
        edit.BeginGesture(first.Id, includeGroup: true);
        Assert.That(edit.ActiveGestureIds, Is.EquivalentTo(new[] { first.Id, second.Id }));
        Assert.That(edit.ActiveGestureBounds, Is.EqualTo(new RectD(100, 90, 260, 120)));
        edit.MoveBy(80, 30);
        edit.MoveBy(120, 45);
        edit.CommitGesture();
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(first.Id).Bounds.X, Is.EqualTo(220));
            Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(380));
            Assert.That(edit.Document.Find(second.Id).Bounds.Y, Is.EqualTo(195));
            Assert.That(edit.Document.Find(locked.Id).Bounds.X, Is.EqualTo(440));
            Assert.That(edit.Document.Find(other.Id).Bounds.X, Is.EqualTo(700));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Find(first.Id).Bounds.X, Is.EqualTo(100));
        Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(260));
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(380));

        edit.BeginGesture(first.Id, includeGroup: false);
        edit.MoveBy(25, 0);
        edit.CancelGesture();
        Assert.That(edit.Document.Find(first.Id).Bounds.X, Is.EqualTo(220));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void GroupMovementClampsOneSharedDeltaAtCoordinateLimit()
    {
        var group = Guid.NewGuid();
        var left = Shape(100, 100); left.GroupId = group;
        var right = Shape(19990, 130); right.GroupId = group;
        var edit = new EditSession(new EditorDocument { Layers = [left, right] });
        edit.BeginGesture(left.Id, includeGroup: true);
        var applied = edit.MoveBy(100, 0);
        edit.CommitGesture();
        Assert.Multiple(() =>
        {
            Assert.That(applied.X, Is.EqualTo(10));
            Assert.That(edit.Document.Find(left.Id).Bounds.X, Is.EqualTo(110));
            Assert.That(edit.Document.Find(right.Id).Bounds.X, Is.EqualTo(20000));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void GroupResizeUsesOneScaleForBoundsAndTextAndOneHistoryEntry()
    {
        var group = Guid.NewGuid();
        var badge = EditorDocument.NewBadge(new PointD(100, 100)); badge.GroupId = group;
        var text = new Layer { Kind = LayerKind.Text, GroupId = group,
            Bounds = new RectD(300, 160, 200, 80), FontSize = 40 };
        var locked = Shape(600, 200); locked.GroupId = group; locked.Locked = true;
        var edit = new EditSession(new EditorDocument { Layers = [badge, text, locked] });
        edit.BeginGesture(badge.Id, includeGroup: true);
        Assert.That(edit.ActiveGestureIds, Is.EquivalentTo(new[] { badge.Id, text.Id }));
        Assert.That(edit.ActiveGestureBounds, Is.EqualTo(new RectD(100, 100, 400, 140)));
        edit.ResizeBy(400, 0);
        edit.CommitGesture();
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(badge.Id).Bounds.Width, Is.EqualTo(256));
            Assert.That(edit.Document.Find(text.Id).Bounds, Is.EqualTo(new RectD(500, 220, 400, 160)));
            Assert.That(edit.Document.Find(text.Id).FontSize, Is.EqualTo(80));
            Assert.That(edit.Document.Find(locked.Id).Bounds.X, Is.EqualTo(600));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Find(text.Id).Bounds.X, Is.EqualTo(300));
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(text.Id).FontSize, Is.EqualTo(80));
    }

    [Test]
    public void GroupResizeClampsAtSharedSizeAndPositionLimitsAndCancelRestores()
    {
        var first = Shape(100, 100, 100, 100);
        var second = Shape(10000, 100, 100, 100);
        var edit = new EditSession(new EditorDocument { Layers = [first, second] });
        edit.SetSelection([first.Id, second.Id]);
        edit.BeginGesture(first.Id);
        edit.ResizeBy(100000, 100000);
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(first.Id).Bounds.Width, Is.EqualTo(201.010101010101).Within(1e-7));
            Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(20000).Within(1e-7));
        });
        edit.CancelGesture();
        Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(10000));
        Assert.That(edit.UndoCount, Is.Zero);

        edit.BeginGesture(first.Id);
        edit.ResizeBy(-100000, -100000);
        edit.CommitGesture();
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(first.Id).Bounds.Width, Is.EqualTo(5));
            Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(595));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void GroupSnapUsesUnionBoundsAndNeverSnapsToItsOwnMembers()
    {
        var group = Guid.NewGuid();
        var first = Shape(100, 90); first.GroupId = group;
        var second = Shape(260, 150); second.GroupId = group;
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [first, second] });
        edit.BeginGesture(first.Id, includeGroup: true);
        var bounds = edit.ActiveGestureBounds;
        var snap = MoveSnapping.Solve(edit.Document, edit.ActiveGestureIds, bounds, -95, 0, 5, 5);
        Assert.That(snap.VerticalGuide, Is.EqualTo(0));
        edit.MoveBy(snap.Delta.X, snap.Delta.Y);
        edit.CommitGesture();
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(first.Id).Bounds.X, Is.Zero);
            Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(160));
            Assert.That(edit.Document.Find(second.Id).Bounds.X - edit.Document.Find(first.Id).Bounds.X,
                Is.EqualTo(160));
        });
    }

    [Test]
    public void DuplicateAtLayerLimitRollsBackWithoutHistoryOrSelectionChanges()
    {
        var layers = Enumerable.Range(0, 200).Select(index => Shape(index, 10)).ToList();
        var edit = new EditSession(new EditorDocument { Layers = layers });
        edit.SetSelection([layers[0].Id, layers[1].Id]);
        Assert.Throws<InvalidDataException>(() => edit.DuplicateLayers(edit.SelectedLayerIds));
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(200));
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { layers[0].Id, layers[1].Id }));
        });
    }

    [Test]
    public void DuplicateVisibilityLockAndOrderRemainUndoableAndPersisted()
    {
        var group = Guid.NewGuid();
        var back = Shape(10, 10); back.GroupId = group;
        var front = Shape(20, 20);
        var edit = new EditSession(new EditorDocument { Layers = [back, front] });
        edit.DuplicateLayer(back.Id);
        var copy = edit.Document.Layers[^1];
        Assert.Multiple(() =>
        {
            Assert.That(copy.Id, Is.Not.EqualTo(back.Id));
            Assert.That(copy.GroupId, Is.Not.Null.And.Not.EqualTo(group));
            Assert.That(copy.Bounds.X, Is.EqualTo(34));
        });
        edit.UpdateLayer(copy.Id, layer => { layer.Visible = false; layer.Locked = true; });
        edit.MoveLayerToIndex(copy.Id, 1);
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p4-layers-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "p4-test");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.StateEquals(edit.Document), Is.True);
            Assert.That(reopened.Layers[1].Id, Is.EqualTo(copy.Id));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Layers[^1].Id, Is.EqualTo(copy.Id));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(copy.Id).Visible, Is.True);
            Assert.That(edit.Document.Find(copy.Id).Locked, Is.False);
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(2));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void MultiSelectionGroupsMovesDuplicatesAndUngroupsInSingleTransactions()
    {
        var first = Shape(100, 100);
        var second = Shape(250, 100);
        var unrelated = Shape(600, 100);
        var edit = new EditSession(new EditorDocument { Layers = [first, second, unrelated] });
        edit.SetSelection([first.Id, second.Id]);
        edit.GroupLayers(edit.SelectedLayerIds);
        var group = edit.Document.Find(first.Id).GroupId;
        Assert.That(group, Is.Not.Null.And.EqualTo(edit.Document.Find(second.Id).GroupId));
        Assert.That(edit.UndoCount, Is.EqualTo(1));

        edit.BeginGesture(first.Id, includeGroup: false);
        edit.MoveBy(60, 20);
        edit.CommitGesture();
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(first.Id).Bounds.X, Is.EqualTo(160));
            Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(310));
            Assert.That(edit.Document.Find(unrelated.Id).Bounds.X, Is.EqualTo(600));
            Assert.That(edit.UndoCount, Is.EqualTo(2));
        });

        edit.DuplicateLayers(edit.SelectedLayerIds);
        var copies = edit.SelectedLayerIds.Select(edit.Document.Find).ToArray();
        var copiedGroup = copies[0].GroupId;
        Assert.Multiple(() =>
        {
            Assert.That(copies.Length, Is.EqualTo(2));
            Assert.That(copies[0].GroupId, Is.Not.Null.And.Not.EqualTo(group));
            Assert.That(copies[0].GroupId, Is.EqualTo(copies[1].GroupId));
            Assert.That(copies.Select(layer => layer.Id), Is.Unique);
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(5));
            Assert.That(edit.UndoCount, Is.EqualTo(3));
        });

        edit.UngroupLayers([copies[0].Id]);
        Assert.That(edit.Document.Find(copies[0].Id).GroupId, Is.Null);
        Assert.That(edit.Document.Find(copies[1].Id).GroupId, Is.Null);
        Assert.That(edit.UndoCount, Is.EqualTo(4));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Find(copies[0].Id).GroupId, Is.EqualTo(copiedGroup));
        edit.DeleteLayers(edit.SelectedLayerIds);
        Assert.That(edit.Document.Layers.Count, Is.EqualTo(3));
        Assert.That(edit.UndoCount, Is.EqualTo(4));
        Assert.That(edit.SelectedLayerIds, Is.Empty);
    }
}
