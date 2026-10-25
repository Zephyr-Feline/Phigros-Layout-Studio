using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

[NonParallelizable]
public sealed class CanvasClickSnappingTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void SamePositionPointerMoveDuringClickDoesNotSnapOrDiscardRedo(bool selected, bool redo)
    {
        using var host = new Host(selected, redo);
        var before = host.Edit.Document.Copy();
        var start = host.BadgeCentre();
        host.Press(start);
        host.Window.MouseMove(start, RawInputModifiers.LeftMouseButton);
        host.Window.MouseMove(start, RawInputModifiers.LeftMouseButton);
        host.Window.MouseUp(start, MouseButton.Left);
        Assert.Multiple(() =>
        {
            Assert.That(host.Edit.Document.StateEquals(before), Is.True,
                $"A stationary click moved the badge from Y=190 to Y={host.Badge.Bounds.Y}.");
            Assert.That(host.Badge.Bounds.Y, Is.EqualTo(190));
            Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(new[] { host.BadgeId }));
            Assert.That(host.Edit.UndoCount, Is.Zero);
            Assert.That(host.Edit.RedoCount, Is.EqualTo(redo ? 1 : 0));
            Assert.That(host.Edit.IsDirty, Is.False);
            Assert.That(host.Canvas.IsInteracting, Is.False);
        });
    }

    [Test]
    public void ClickWithoutPointerMoveKeepsTheOriginalGeometry()
    {
        using var host = new Host(selected: false, redo: true);
        var before = host.Edit.Document.Copy();
        var start = host.BadgeCentre();
        host.Press(start);
        host.Window.MouseUp(start, MouseButton.Left);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
    }

    [TestCase(0.25)]
    [TestCase(1)]
    [TestCase(-0.25)]
    public void ActualSmallPointerDisplacementKeepsTheExistingSnapBehavior(double viewDeltaY)
    {
        using var host = new Host(selected: true, redo: false);
        var before = host.Edit.Document.Copy();
        var start = host.BadgeCentre();
        var end = new Point(start.X, start.Y + viewDeltaY);
        host.Press(start);
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        host.Window.MouseUp(end, MouseButton.Left);
        Assert.Multiple(() =>
        {
            Assert.That(host.Badge.Bounds.X, Is.EqualTo(800));
            Assert.That(host.Badge.Bounds.Y, Is.EqualTo(204));
            Assert.That(host.Badge.Bounds.Y + host.Badge.Bounds.Height, Is.EqualTo(420));
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        });
        Assert.That(host.Edit.Undo(), Is.True);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.Redo(), Is.True);
        Assert.That(host.Badge.Bounds.Y, Is.EqualTo(204));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ReturningAnActualMoveToItsExactStartRestoresGeometryAndRedo(bool bypassSnapping)
    {
        using var host = new Host(selected: true, redo: true);
        var before = host.Edit.Document.Copy();
        var start = host.BadgeCentre();
        var end = new Point(start.X + 35, start.Y + 20);
        var modifiers = RawInputModifiers.LeftMouseButton |
            (bypassSnapping ? RawInputModifiers.Alt : RawInputModifiers.None);
        host.Press(start);
        host.Window.MouseMove(end, modifiers);
        Assert.That(host.Edit.Document.StateEquals(before), Is.False);
        host.Window.MouseMove(start, modifiers);
        host.Window.MouseUp(start, MouseButton.Left);
        Assert.Multiple(() =>
        {
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
            Assert.That(host.Edit.UndoCount, Is.Zero);
            Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
            Assert.That(host.Edit.IsDirty, Is.False);
        });
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void SamePositionResizeMoveKeepsTheOriginalGeometryAndRedo(bool returning,
        bool bypassSnapping)
    {
        using var host = new Host(selected: true, redo: true);
        var before = host.Edit.Document.Copy();
        var start = host.Handle(ResizeHandle.BottomRight);
        var end = new Point(start.X + 35, start.Y + 20);
        var modifiers = RawInputModifiers.LeftMouseButton |
            (bypassSnapping ? RawInputModifiers.Alt : RawInputModifiers.None);
        host.Press(start);
        if (returning)
        {
            host.Window.MouseMove(end, modifiers);
            Assert.That(host.Edit.Document.StateEquals(before), Is.False);
        }
        host.Window.MouseMove(start, modifiers);
        host.Window.MouseUp(start, MouseButton.Left);
        Assert.Multiple(() =>
        {
            Assert.That(host.Edit.Document.StateEquals(before), Is.True,
                $"A stationary resize changed the badge bounds to {host.Badge.Bounds}.");
            Assert.That(host.Edit.UndoCount, Is.Zero);
            Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
            Assert.That(host.Edit.IsDirty, Is.False);
        });
    }

    [TestCase(0.25)]
    [TestCase(1)]
    [TestCase(-0.25)]
    public void ActualSmallResizeDisplacementKeepsTheExistingSnapBehavior(double viewDeltaY)
    {
        using var host = new Host(selected: true, redo: false);
        var before = host.Edit.Document.Copy();
        var start = host.Handle(ResizeHandle.BottomRight);
        var end = new Point(start.X, start.Y + viewDeltaY);
        host.Press(start);
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        host.Window.MouseUp(end, MouseButton.Left);
        Assert.Multiple(() =>
        {
            Assert.That(host.Badge.Bounds.X, Is.EqualTo(800));
            Assert.That(host.Badge.Bounds.Y, Is.EqualTo(190));
            Assert.That(host.Badge.Bounds.Y + host.Badge.Bounds.Height, Is.EqualTo(420).Within(1e-7));
            Assert.That(host.Badge.Bounds.Width / host.Badge.Bounds.Height, Is.EqualTo(256.0 / 216).Within(1e-7));
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        });
        Assert.That(host.Edit.Undo(), Is.True);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
    }

    [Test]
    public void ReleasingShiftAtTheStartLetsTheNextShiftMoveChooseAnotherAxis()
    {
        using var host = new Host(selected: true, redo: false);
        var before = host.Edit.Document.Copy();
        var start = host.BadgeCentre();
        var scale = host.Canvas.CurrentViewTransform.Scale;
        var horizontal = new Point(start.X + 30, start.Y);
        var vertical = new Point(start.X, start.Y + 30);
        var shift = RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift | RawInputModifiers.Alt;
        host.Press(start);
        host.Window.MouseMove(horizontal, shift);
        Assert.That(host.Badge.Bounds.X, Is.GreaterThan(800));
        host.Window.MouseMove(start, shift);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        host.Window.MouseMove(start, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        host.Window.MouseMove(vertical, shift);
        host.Window.MouseUp(vertical, MouseButton.Left);
        Assert.Multiple(() =>
        {
            Assert.That(host.Badge.Bounds.X, Is.EqualTo(800));
            Assert.That(host.Badge.Bounds.Y, Is.EqualTo(190 + 30 / scale).Within(1e-7));
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        });
        Assert.That(host.Edit.Undo(), Is.True);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
    }

    private sealed class Host : IDisposable
    {
        public EditSession Edit { get; }
        public SceneCanvas Canvas { get; }
        public Window Window { get; }
        public Guid BadgeId { get; }
        public Layer Badge => Edit.Document.Find(BadgeId);

        public Host(bool selected, bool redo)
        {
            AvaloniaTestHost.EnsureStarted();
            var document = new EditorDocument { CanvasWidth = 2740, CanvasHeight = 1080 };
            var asset = Guid.NewGuid();
            document.AddAsset(asset, TestImages.EncodePng(2, 2, TestImages.SyntheticBgra(2, 2)));
            // Geometry from the existing free-layout migration fixture. The badge's bottom
            // at 406 is within snapping distance of the text's top at 420 in a fitted view.
            document.Layers.Add(new Layer
            {
                Kind = LayerKind.Image, Name = "Image", AssetId = asset,
                Bounds = new RectD(50, 50, 500, 312.5)
            });
            document.Layers.Add(new Layer
            {
                Kind = LayerKind.Text, Name = "Text", Text = "中文 English",
                Bounds = new RectD(90, 420, 600, 120), FontSize = 48
            });
            var badge = EditorDocument.NewBadge(new PointD(800, 190));
            badge.Bounds = new RectD(800, 190, 256, 216);
            badge.Difficulty = "HD";
            badge.Level = 12;
            BadgeId = badge.Id;
            document.Layers.Add(badge);
            Edit = new EditSession(document);
            if (redo)
            {
                Edit.UpdateLayer(BadgeId, layer => layer.Name = "Retained redo");
                Assert.That(Edit.Undo(), Is.True);
            }
            if (selected) Edit.SelectedLayerId = BadgeId;
            Canvas = new SceneCanvas(Edit);
            Window = new Window { Width = 800, Height = 600, Content = Canvas };
            Window.Show();
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (true)
                {
                    Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
                    using var frame = Window.CaptureRenderedFrame();
                    if (frame is not null) break;
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "The test canvas did not render.");
                    Thread.Sleep(5);
                }
            }
            catch { Window.Close(); throw; }
        }

        public Point BadgeCentre()
        {
            return View(LayerGeometry.Centre(Badge.Bounds));
        }

        public Point Handle(ResizeHandle handle) => View(LayerGeometry.HandlePoint(Badge.Bounds, handle));

        private Point View(PointD logical)
        {
            var view = Canvas.CurrentViewTransform.ToView(logical);
            return Canvas.TranslatePoint(new Point(view.X, view.Y), Window)!.Value;
        }

        public void Press(Point start)
        {
            Assert.That(Window.InputHitTest(start), Is.SameAs(Canvas));
            Window.MouseDown(start, MouseButton.Left);
            Assert.That(Edit.ActiveGestureIds, Is.EqualTo(new[] { BadgeId }));
        }

        public void Dispose()
        {
            Edit.CancelGesture();
            Edit.MarkSaved();
            Window.Close();
        }
    }
}
