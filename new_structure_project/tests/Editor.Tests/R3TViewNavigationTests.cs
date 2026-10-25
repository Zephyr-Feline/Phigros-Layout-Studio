using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3TViewNavigationTests
{
    [Test]
    public void ZoomCommandsKeepThePointerAnchorAndNeverEditTheDocument()
    {
        var edit = Session();
        using var host = new CanvasHost(edit, 900, 600);
        var before = PngExporter.Render(edit.Document);
        var anchor = new Point(173, 241);
        var logicalBefore = host.Canvas.CurrentViewTransform.ToLogical(
            new PointD(anchor.X, anchor.Y));

        Assert.That(host.Canvas.ZoomBy(1.25, anchor), Is.True);
        var logicalAfter = host.Canvas.CurrentViewTransform.ToLogical(
            new PointD(anchor.X, anchor.Y));
        Assert.Multiple(() =>
        {
            Assert.That(logicalAfter.X, Is.EqualTo(logicalBefore.X).Within(1e-6));
            Assert.That(logicalAfter.Y, Is.EqualTo(logicalBefore.Y).Within(1e-6));
            Assert.That(host.Canvas.ZoomToActualSize(), Is.True);
            Assert.That(host.Canvas.CurrentViewTransform.Scale, Is.EqualTo(1).Within(1e-9));
            Assert.That(host.Canvas.ZoomPercent, Is.EqualTo(100).Within(1e-9));
            Assert.That(host.Canvas.ZoomIn(), Is.True);
            Assert.That(host.Canvas.CurrentViewTransform.Scale, Is.GreaterThan(1));
            Assert.That(host.Canvas.ZoomOut(), Is.True);
            Assert.That(host.Canvas.CurrentViewTransform.Scale, Is.EqualTo(1).Within(1e-9));
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
            Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(before));
        });
    }

    [Test]
    public void MaximumSupportedCanvasCanReachActualSizeInACompactView()
    {
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 7680,
            CanvasHeight = 4320,
            Layers = [new Layer { Kind = LayerKind.Shape, Bounds = new RectD(100, 100, 200, 160) }]
        });
        using var host = new CanvasHost(edit, 900, 600);
        Assert.That(host.Canvas.ZoomToActualSize(), Is.True);
        Assert.That(host.Canvas.CurrentViewTransform.Scale, Is.EqualTo(1).Within(1e-9));
        Assert.That(host.Canvas.ZoomPercent, Is.EqualTo(100).Within(1e-9));
    }

    [Test]
    public void FitSelectionUsesTheRotatedVisualUnionAndCentresIt()
    {
        var layers = new[]
        {
            new Layer { Kind = LayerKind.Shape, Name = "left",
                Bounds = new RectD(100, 200, 360, 220) },
            new Layer { Kind = LayerKind.Shape, Name = "rotated",
                Bounds = new RectD(1150, 430, 420, 210), RotationDegrees = 45 }
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 2000, CanvasHeight = 1000, Layers = layers.ToList()
        });
        edit.SetSelection(layers.Select(layer => layer.Id));
        using var host = new CanvasHost(edit, 900, 600);
        var union = MoveSnapping.BoundsOf(layers.Select(LayerGeometry.VisualBounds));

        Assert.That(host.Canvas.ZoomToSelection(), Is.True);
        var projected = host.Canvas.CurrentViewTransform.ToView(union);
        Assert.Multiple(() =>
        {
            Assert.That(projected.X + projected.Width / 2, Is.EqualTo(450).Within(1e-6));
            Assert.That(projected.Y + projected.Height / 2, Is.EqualTo(300).Within(1e-6));
            Assert.That(projected.X, Is.GreaterThanOrEqualTo(0));
            Assert.That(projected.Y, Is.GreaterThanOrEqualTo(0));
            Assert.That(projected.X + projected.Width, Is.LessThanOrEqualTo(900));
            Assert.That(projected.Y + projected.Height, Is.LessThanOrEqualTo(600));
        });

        var scale = host.Canvas.CurrentViewTransform.Scale;
        edit.SetSelection([]);
        Assert.That(host.Canvas.ZoomToSelection(), Is.False);
        Assert.That(host.Canvas.CurrentViewTransform.Scale, Is.EqualTo(scale));
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void ZoomCommandsAreRejectedDuringACanvasGesture()
    {
        var edit = Session();
        using var host = new CanvasHost(edit, 900, 600);
        var empty = host.Canvas.CurrentViewTransform.ToView(new PointD(700, 500));
        host.Window.MouseDown(new Point(empty.X, empty.Y), MouseButton.Left);
        Assert.That(host.Canvas.IsInteracting, Is.True);
        var scale = host.Canvas.CurrentViewTransform.Scale;
        Assert.That(host.Canvas.ZoomIn(), Is.False);
        Assert.That(host.Canvas.ZoomToActualSize(), Is.False);
        Assert.That(host.Canvas.CurrentViewTransform.Scale, Is.EqualTo(scale));
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        host.Window.MouseUp(new Point(empty.X, empty.Y), MouseButton.Left);
        Assert.That(host.Canvas.IsInteracting, Is.False);
    }

    [Test]
    public void MainWindowZoomButtonsAndShortcutsUpdateFeedbackWithoutHistory()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var edit = Session();
        edit.SetSelection([edit.Document.Layers[0].Id]);
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            window.UpdateLayout();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var status = window.GetLogicalDescendants().OfType<TextBlock>()
                .Single(text => text.Name == "ZoomStatus");
            var initial = canvas.CurrentViewTransform.Scale;

            UiTestActions.ClickNamed(window, "ZoomOutMenu");
            Assert.That(canvas.CurrentViewTransform.Scale, Is.LessThan(initial));
            Assert.That(status.Text, Does.StartWith("缩放 "));

            UiTestActions.ClickNamed(window, "ZoomActualSizeMenu");
            Assert.That(canvas.CurrentViewTransform.Scale, Is.EqualTo(1).Within(1e-9));
            Assert.That(status.Text, Is.EqualTo("缩放 100%"));

            UiTestActions.ClickNamed(window, "ZoomToSelectionMenu");
            Assert.That(UiTestActions.Status(window), Is.EqualTo("已适合所选图层。"));
            Assert.That(status.Text, Does.StartWith("缩放 "));

            canvas.Focus();
            window.KeyPress(Key.D1, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(canvas.CurrentViewTransform.Scale, Is.EqualTo(1).Within(1e-9));
            window.KeyPress(Key.D2, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(UiTestActions.Status(window), Is.EqualTo("已适合所选图层。"));
            window.KeyPress(Key.D0, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(canvas.CurrentViewTransform.Scale, Is.EqualTo(initial).Within(1e-9));
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static EditSession Session() => new(new EditorDocument
    {
        CanvasWidth = 1000,
        CanvasHeight = 600,
        Layers =
        [
            new Layer { Kind = LayerKind.Shape, Name = "first",
                Bounds = new RectD(100, 100, 260, 160) },
            new Layer { Kind = LayerKind.Shape, Name = "second",
                Bounds = new RectD(620, 260, 220, 180), RotationDegrees = 30 }
        ]
    });

    private sealed class CanvasHost : IDisposable
    {
        public SceneCanvas Canvas { get; }
        public Window Window { get; }

        public CanvasHost(EditSession edit, double width, double height)
        {
            AvaloniaTestHost.EnsureStarted();
            Canvas = new SceneCanvas(edit);
            Window = new Window { Width = width, Height = height, Content = Canvas };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public void Dispose() => Window.Close();
    }
}
