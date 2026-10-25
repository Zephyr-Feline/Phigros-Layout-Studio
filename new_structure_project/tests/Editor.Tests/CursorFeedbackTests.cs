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

public sealed class CursorFeedbackTests
{
    private static IEnumerable<TestCaseData> ResizeCases()
    {
        var expected = new[]
        {
            new[] { "TopLeftCorner", "SizeNorthSouth", "TopRightCorner", "SizeWestEast",
                "TopLeftCorner", "SizeNorthSouth", "TopRightCorner", "SizeWestEast" },
            new[] { "SizeNorthSouth", "TopRightCorner", "SizeWestEast", "TopLeftCorner",
                "SizeNorthSouth", "TopRightCorner", "SizeWestEast", "TopLeftCorner" },
            new[] { "TopRightCorner", "SizeWestEast", "TopLeftCorner", "SizeNorthSouth",
                "TopRightCorner", "SizeWestEast", "TopLeftCorner", "SizeNorthSouth" }
        };
        foreach (var angleIndex in Enumerable.Range(0, 3))
        foreach (var handle in Enum.GetValues<ResizeHandle>())
            yield return new TestCaseData(handle, angleIndex * 45d, expected[angleIndex][(int)handle]);
    }

    [TestCaseSource(nameof(ResizeCases))]
    public void EightHandlesUseScreenResizeAxesAfterRotation(ResizeHandle handle, double angle, string expected)
    {
        var layer = Shape(); layer.RotationDegrees = angle;
        var edit = Session(layer); edit.SelectedLayerId = layer.Id;
        using var host = new CanvasHost(edit);
        var before = edit.Document.Copy();
        var png = PngExporter.Render(edit.Document);
        host.Window.MouseMove(host.View(LayerGeometry.HandlePoint(layer.Bounds, handle, angle)));
        Assert.Multiple(() =>
        {
            Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo(expected));
            Assert.That(ToolTip.GetTip(host.Canvas)?.ToString(), Does.Contain("缩放"));
            Assert.That(edit.Document.StateEquals(before), Is.True);
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
        });
        Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(png), "Hover feedback is preview-only.");
    }

    [TestCase(600)]
    [TestCase(750)]
    public void HandleHoverAndActualPressShareToleranceAndCtrlBypass(int height)
    {
        var layer = Shape(); var edit = Session(layer, height); edit.SelectedLayerId = layer.Id;
        using var host = new CanvasHost(edit);
        var edge = host.View(LayerGeometry.HandlePoint(layer.Bounds, ResizeHandle.Right));
        var near = new Point(edge.X + 7, edge.Y);
        host.Window.MouseMove(near);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("SizeWestEast"));
        host.Window.MouseDown(near, MouseButton.Left);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("SizeWestEast"));
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        host.Window.MouseUp(near, MouseButton.Left);
        Assert.That(edit.UndoCount, Is.Zero);

        host.Window.MouseMove(edge, RawInputModifiers.Control);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("SizeAll"));
        host.Window.MouseMove(new Point(edge.X + 9, edge.Y));
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("Arrow"));
    }

    [TestCase(-90)]
    [TestCase(-45)]
    [TestCase(180)]
    public void NegativeAndHalfTurnAnglesKeepEquivalentResizeAxes(double angle)
    {
        var layer = Shape(); layer.RotationDegrees = angle;
        var edit = Session(layer); edit.SelectedLayerId = layer.Id;
        using var host = new CanvasHost(edit);
        host.Window.MouseMove(host.View(LayerGeometry.HandlePoint(layer.Bounds, ResizeHandle.Right, angle)));
        var expected = angle == -90 ? "SizeNorthSouth" : angle == -45 ? "TopRightCorner" : "SizeWestEast";
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void RotationMoveAndMarqueeKeepFeedbackThroughCaptureThenCancelCleanly()
    {
        var layer = Shape(); var edit = Session(layer); edit.SelectedLayerId = layer.Id;
        using var host = new CanvasHost(edit);
        var top = host.View(LayerGeometry.HandlePoint(layer.Bounds, ResizeHandle.Top));
        var rotation = new Point(top.X, top.Y - 24);
        host.Window.MouseMove(rotation);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("Hand"));
        Assert.That(ToolTip.GetTip(host.Canvas)?.ToString(), Does.Contain("旋转"));
        host.Window.MouseDown(rotation, MouseButton.Left);
        host.Window.MouseMove(host.View(new(100, 100)), RawInputModifiers.LeftMouseButton);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("Hand"));
        Assert.That(ToolTip.GetIsOpen(host.Canvas), Is.False);
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        host.Window.MouseUp(host.View(new(100, 100)), MouseButton.Left);
        Assert.That(edit.Document.Find(layer.Id).RotationDegrees, Is.Zero);
        Assert.That(edit.UndoCount, Is.Zero);

        var centre = host.View(LayerGeometry.Centre(layer.Bounds));
        host.Window.MouseMove(centre);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("SizeAll"));
        host.Window.MouseDown(centre, MouseButton.Left);
        host.Window.MouseMove(new(centre.X + 30, centre.Y + 20), RawInputModifiers.LeftMouseButton);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("SizeAll"));
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        host.Window.MouseUp(centre, MouseButton.Left);

        var empty = host.View(new(800, 500));
        host.Window.MouseDown(empty, MouseButton.Left);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("Cross"));
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        host.Window.MouseUp(empty, MouseButton.Left);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("Arrow"));
        Assert.That(edit.Document.Find(layer.Id).Bounds, Is.EqualTo(layer.Bounds));
        Assert.That(edit.SelectedLayerId, Is.EqualTo(layer.Id));
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void GroupHintTracksActualUnlockedTargetsAndRefreshesWithoutPointerMovement()
    {
        var group = Guid.NewGuid();
        var first = Shape(); first.GroupId = group;
        var second = Shape(); second.Id = Guid.NewGuid(); second.GroupId = group;
        second.Bounds = new(500, 200, 160, 120);
        var locked = Shape(); locked.Id = Guid.NewGuid(); locked.GroupId = group;
        locked.Bounds = new(700, 200, 100, 120); locked.Locked = true;
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [first, second, locked] }) { SelectedLayerId = first.Id };
        using var host = new CanvasHost(edit);
        host.Window.MouseMove(host.View(LayerGeometry.Centre(first.Bounds)));
        Assert.That(ToolTip.GetTip(host.Canvas)?.ToString(), Does.Contain("2 个联动图层"));
        host.Canvas.MoveWholeGroup = false; host.Canvas.Refresh();
        Assert.That(ToolTip.GetTip(host.Canvas)?.ToString(), Does.Not.Contain("联动"));
        host.Window.MouseMove(host.View(LayerGeometry.Centre(locked.Bounds)));
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("No"));
        Assert.That(ToolTip.GetTip(host.Canvas)?.ToString(), Does.Contain("已锁定"));
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void LockedOverlayDoesNotMaskMovableLayerOrTextFeedback()
    {
        var text = Shape(); text.Kind = LayerKind.Text; text.Text = "中文";
        var locked = Shape(); locked.Id = Guid.NewGuid(); locked.Locked = true;
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [text, locked] });
        using var host = new CanvasHost(edit);
        host.Window.MouseMove(host.View(LayerGeometry.Centre(text.Bounds)));
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("SizeAll"));
        Assert.That(ToolTip.GetTip(host.Canvas)?.ToString(), Does.Contain("双击编辑文字"));
        edit.Document.Find(text.Id).Visible = false; host.Canvas.Refresh();
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("No"));
        edit.Document.Find(locked.Id).Visible = false; host.Canvas.Refresh();
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("Arrow"));
    }

    [Test]
    public void MiddlePanKeepsItsCursorUntilReleaseAndPointerExitClearsHover()
    {
        var layer = Shape(); var edit = Session(layer);
        using var host = new CanvasHost(edit);
        var centre = host.View(LayerGeometry.Centre(layer.Bounds));
        host.Window.MouseDown(centre, MouseButton.Middle);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("Hand"));
        host.Window.MouseMove(new(centre.X + 20, centre.Y + 10), RawInputModifiers.MiddleMouseButton);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("Hand"));
        host.Window.MouseUp(new(centre.X + 20, centre.Y + 10), MouseButton.Middle);
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("SizeAll"));
        host.Window.MouseMove(new(-20, -20));
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("Arrow"));
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void DetachReleasesCachedCursorsAndReattachLazilyRebuildsFeedback()
    {
        var layer = Shape(); var edit = Session(layer); edit.SelectedLayerId = layer.Id;
        using var host = new CanvasHost(edit);
        var centre = host.View(LayerGeometry.Centre(layer.Bounds));
        host.Window.MouseMove(centre);
        var previousCursor = host.Canvas.Cursor;
        Assert.That(previousCursor?.ToString(), Is.EqualTo("SizeAll"));
        host.Window.Content = null;
        Assert.That(host.Canvas.Cursor, Is.Null);
        host.Canvas.Refresh();
        Assert.That(host.Canvas.Cursor, Is.Null, "A detached refresh must not recreate cursor resources.");
        host.Window.Content = host.Canvas;
        host.Window.MouseMove(new(centre.X + 1, centre.Y));
        Assert.That(host.Canvas.Cursor?.ToString(), Is.EqualTo("SizeAll"));
        Assert.That(host.Canvas.Cursor, Is.Not.SameAs(previousCursor));
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(edit.Document.Find(layer.Id).Bounds, Is.EqualTo(layer.Bounds));
    }

    private static Layer Shape() => new() { Kind = LayerKind.Shape, Bounds = new(200, 200, 200, 120) };
    private static EditSession Session(Layer layer, int height = 600) =>
        new(new EditorDocument { CanvasWidth = 1000, CanvasHeight = height, Layers = [layer] });

    private sealed class CanvasHost : IDisposable
    {
        private readonly EditSession _edit;
        public SceneCanvas Canvas { get; }
        public Window Window { get; }
        public CanvasHost(EditSession edit)
        {
            AvaloniaTestHost.EnsureStarted();
            _edit = edit;
            Canvas = new(edit);
            Window = new() { Width = 800, Height = 600, Content = Canvas };
            Window.Show();
        }
        public Point View(PointD point)
        {
            var document = _edit.Document;
            var view = new ViewportTransform(document.CanvasWidth, document.CanvasHeight,
                Canvas.Bounds.Width, Canvas.Bounds.Height).ToView(point);
            return new(view.X, view.Y);
        }
        public void Dispose() => Window.Close();
    }
}
