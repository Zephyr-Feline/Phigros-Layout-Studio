using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4CanvasInteractionTests
{
    [Test]
    public void MainWindowWithLayerActionsOpensInHeadlessHost()
    {
        AvaloniaTestHost.EnsureStarted();
        var window = new MainWindow();
        try
        {
            window.Show();
            Assert.That(window.Bounds.Width, Is.GreaterThan(0));
            Assert.That(window.Title, Does.Contain("未命名"));
        }
        finally { window.Close(); }
    }

    [Test]
    public void HeadlessPointerUsesSnapAltAndEscapeWithinOneGesture()
    {
        AvaloniaTestHost.EnsureStarted();
        var mover = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(100, 100, 100, 100) };
        var target = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(300, 300, 100, 100), Locked = true };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [mover, target] });
        var canvas = new SceneCanvas(edit);
        var window = new Window { Width = 800, Height = 600, Content = canvas };
        try
        {
            window.Show();
            Assert.That(canvas.Bounds.Width, Is.GreaterThan(0));
            var transform = new ViewportTransform(1000, 600, canvas.Bounds.Width, canvas.Bounds.Height);
            Point View(double x, double y)
            {
                var point = transform.ToView(new PointD(x, y));
                return new Point(point.X, point.Y);
            }

            var start = View(150, 150);
            var nearTarget = View(345, 150);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(nearTarget, RawInputModifiers.LeftMouseButton);
            Assert.That(edit.Document.Find(mover.Id).Bounds.X, Is.EqualTo(300).Within(1e-6));
            window.MouseMove(nearTarget, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            Assert.That(edit.Document.Find(mover.Id).Bounds.X, Is.EqualTo(295).Within(1e-6));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(mover.Id).Bounds.X, Is.EqualTo(100));
                Assert.That(edit.UndoCount, Is.Zero);
            });

            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(nearTarget, RawInputModifiers.LeftMouseButton);
            var withGuide = PngExporter.Render(edit.Document);
            window.MouseUp(nearTarget, MouseButton.Left);
            var afterRelease = PngExporter.Render(edit.Document);
            Assert.That(edit.Document.Find(mover.Id).Bounds.X, Is.EqualTo(300).Within(1e-6));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(afterRelease, Is.EqualTo(withGuide), "preview-only snap guides must not enter PNG export");
        }
        finally { window.Close(); }
    }

    [Test]
    public void HeadlessGroupToggleChangesOnlyTheMoveTargetSet()
    {
        AvaloniaTestHost.EnsureStarted();
        var group = Guid.NewGuid();
        var first = new Layer { Kind = LayerKind.Shape, GroupId = group,
            Bounds = new RectD(100, 100, 100, 100) };
        var second = new Layer { Kind = LayerKind.Shape, GroupId = group,
            Bounds = new RectD(250, 100, 100, 100) };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [first, second] });
        var canvas = new SceneCanvas(edit);
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

            window.MouseDown(View(150, 150), MouseButton.Left);
            window.MouseMove(View(230, 150), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            window.MouseUp(View(230, 150), MouseButton.Left);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(first.Id).Bounds.X, Is.EqualTo(180).Within(1e-6));
                Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(330).Within(1e-6));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });

            canvas.MoveWholeGroup = false;
            window.MouseDown(View(230, 150), MouseButton.Left);
            window.MouseMove(View(280, 150), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            window.MouseUp(View(280, 150), MouseButton.Left);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(first.Id).Bounds.X, Is.EqualTo(230).Within(1e-6));
                Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(330).Within(1e-6));
                Assert.That(edit.UndoCount, Is.EqualTo(2));
            });
        }
        finally { window.Close(); }
    }

    [TestCase(1000, 600)]
    [TestCase(1000, 750)]
    public void HeadlessUnionHandleResizesSelectionAndAltDoesNotChangeIt(int canvasWidth, int canvasHeight)
    {
        AvaloniaTestHost.EnsureStarted();
        var first = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(100, 100, 100, 100) };
        var second = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(300, 100, 100, 100) };
        var edit = new EditSession(new EditorDocument { CanvasWidth = canvasWidth, CanvasHeight = canvasHeight,
            Layers = [first, second] });
        edit.SetSelection([first.Id, second.Id]);
        var canvas = new SceneCanvas(edit);
        var window = new Window { Width = 800, Height = 600, Content = canvas };
        try
        {
            window.Show();
            var beforePng = PngExporter.Render(edit.Document);
            var transform = new ViewportTransform(canvasWidth, canvasHeight, canvas.Bounds.Width, canvas.Bounds.Height);
            Point View(double x, double y)
            {
                var point = transform.ToView(new PointD(x, y));
                return new Point(point.X, point.Y);
            }
            var handle = View(400, 200);
            var end = View(550, 250);
            window.MouseDown(handle, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            window.MouseUp(end, MouseButton.Left);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(first.Id).Bounds.Width, Is.EqualTo(150).Within(1e-6));
                Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(400).Within(1e-6));
                Assert.That(edit.Document.Find(second.Id).Bounds.Width, Is.EqualTo(150).Within(1e-6));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            var resizedPng = PngExporter.Render(edit.Document);
            Assert.That(resizedPng, Is.Not.EqualTo(beforePng));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(second.Id).Bounds.X, Is.EqualTo(300));
            Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(beforePng));
            Assert.That(edit.Redo(), Is.True);
            Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(resizedPng));
        }
        finally { window.Close(); }
    }

    [Test]
    public void HeadlessPropertyPanelEditsBadgeAtomicallyAndRejectsInvalidLevel()
    {
        AvaloniaTestHost.EnsureStarted();
        var badge = EditorDocument.NewBadge(new PointD(100, 120));
        var edit = new EditSession(new EditorDocument { Layers = [badge] });
        edit.SelectedLayerId = badge.Id;
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            TextBox Field(string name) => window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == name);
            var apply = UiTestActions.FindNamed(window, "ApplyLayerProperties");
            Field("LayerX").Text = "210.5";
            Field("LayerWidth").Text = "256";
            Field("LayerLevel").Text = "17";
            UiTestActions.Click(apply);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(badge.Id).Bounds,
                    Is.EqualTo(new RectD(210.5, 120, 256, 216)));
                Assert.That(edit.Document.Find(badge.Id).Level, Is.EqualTo(17));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            Field("LayerLevel").Text = "0";
            UiTestActions.Click(apply);
            Assert.That(edit.Document.Find(badge.Id).Level, Is.EqualTo(17));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(badge.Id).Bounds.X, Is.EqualTo(100));
        }
        finally { window.Close(); }
    }

    [Test]
    public void HeadlessPropertyPanelEditsTextStyleAndRollsBackInvalidColor()
    {
        AvaloniaTestHost.EnsureStarted();
        var textLayer = new Layer { Kind = LayerKind.Text, Name = "Title",
            Bounds = new RectD(20, 30, 200, 80), Text = "Song" };
        var edit = new EditSession(new EditorDocument { Layers = [textLayer] });
        edit.SelectedLayerId = textLayer.Id;
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            TextBox Field(string name) => window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == name);
            var apply = UiTestActions.FindNamed(window, "ApplyLayerProperties");
            Field("LayerFontSize").Text = "64.5";
            Field("LayerLineSpacing").Text = "1.25";
            Field("LayerColor").Text = "FFCF1313";
            window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(check => check.Name == "LayerBold").IsChecked = true;
            window.GetLogicalDescendants().OfType<ComboBox>()
                .Single(combo => combo.Name == "LayerAlignment").SelectedItem = HorizontalAlignment.Right;
            UiTestActions.Click(apply);
            Assert.Multiple(() =>
            {
                var layer = edit.Document.Find(textLayer.Id);
                Assert.That(layer.FontSize, Is.EqualTo(64.5));
                Assert.That(layer.LineSpacing, Is.EqualTo(1.25));
                Assert.That(layer.ColorArgb, Is.EqualTo(0xFFCF1313u));
                Assert.That(layer.Bold, Is.True);
                Assert.That(layer.Alignment, Is.EqualTo(HorizontalAlignment.Right));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            Field("LayerColor").Text = "bad";
            UiTestActions.Click(apply);
            Assert.That(edit.Document.Find(textLayer.Id).ColorArgb, Is.EqualTo(0xFFCF1313u));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        }
        finally { window.Close(); }
    }

    [Test]
    public void HeadlessCanvasSettingsResizeWithoutChangingLayerCoordinatesAndUndoAsOneEdit()
    {
        AvaloniaTestHost.EnsureStarted();
        var shape = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(80, 60, 100, 50) };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [shape] });
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            TextBox Field(string name) => window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == name);
            var apply = UiTestActions.FindNamed(window, "ApplyCanvasSettings");
            Field("CanvasWidth").Text = "1200";
            Field("CanvasHeight").Text = "900";
            Field("BackgroundColor").Text = "FF203040";
            UiTestActions.Click(apply);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.CanvasWidth, Is.EqualTo(1200));
                Assert.That(edit.Document.CanvasHeight, Is.EqualTo(900));
                Assert.That(edit.Document.BackgroundArgb, Is.EqualTo(0xFF203040u));
                Assert.That(edit.Document.Find(shape.Id).Bounds, Is.EqualTo(shape.Bounds));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            var exported = TestImages.DecodePng(TestImages.RenderPng(edit.Document));
            Assert.That((exported.Width, exported.Height), Is.EqualTo((1200, 900)));
            Assert.That(exported.Bgra[..4], Is.EqualTo(new byte[] { 0x40, 0x30, 0x20, 0xFF }));

            Field("CanvasWidth").Text = "200";
            UiTestActions.Click(apply);
            Assert.That(edit.Document.CanvasWidth, Is.EqualTo(1200));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.Undo(), Is.True);
            Assert.That((edit.Document.CanvasWidth, edit.Document.CanvasHeight), Is.EqualTo((1000, 600)));
            Assert.That(edit.Redo(), Is.True);
            Assert.That((edit.Document.CanvasWidth, edit.Document.CanvasHeight), Is.EqualTo((1200, 900)));
        }
        finally { window.Close(); }
    }

    [Test]
    public void HeadlessLayoutButtonArrangesSelectedLayersAsOneUndoableEdit()
    {
        AvaloniaTestHost.EnsureStarted();
        var layers = Enumerable.Range(0, 3).Select(index => new Layer
        {
            Kind = LayerKind.Shape, Name = $"Song {index}", Bounds = new RectD(100, 100, 200, 120)
        }).ToArray();
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1440, CanvasHeight = 1080,
            Layers = layers.ToList() });
        edit.SetSelection(layers.Select(layer => layer.Id));
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var apply = UiTestActions.FindNamed(window, "ApplyLayoutTemplate");
            UiTestActions.Click(apply);
            var positions = layers.Select(layer => edit.Document.Find(layer.Id).Bounds.X).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(positions, Is.Ordered.Ascending);
                Assert.That(positions.Distinct().Count(), Is.EqualTo(3));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            Assert.That(edit.Undo(), Is.True);
            Assert.That(layers.All(layer => edit.Document.Find(layer.Id).Bounds.X == 100), Is.True);
        }
        finally { window.Close(); }
    }
}
