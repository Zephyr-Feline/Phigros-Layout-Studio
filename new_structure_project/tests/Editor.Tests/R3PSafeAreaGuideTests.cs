using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3PSafeAreaGuideTests
{
    [Test]
    public void AltBypassesSafeAreaSnapAndResizeHandleUsesIt()
    {
        AvaloniaTestHost.EnsureStarted();
        var layer = new Layer
        {
            Kind = LayerKind.Shape, Bounds = new RectD(100, 100, 100, 100)
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 1000, CanvasHeight = 600, Layers = [layer]
        });
        var canvas = new SceneCanvas(edit) { ShowSafeArea = true, SafeAreaMarginPercent = 10 };
        var window = new Window { Width = 800, Height = 600, Content = canvas };
        try
        {
            window.Show();
            var transform = new ViewportTransform(1000, 600, canvas.Bounds.Width, canvas.Bounds.Height);
            Point View(double x, double y)
            {
                var point = transform.ToView(new PointD(x, y));
                return new Point(point.X, point.Y);
            }
            var start = View(150, 150);
            var startLogical = transform.ToLogical(new PointD(start.X, start.Y));
            var target = transform.ToView(new PointD(startLogical.X - 35, startLogical.Y));
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(target.X, target.Y),
                RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            window.MouseUp(new Point(target.X, target.Y), MouseButton.Left);
            Assert.That(edit.Document.Find(layer.Id).Bounds.X, Is.EqualTo(65).Within(1e-6));

            edit.SelectedLayerId = layer.Id;
            window.MouseDown(View(65, 150), MouseButton.Left);
            window.MouseMove(View(55, 150), RawInputModifiers.LeftMouseButton);
            window.MouseUp(View(55, 150), MouseButton.Left);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(layer.Id).Bounds.X, Is.EqualTo(60).Within(1e-6));
                Assert.That(edit.Document.Find(layer.Id).Bounds.Width, Is.EqualTo(105).Within(1e-6));
            });
        }
        finally { window.Close(); }
    }

    [Test]
    public void SafeAreaEdgesParticipateInMoveSnappingWithoutChangingDocument()
    {
        AvaloniaTestHost.EnsureStarted();
        var layer = new Layer
        {
            Kind = LayerKind.Shape, Bounds = new RectD(100, 100, 100, 100)
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 1000, CanvasHeight = 600, Layers = [layer]
        });
        var canvas = new SceneCanvas(edit) { ShowSafeArea = true, SafeAreaMarginPercent = 10 };
        var window = new Window { Width = 800, Height = 600, Content = canvas };
        try
        {
            window.Show();
            var transform = new ViewportTransform(1000, 600,
                canvas.Bounds.Width, canvas.Bounds.Height);
            Point View(double x, double y)
            {
                var point = transform.ToView(new PointD(x, y));
                return new Point(point.X, point.Y);
            }
            var start = View(150, 150);
            var startLogical = transform.ToLogical(new PointD(start.X, start.Y));
            var targetLogical = new PointD(startLogical.X - 35, startLogical.Y);
            var target = transform.ToView(targetLogical);
            var targetPoint = new Point(target.X, target.Y);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(targetPoint, RawInputModifiers.LeftMouseButton);
            window.MouseUp(targetPoint, MouseButton.Left);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(layer.Id).Bounds.X, Is.EqualTo(60).Within(1e-6));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
                Assert.That(canvas.ManualGuides.Count, Is.Zero);
            });
        }
        finally { window.Close(); }
    }

    [Test]
    public void SafeAreaEdgesBecomeSessionGuidesWithoutChangingPngOrHistory()
    {
        AvaloniaTestHost.EnsureStarted();
        var path = Path.Combine(Path.GetTempPath(),
            "r3p-safe-guides-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var store = new EditorPreferencesStore(path);
        store.Save(new EditorPreferences
        {
            ShowSafeArea = true, SafeAreaMarginPercent = 10
        });
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [new Layer
            {
                Kind = LayerKind.Shape, Bounds = new RectD(100, 100, 200, 100)
            }]
        });
        var window = new MainWindow(edit, preferencesStore: store);
        try
        {
            window.Show();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var before = PngExporter.Render(edit.Document);
            UiTestActions.ClickNamed(window, "AddSafeAreaGuidesMenu");
            Assert.Multiple(() =>
            {
                Assert.That(canvas.ManualGuides.Positions(true),
                    Is.EquivalentTo(new[] { 60.0, 940.0 }));
                Assert.That(canvas.ManualGuides.Positions(false),
                    Is.EquivalentTo(new[] { 60.0, 540.0 }));
                Assert.That(edit.UndoCount, Is.Zero);
                Assert.That(edit.IsDirty, Is.False);
                Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(before));
            });
            UiTestActions.ClickNamed(window, "AddSafeAreaGuidesMenu");
            Assert.That(canvas.ManualGuides.Count, Is.EqualTo(4));
            Assert.That(UiTestActions.Status(window), Does.Contain("已经存在"));
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
