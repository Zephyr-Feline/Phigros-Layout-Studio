using System.IO.Compression;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class ReviewCanvasTransformTests
{
    [TestCase(ResizeHandle.TopLeft)] [TestCase(ResizeHandle.Top)]
    [TestCase(ResizeHandle.TopRight)] [TestCase(ResizeHandle.Right)]
    [TestCase(ResizeHandle.BottomRight)] [TestCase(ResizeHandle.Bottom)]
    [TestCase(ResizeHandle.BottomLeft)] [TestCase(ResizeHandle.Left)]
    public void EveryHandleResizesFromItsOppositeAnchorAndRecordsOneGesture(ResizeHandle handle)
    {
        var layer = Shape(); var edit = Session(layer);
        var (hx, hy) = LayerGeometry.HandleFraction(handle);
        var dx = hx == 0.5 ? 0 : hx == 0 ? -40 : 40;
        var dy = hy == 0.5 ? 0 : hy == 0 ? -20 : 20;
        var anchor = OppositeAnchor(layer.Bounds, handle, 0);
        edit.BeginGesture(layer.Id);
        edit.ResizeFromHandle(dx / 2, dy / 2, handle);
        edit.ResizeFromHandle(dx, dy, handle);
        edit.CommitGesture();
        var changed = edit.Document.Find(layer.Id);
        AssertPoint(OppositeAnchor(changed.Bounds, handle, 0), anchor);
        Assert.Multiple(() =>
        {
            Assert.That(changed.Bounds.Width, Is.EqualTo(hx == 0.5 ? 200 : 240).Within(1e-8));
            Assert.That(changed.Bounds.Height, Is.EqualTo(hy == 0.5 ? 100 : 120).Within(1e-8));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(layer.Id).Bounds, Is.EqualTo(layer.Bounds));
            Assert.That(edit.Redo(), Is.True);
        });
    }

    [TestCase(ResizeHandle.TopLeft)] [TestCase(ResizeHandle.Top)]
    [TestCase(ResizeHandle.TopRight)] [TestCase(ResizeHandle.Right)]
    [TestCase(ResizeHandle.BottomRight)] [TestCase(ResizeHandle.Bottom)]
    [TestCase(ResizeHandle.BottomLeft)] [TestCase(ResizeHandle.Left)]
    public void RotatedHandleUsesLocalAxesAndKeepsTheWorldAnchor(ResizeHandle handle)
    {
        var layer = Shape(); layer.RotationDegrees = 33;
        var edit = Session(layer); var (hx, hy) = LayerGeometry.HandleFraction(handle);
        var delta = LayerGeometry.Rotate(new(hx == 0.5 ? 0 : hx == 0 ? -40 : 40,
            hy == 0.5 ? 0 : hy == 0 ? -20 : 20), default, layer.RotationDegrees);
        var anchor = OppositeAnchor(layer.Bounds, handle, layer.RotationDegrees);
        edit.BeginGesture(layer.Id); edit.ResizeFromHandle(delta.X, delta.Y, handle); edit.CommitGesture();
        var resized = edit.Document.Find(layer.Id);
        AssertPoint(OppositeAnchor(resized.Bounds, handle, resized.RotationDegrees), anchor);
        Assert.That(resized.RotationDegrees, Is.EqualTo(33));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [TestCase(1000, 600)] [TestCase(1000, 750)]
    public void EightVisibleHandlesWorkThroughActualPointerEvents(int width, int height)
    {
        foreach (var handle in Enum.GetValues<ResizeHandle>())
        {
            var layer = Shape(); var edit = Session(layer, width, height); edit.SelectedLayerId = layer.Id;
            using var host = new CanvasHost(edit);
            var start = LayerGeometry.HandlePoint(layer.Bounds, handle);
            var (hx, hy) = LayerGeometry.HandleFraction(handle);
            var end = new PointD(start.X + (hx == 0.5 ? 0 : hx == 0 ? -40 : 40),
                start.Y + (hy == 0.5 ? 0 : hy == 0 ? -20 : 20));
            host.Press(start);
            host.Window.MouseMove(host.View(end), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            host.Window.MouseUp(host.View(end), MouseButton.Left);
            Assert.That(edit.UndoCount, Is.EqualTo(1), handle.ToString());
            Assert.That(edit.Document.Find(layer.Id).Bounds.Width,
                Is.EqualTo(hx == 0.5 ? 200 : 240).Within(1e-7), handle.ToString());
            Assert.That(edit.Document.Find(layer.Id).Bounds.Height,
                Is.EqualTo(hy == 0.5 ? 100 : 120).Within(1e-7), handle.ToString());
        }
    }

    [TestCase(ResizeHandle.Left)] [TestCase(ResizeHandle.Top)]
    [TestCase(ResizeHandle.BottomRight)]
    public void ShiftTemporarilyPreservesRatioAndDoesNotChangeKeepAspect(ResizeHandle handle)
    {
        var layer = Shape(); var edit = Session(layer); edit.SelectedLayerId = layer.Id;
        using var host = new CanvasHost(edit);
        var start = LayerGeometry.HandlePoint(layer.Bounds, handle);
        var end = new PointD(start.X + (handle == ResizeHandle.Left ? -80 : 80),
            start.Y + (handle == ResizeHandle.Top ? -15 : 15));
        host.Press(start, RawInputModifiers.Shift);
        host.Window.MouseMove(host.View(end), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt | RawInputModifiers.Shift);
        host.Window.MouseUp(host.View(end), MouseButton.Left);
        var resized = edit.Document.Find(layer.Id);
        Assert.That(resized.Bounds.Width / resized.Bounds.Height, Is.EqualTo(2).Within(1e-8));
        Assert.That(resized.KeepAspect, Is.False);
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [TestCase(ResizeHandle.Left)] [TestCase(ResizeHandle.Top)] [TestCase(ResizeHandle.TopLeft)]
    public void NewEdgesSnapToStationaryLayersWithTheOppositeAnchorFixed(ResizeHandle handle)
    {
        var layer = Shape(); var target = new Layer { Kind = LayerKind.Shape,
            Bounds = new(160, 160, 10, 10), Locked = true };
        var edit = Session(layer); edit.Document.Layers.Add(target);
        var dx = handle == ResizeHandle.Top ? 0 : -38;
        var dy = handle == ResizeHandle.Left ? 0 : -38;
        var anchor = OppositeAnchor(layer.Bounds, handle, 0);
        var snap = ResizeSnapping.Solve(edit.Document, [layer.Id], layer.Bounds, dx, dy, 4, 4,
            uniform: false, handle);
        edit.BeginGesture(layer.Id); edit.ResizeFromHandle(snap.Delta.X, snap.Delta.Y, handle); edit.CommitGesture();
        AssertPoint(OppositeAnchor(edit.Document.Find(layer.Id).Bounds, handle, 0), anchor);
        if (handle != ResizeHandle.Top) Assert.That(snap.VerticalGuide, Is.EqualTo(160));
        if (handle != ResizeHandle.Left) Assert.That(snap.HorizontalGuide, Is.EqualTo(160));
    }

    [TestCase("escape")] [TestCase("capture")]
    public void RotationCanCancelOrCommitAsOneUndoableGesture(string cancellation)
    {
        var layer = Shape(); var edit = Session(layer); edit.SelectedLayerId = layer.Id;
        using var host = new CanvasHost(edit);
        var pivot = LayerGeometry.Centre(layer.Bounds);
        var start = new PointD(pivot.X, layer.Bounds.Y - 24 / host.Transform.Scale);
        var end = LayerGeometry.Rotate(start, pivot, 90);
        host.Press(start);
        host.Window.MouseMove(host.View(end), RawInputModifiers.LeftMouseButton);
        Assert.That(edit.Document.Find(layer.Id).RotationDegrees, Is.EqualTo(90).Within(1e-7));
        if (cancellation == "escape")
            host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        else
        {
            var pointer = (IPointer)typeof(SceneCanvas).GetField("_capturedPointer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host.Canvas)!;
            pointer.Capture(null);
        }
        Assert.That(edit.Document.Find(layer.Id).StateEquals(layer), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
        host.Press(start);
        host.Window.MouseMove(host.View(end), RawInputModifiers.LeftMouseButton);
        var beforeRelease = PngExporter.Render(edit.Document);
        host.Window.MouseUp(host.View(end), MouseButton.Left);
        Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(beforeRelease));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        edit.Undo(); Assert.That(edit.Document.Find(layer.Id).StateEquals(layer), Is.True);
    }

    [Test]
    public void RotationShiftSnapsToFifteenDegreesAndGroupLeavesLockedMembersUntouched()
    {
        var first = Shape(); var group = Guid.NewGuid(); first.GroupId = group;
        var second = Shape(); second.Bounds = new(500, 200, 200, 100); second.GroupId = group;
        var locked = Shape(); locked.Bounds = new(800, 200, 100, 100); locked.GroupId = group; locked.Locked = true;
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [first, second, locked] }); edit.SelectedLayerId = first.Id;
        using var host = new CanvasHost(edit);
        var bounds = MoveSnapping.BoundsOf([first.Bounds, second.Bounds]); var pivot = LayerGeometry.Centre(bounds);
        var start = new PointD(pivot.X, bounds.Y - 24 / host.Transform.Scale);
        var end = LayerGeometry.Rotate(start, pivot, 37);
        host.Press(start);
        host.Window.MouseMove(host.View(end), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
        host.Window.MouseUp(host.View(end), MouseButton.Left);
        Assert.That(edit.Document.Find(first.Id).RotationDegrees, Is.EqualTo(30).Within(1e-7));
        Assert.That(edit.Document.Find(second.Id).RotationDegrees, Is.EqualTo(30).Within(1e-7));
        Assert.That(edit.Document.Find(locked.Id).StateEquals(locked), Is.True);
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [TestCase(1000, 600)] [TestCase(1000, 750)]
    public void RotatedPreviewPngAndHitTestAgreeInLogicalCanvasSpace(int width, int height)
    {
        var shape = Shape(); shape.RotationDegrees = 90;
        var edit = Session(shape, width, height); using var host = new CanvasHost(edit);
        var inside = new PointD(270, 160); var outside = new PointD(210, 210);
        Assert.That(LayerGeometry.Contains(shape, inside), Is.True);
        Assert.That(LayerGeometry.Contains(shape, outside), Is.False);
        var (_, _, export) = TestImages.DecodePng(TestImages.RenderPng(edit.Document, true));
        Assert.That(export[((int)inside.Y * width + (int)inside.X) * 4 + 3], Is.EqualTo(255));
        Assert.That(export[((int)outside.Y * width + (int)outside.X) * 4 + 3], Is.Zero);
        using var target = new RenderTargetBitmap(new(800, 600), new(96, 96));
        using (var context = target.CreateDrawingContext()) host.Canvas.Render(context);
        using var stream = new MemoryStream(); target.Save(stream, new PngBitmapEncoderOptions());
        var (_, _, preview) = TestImages.DecodePng(stream.ToArray()); var point = host.View(inside);
        Assert.That(preview[((int)point.Y * 800 + (int)point.X) * 4 + 2], Is.EqualTo(255));
        host.Press(inside); host.Window.MouseUp(host.View(inside), MouseButton.Left);
        Assert.That(edit.SelectedLayerId, Is.EqualTo(shape.Id));
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void SlantedCornersRemainTransparentAfterRotationAndMarqueeUsesRotatedCorners()
    {
        var layer = Shape(); layer.Slanted = true; layer.RotationDegrees = 33;
        var edit = Session(layer); var centre = LayerGeometry.Centre(layer.Bounds);
        Assert.That(LayerGeometry.Contains(layer, LayerGeometry.Rotate(new(202, 202), centre, 33)), Is.False);
        Assert.That(LayerGeometry.Contains(layer, centre), Is.True);
        edit.SelectCanvasRegion(layer.Bounds); Assert.That(edit.SelectedLayerIds, Is.Empty);
        edit.SelectCanvasRegion(LayerGeometry.VisualBounds(layer)); Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { layer.Id }));
    }

    [Test]
    public void RotationChangesBackgroundWeightsAndCacheMatchesFreshComposition()
    {
        var document = new EditorDocument { CanvasWidth = 400, CanvasHeight = 300,
            BackgroundBlend = new(true, BlurRadius: 0, Darkness: 0, IncludeFlatImages: true) };
        var asset = Guid.NewGuid(); document.AddAsset(asset, P4AutomaticBackgroundTests.Solid(0xFFFF0000));
        document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = asset, Bounds = new(20, 80, 300, 50) });
        var cache = new BackgroundRenderCache(); var original = cache.Get(document, TestImages.Decoder)!;
        document.Layers[0].RotationDegrees = 90;
        var rotated = cache.Get(document, TestImages.Decoder)!;
        Assert.That(rotated, Is.Not.SameAs(original));
        Assert.That(rotated.ToArray(), Is.EqualTo(BackgroundComposer.Generate(document, TestImages.Decoder)));
        Assert.That(cache.Get(document, TestImages.Decoder), Is.SameAs(rotated));
    }

    private static PointD OppositeAnchor(RectD box, ResizeHandle handle, double angle)
    {
        var (hx, hy) = LayerGeometry.HandleFraction(handle);
        return LayerGeometry.Rotate(new(box.X + box.Width * (1 - hx), box.Y + box.Height * (1 - hy)),
            LayerGeometry.Centre(box), angle);
    }
    private static void AssertPoint(PointD actual, PointD expected)
    {
        Assert.That(actual.X, Is.EqualTo(expected.X).Within(1e-7));
        Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(1e-7));
    }
    private static Layer Shape() => new() { Kind = LayerKind.Shape, Bounds = new(200, 200, 200, 100) };
    private static EditSession Session(Layer layer, int width = 1000, int height = 600) =>
        new(new EditorDocument { CanvasWidth = width, CanvasHeight = height, Layers = [layer] });

    private sealed class CanvasHost : IDisposable
    {
        public SceneCanvas Canvas { get; }
        public Window Window { get; }
        public ViewportTransform Transform => Canvas.CurrentViewTransform;
        public CanvasHost(EditSession edit)
        {
            AvaloniaTestHost.EnsureStarted(); Canvas = new(edit);
            Window = new() { Width = 800, Height = 600, Content = Canvas }; Window.Show();
            // Show alone does not guarantee the first layout and renderer hit area are ready.
            // Render one frame before pointer input; use the canvas's live transform thereafter.
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (true)
                {
                    Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
                    using var frame = Window.CaptureRenderedFrame();
                    if (frame is not null) break;
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline),
                        "The canvas host must render before pointer input.");
                    // The headless renderer runs on its own thread. Wait for readiness;
                    // pointer actions and assertions are never retried.
                    Thread.Sleep(5);
                }
            }
            catch { Window.Close(); throw; }
        }
        public Point View(PointD logical)
        {
            var point = Transform.ToView(logical);
            return Canvas.TranslatePoint(new Point(point.X, point.Y), Window)!.Value;
        }
        public void Press(PointD logical, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            var at = View(logical);
            Assert.That(Window.InputHitTest(at), Is.SameAs(Canvas),
                $"Canvas pointer target at {at}; canvas bounds={Canvas.Bounds}, scale={Transform.Scale}.");
            Window.MouseDown(at, MouseButton.Left, modifiers);
        }
        public void Dispose() => Window.Close();
    }
}
