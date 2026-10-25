using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4ResizeSnappingTests
{
    [Test]
    public void IndependentResizeSnapsMovingEdgesAndKeepsAnchorFixed()
    {
        var shape = Shape(100, 100, 100, 80);
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [shape] };
        var snap = ResizeSnapping.Solve(document, [shape.Id], shape.Bounds,
            297, 119, 4, 4, uniform: false);
        Assert.Multiple(() =>
        {
            Assert.That(snap.Delta, Is.EqualTo(new PointD(300, 120)));
            Assert.That(snap.VerticalGuide, Is.EqualTo(500));
            Assert.That(snap.HorizontalGuide, Is.EqualTo(300));
        });
        var edit = new EditSession(document);
        edit.BeginGesture(shape.Id);
        edit.ResizeBy(snap.Delta.X, snap.Delta.Y);
        edit.CommitGesture();
        Assert.That(edit.Document.Find(shape.Id).Bounds, Is.EqualTo(new RectD(100, 100, 400, 200)));
    }

    [Test]
    public void StationaryLockedLayersGuideResizeWhileHiddenAndMovingLayersDoNot()
    {
        var moving = Shape(100, 100, 100, 80);
        var stationary = Shape(340, 300, 100, 40); stationary.Locked = true;
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving, stationary] };
        var snap = ResizeSnapping.Solve(document, [moving.Id], moving.Bounds,
            137, 0, 4, 4, uniform: false);
        Assert.That((snap.Delta.X, snap.VerticalGuide), Is.EqualTo((140, (double?)340)));
        stationary.Visible = false;
        var hidden = ResizeSnapping.Solve(document, [moving.Id], moving.Bounds,
            137, 0, 4, 4, uniform: false);
        Assert.That(hidden.VerticalGuide, Is.Null);
        document.Layers.Remove(stationary);
        var own = ResizeSnapping.Solve(document, [moving.Id], moving.Bounds,
            3, 0, 4, 4, uniform: false);
        Assert.That(own.VerticalGuide, Is.Null, "the resized layer offers no guide to itself");
    }

    [Test]
    public void EqualDistanceStationaryGuidesUseStableLayerOrder()
    {
        var moving = Shape(100, 100, 100, 80);
        var first = Shape(340, 300, 50, 40);
        var second = Shape(336, 400, 50, 40);
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving, first, second] };
        var snap = ResizeSnapping.Solve(document, [moving.Id], moving.Bounds,
            138, 0, 4, 4, uniform: false);
        Assert.That((snap.Delta.X, snap.VerticalGuide), Is.EqualTo((140, (double?)340)));
        first.Visible = false;
        var next = ResizeSnapping.Solve(document, [moving.Id], moving.Bounds,
            138, 0, 4, 4, uniform: false);
        Assert.That((next.Delta.X, next.VerticalGuide), Is.EqualTo((136, (double?)336)));
    }

    [Test]
    public void UniformGroupResizeUsesOneScaleAndCanvasWinsExactTie()
    {
        var first = Shape(100, 100, 100, 100);
        var second = Shape(300, 100, 100, 100);
        var stationary = Shape(500, 350, 100, 40); stationary.Locked = true;
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [first, second, stationary] };
        var bounds = MoveSnapping.BoundsOf([first.Bounds, second.Bounds]);
        var snap = ResizeSnapping.Solve(document, [first.Id, second.Id], bounds,
            98, 0, 5, 5, uniform: true);
        Assert.Multiple(() =>
        {
            Assert.That(snap.Delta.X, Is.EqualTo(100).Within(1e-9));
            Assert.That(snap.Delta.Y, Is.EqualTo(100.0 / 3).Within(1e-9));
            Assert.That(snap.VerticalGuide, Is.EqualTo(500));
            Assert.That(snap.HorizontalGuide, Is.Null);
        });
        var edit = new EditSession(document);
        edit.SetSelection([first.Id, second.Id]);
        edit.BeginGesture(first.Id);
        edit.ResizeBy(snap.Delta.X, snap.Delta.Y);
        edit.CommitGesture();
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(first.Id).Bounds.Width, Is.EqualTo(400.0 / 3).Within(1e-9));
            Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(1100.0 / 3).Within(1e-9));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void UniformCandidateMustKeepWholeHandleWithinPhysicalTolerance()
    {
        var moving = Shape(100, 100, 300, 100);
        var vertical = Shape(553, 400, 100, 50);
        var horizontal = Shape(700, 252, 100, 50);
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving, vertical, horizontal] };
        var snap = ResizeSnapping.Solve(document, [moving.Id], moving.Bounds,
            150, 0, 8, 8, uniform: true);
        Assert.That(snap.VerticalGuide, Is.EqualTo(553),
            "x target moves the whole handle 3 units; y target would move x by 6");
        Assert.That(snap.Delta.X, Is.EqualTo(153).Within(1e-9));
    }

    [Test]
    public void AspectLockedBadgeKeepsRatioAfterComponentResizeSnap()
    {
        var badge = EditorDocument.NewBadge(new PointD(100, 100));
        var stationary = Shape(357, 400, 100, 50);
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [badge, stationary] };
        var snap = ResizeSnapping.Solve(document, [badge.Id], badge.Bounds,
            126, 0, 4, 4, uniform: true);
        Assert.That(snap.VerticalGuide, Is.EqualTo(357));
        var edit = new EditSession(document);
        edit.BeginGesture(badge.Id);
        edit.ResizeBy(snap.Delta.X, snap.Delta.Y);
        edit.CommitGesture();
        var resized = edit.Document.Find(badge.Id).Bounds;
        Assert.Multiple(() =>
        {
            Assert.That(resized.X + resized.Width, Is.EqualTo(357).Within(1e-9));
            Assert.That(resized.Width / resized.Height,
                Is.EqualTo(EditorDocument.BadgeRatio).Within(1e-9));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
    }

    [TestCase(1000, 600)]
    [TestCase(1000, 750)]
    public void PhysicalPixelThresholdIsStableAcrossCanvasRatios(int width, int height)
    {
        var shape = Shape(100, 100, 100, 80);
        var document = new EditorDocument { CanvasWidth = width, CanvasHeight = height,
            Layers = [shape] };
        var transform = new ViewportTransform(width, height, 800, 600);
        var tolerance = transform.ScreenPixelsToLogical(8, 1.5);
        var inside = ResizeSnapping.Solve(document, [shape.Id], shape.Bounds,
            width / 2.0 - 200 + tolerance, 0, tolerance, tolerance, uniform: false);
        var outside = ResizeSnapping.Solve(document, [shape.Id], shape.Bounds,
            width / 2.0 - 200 + tolerance + 0.01, 0, tolerance, tolerance, uniform: false);
        Assert.That(inside.VerticalGuide, Is.EqualTo(width / 2.0));
        Assert.That(outside.VerticalGuide, Is.Null);
    }

    [TestCase(1000, 600)]
    [TestCase(1000, 750)]
    public void HeadlessUnionHandleSnapsAltBypassesAndGuideNeverExports(int width, int height)
    {
        AvaloniaTestHost.EnsureStarted();
        var first = Shape(100, 100, 100, 100);
        var second = Shape(300, 100, 100, 100);
        var stationary = Shape(550, 350, 100, 50); stationary.Locked = true;
        var edit = new EditSession(new EditorDocument { CanvasWidth = width,
            CanvasHeight = height, Layers = [first, second, stationary] });
        edit.SetSelection([first.Id, second.Id]);
        var canvas = new SceneCanvas(edit);
        var window = new Window { Width = 800, Height = 600, Content = canvas };
        try
        {
            window.Show();
            var transform = new ViewportTransform(width, height, canvas.Bounds.Width,
                canvas.Bounds.Height);
            Point View(double x, double y)
            {
                var point = transform.ToView(new PointD(x, y));
                return new Point(point.X, point.Y);
            }
            var handle = View(400, 200);
            var near = View(548, 200);
            window.MouseDown(handle, MouseButton.Left);
            window.MouseMove(near, RawInputModifiers.LeftMouseButton);
            Assert.That(edit.Document.Find(second.Id).Bounds.X +
                edit.Document.Find(second.Id).Bounds.Width, Is.EqualTo(550).Within(1e-6));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.That(edit.Document.Find(second.Id).Bounds,
                Is.EqualTo(new RectD(300, 100, 100, 100)));
            Assert.That(edit.UndoCount, Is.Zero);

            window.MouseDown(handle, MouseButton.Left);
            window.MouseMove(near, RawInputModifiers.LeftMouseButton);
            var withGuide = PngExporter.Render(edit.Document);
            window.MouseUp(near, MouseButton.Left);
            Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(withGuide));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.Undo(), Is.True);

            window.MouseDown(handle, MouseButton.Left);
            window.MouseMove(near, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            window.MouseUp(near, MouseButton.Left);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(second.Id).Bounds.X +
                    edit.Document.Find(second.Id).Bounds.Width, Is.EqualTo(548).Within(1e-6));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
        }
        finally { window.Close(); }
    }

    private static Layer Shape(double x, double y, double width, double height) => new()
    {
        Kind = LayerKind.Shape, Bounds = new RectD(x, y, width, height)
    };
}
