using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4DirectionAndGroupSnapTests
{
    [Test]
    public void DirectionLocksAfterFourPhysicalPixelsAndStaysLatchedUntilShiftRelease()
    {
        var initial = MoveDirectionLock.Apply(2, 3, true, MoveAxis.None, 4);
        Assert.That(initial, Is.EqualTo((new PointD(0, 0), MoveAxis.None)));
        var horizontal = MoveDirectionLock.Apply(10, 8, true, initial.Axis, 4);
        Assert.That(horizontal, Is.EqualTo((new PointD(10, 0), MoveAxis.Horizontal)));
        var stillHorizontal = MoveDirectionLock.Apply(15, 100, true, horizontal.Axis, 4);
        Assert.That(stillHorizontal, Is.EqualTo((new PointD(15, 0), MoveAxis.Horizontal)));
        var released = MoveDirectionLock.Apply(15, 100, false, stillHorizontal.Axis, 4);
        Assert.That(released, Is.EqualTo((new PointD(15, 100), MoveAxis.None)));
        var vertical = MoveDirectionLock.Apply(15, -100, true, released.Axis, 4);
        Assert.That(vertical, Is.EqualTo((new PointD(0, -100), MoveAxis.Vertical)));
        var tie = MoveDirectionLock.Apply(-4, 4, true, MoveAxis.None, 4);
        Assert.That(tie.Axis, Is.EqualTo(MoveAxis.Horizontal));
    }

    [TestCase(1920, 1080, 1.0)]
    [TestCase(1440, 1080, 1.5)]
    [TestCase(2740, 1080, 2.0)]
    public void ShiftActivationUsesTheSamePairedViewportAndPhysicalScale(int width, int height, double scale)
    {
        var transform = new ViewportTransform(width, height, 900, 540);
        var threshold = transform.ScreenPixelsToLogical(4, scale);
        Assert.That(MoveDirectionLock.Apply(threshold - 0.01, 0, true, MoveAxis.None, threshold).Axis,
            Is.EqualTo(MoveAxis.None));
        Assert.That(MoveDirectionLock.Apply(threshold, 0, true, MoveAxis.None, threshold).Axis,
            Is.EqualTo(MoveAxis.Horizontal));
    }

    [Test]
    public void StationaryGroupUnionGuideIncludesLockedButNotHiddenMembers()
    {
        var group = Guid.NewGuid();
        var moving = Shape(100, 100, 100, 100);
        var locked = Shape(270, 320, 40, 30); locked.GroupId = group; locked.Locked = true;
        var other = Shape(450, 390, 40, 30); other.GroupId = group;
        var hidden = Shape(800, 450, 40, 30); hidden.GroupId = group; hidden.Visible = false;
        var doc = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving, locked, other, hidden] };
        var result = MoveSnapping.Solve(doc, [moving.Id], moving.Bounds, 178, 0, 3, 3);
        Assert.That(result.Delta.X, Is.EqualTo(180));
        Assert.That(result.VerticalGuide, Is.EqualTo(380), "union centre of visible members");
        Assert.That(result.HorizontalGuide, Is.Null);
        hidden.Visible = true;
        var changed = MoveSnapping.Solve(doc, [moving.Id], moving.Bounds, 178, 0, 3, 3);
        Assert.That(changed.VerticalGuide, Is.Null);
        hidden.Visible = false;
        locked.Visible = false;
        var oneVisible = MoveSnapping.Solve(doc, [moving.Id], moving.Bounds, 178, 0, 3, 3);
        Assert.That(oneVisible.VerticalGuide, Is.Null);
    }

    [Test]
    public void MovingMemberCannotOfferItsOwnGroupUnionAsATarget()
    {
        var group = Guid.NewGuid();
        var moving = Shape(100, 100, 100, 100); moving.GroupId = group;
        var first = Shape(270, 320, 40, 30); first.GroupId = group;
        var second = Shape(450, 390, 40, 30); second.GroupId = group;
        var doc = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving, first, second] };
        var snap = MoveSnapping.Solve(doc, [moving.Id], moving.Bounds, 178, 0, 3, 3);
        Assert.That(snap.VerticalGuide, Is.Null);
        Assert.That(snap.Delta.X, Is.EqualTo(178));
    }

    [Test]
    public void ExistingCanvasAndIndividualGuidesWinExactTiesBeforeGroupUnions()
    {
        var group = Guid.NewGuid();
        var moving = Shape(100, 100, 100, 100);
        var first = Shape(270, 320, 40, 30); first.GroupId = group;
        var second = Shape(454, 390, 40, 30); second.GroupId = group;
        var individual = Shape(600, 500, 30, 30);
        var doc = new EditorDocument { CanvasWidth = 760, CanvasHeight = 600,
            Layers = [moving, first, second, individual] };
        // Moving right=381 is equally close to canvas centre=380 and group centre=382.
        var tie = MoveSnapping.Solve(doc, [moving.Id], moving.Bounds, 181, 0, 2, 2);
        Assert.That(tie.VerticalGuide, Is.EqualTo(380));
        Assert.That(tie.Delta.X, Is.EqualTo(180));
        individual.Bounds = individual.Bounds with { X = 380 };
        doc.CanvasWidth = 1000;
        var individualBeforeGroup = MoveSnapping.Solve(doc, [moving.Id], moving.Bounds, 181, 0, 2, 2);
        Assert.That(individualBeforeGroup.VerticalGuide, Is.EqualTo(380),
            "an existing individual guide wins an equal-distance tie against the group union");
    }

    [Test]
    public void EqualDistanceGroupUnionsUseFirstLayerOrder()
    {
        var moving = Shape(100, 100, 100, 100);
        var firstGroup = Guid.NewGuid();
        var firstLeft = Shape(270, 320, 40, 30); firstLeft.GroupId = firstGroup;
        var firstRight = Shape(450, 390, 40, 30); firstRight.GroupId = firstGroup;
        var secondGroup = Guid.NewGuid();
        var secondLeft = Shape(272, 320, 40, 30); secondLeft.GroupId = secondGroup;
        var secondRight = Shape(452, 390, 40, 30); secondRight.GroupId = secondGroup;
        var doc = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving, firstLeft, secondLeft, firstRight, secondRight] };
        var firstWins = MoveSnapping.Solve(doc, [moving.Id], moving.Bounds, 181, 0, 2, 2);
        Assert.That(firstWins.VerticalGuide, Is.EqualTo(380));
        doc.Layers = [moving, secondLeft, firstLeft, secondRight, firstRight];
        var secondWins = MoveSnapping.Solve(doc, [moving.Id], moving.Bounds, 181, 0, 2, 2);
        Assert.That(secondWins.VerticalGuide, Is.EqualTo(382));
    }

    [Test]
    public void GroupUnionGuidesAlsoReachResizeWithoutChangingItsAspectPolicy()
    {
        var group = Guid.NewGuid();
        var moving = Shape(100, 100, 100, 80);
        var first = Shape(270, 320, 40, 30); first.GroupId = group;
        var second = Shape(450, 390, 40, 30); second.GroupId = group;
        var doc = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving, first, second] };
        var result = ResizeSnapping.Solve(doc, [moving.Id], moving.Bounds,
            178, 0, 3, 3, uniform: false);
        Assert.That(result.Delta.X, Is.EqualTo(180));
        Assert.That(result.VerticalGuide, Is.EqualTo(380));
        Assert.That(result.Delta.Y, Is.Zero);
    }

    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    public void HeadlessShiftAltGroupSnapUndoAndPngRemainConsistent(int width, int height)
    {
        AvaloniaTestHost.EnsureStarted();
        var movingGroup = Guid.NewGuid();
        var stationaryGroup = Guid.NewGuid();
        var mover = Shape(100, 100, 100, 100); mover.GroupId = movingGroup;
        var hiddenFollower = Shape(100, 270, 100, 50); hiddenFollower.GroupId = movingGroup; hiddenFollower.Visible = false;
        var stationaryA = Shape(270, 420, 40, 30); stationaryA.GroupId = stationaryGroup; stationaryA.Locked = true;
        var stationaryB = Shape(450, 480, 40, 30); stationaryB.GroupId = stationaryGroup;
        var edit = new EditSession(new EditorDocument { CanvasWidth = width, CanvasHeight = height,
            Layers = [mover, hiddenFollower, stationaryA, stationaryB] });
        var before = edit.Document.Copy();
        var canvas = new SceneCanvas(edit);
        var window = new Window { Width = 900, Height = 600, Content = canvas };
        try
        {
            window.Show();
            var transform = new ViewportTransform(width, height, canvas.Bounds.Width, canvas.Bounds.Height);
            Point View(double x, double y)
            {
                var p = transform.ToView(new PointD(x, y));
                return new Point(p.X, p.Y);
            }
            var start = View(150, 150);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(View(328, 205), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
            Assert.That(edit.Document.Find(mover.Id).Bounds, Is.EqualTo(new RectD(280, 100, 100, 100)));
            Assert.That(edit.Document.Find(hiddenFollower.Id).Bounds.X, Is.EqualTo(280));
            window.MouseMove(View(328, 410), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
            Assert.That(edit.Document.Find(mover.Id).Bounds.Y, Is.EqualTo(100), "axis stays latched");
            window.MouseMove(View(328, 410), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift | RawInputModifiers.Alt);
            Assert.That(edit.Document.Find(mover.Id).Bounds.X, Is.EqualTo(278), "Alt bypasses group snap");
            window.MouseMove(View(328, 205), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            Assert.That(edit.Document.Find(mover.Id).Bounds.Y, Is.EqualTo(155), "releasing Shift restores free movement");
            window.MouseMove(View(328, 205), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
            Assert.That(edit.Document.Find(mover.Id).Bounds.Y, Is.EqualTo(100), "fresh Shift relatches from drag origin");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.That(edit.Document.StateEquals(before), Is.True);
            Assert.That(edit.UndoCount, Is.Zero);

            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(View(328, 205), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
            var withGuide = PngExporter.Render(edit.Document);
            window.MouseUp(View(328, 205), MouseButton.Left);
            Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(withGuide));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            var committed = edit.Document.Copy();
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "shift-snap-" + Guid.NewGuid().ToString("N") + ".ppgproj");
            try
            {
                NativePackageStore.Save(path, committed, "shift-test");
                Assert.That(NativePackageStore.Read(path).StateEquals(committed), Is.True);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
            edit.Undo();
            Assert.That(edit.Document.StateEquals(before), Is.True);
            edit.Redo();
            Assert.That(edit.Document.StateEquals(committed), Is.True);
        }
        finally { window.Close(); }
    }

    private static Layer Shape(double x, double y, double width, double height) => new()
    {
        Kind = LayerKind.Shape, Bounds = new RectD(x, y, width, height)
    };
}
