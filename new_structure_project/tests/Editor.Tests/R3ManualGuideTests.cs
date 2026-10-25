using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3ManualGuideTests
{
    private static Layer Shape(double x, double y, double width = 100, double height = 100) => new()
    {
        Kind = LayerKind.Shape, Bounds = new RectD(x, y, width, height)
    };

    [Test]
    public void GuideSetValidatesRangeDuplicatesPruningAndEvents()
    {
        var guides = new ManualGuideSet();
        var changes = 0;
        guides.Changed += () => changes++;
        Assert.Multiple(() =>
        {
            Assert.That(guides.Add(true, 100, 1000, 600), Is.EqualTo(ManualGuideAddResult.Added));
            Assert.That(guides.Add(true, 100, 1000, 600), Is.EqualTo(ManualGuideAddResult.Duplicate));
            Assert.That(guides.Add(true, 1001, 1000, 600), Is.EqualTo(ManualGuideAddResult.OutOfRange));
            Assert.That(guides.Add(false, 600, 1000, 600), Is.EqualTo(ManualGuideAddResult.Added));
            Assert.That(guides.Count, Is.EqualTo(2));
        });
        Assert.That(guides.Move(0, 200, 1000, 600), Is.True);
        Assert.That(guides.Items[0].Position, Is.EqualTo(200));
        Assert.That(guides.Move(0, 600, 1000, 600), Is.True);
        Assert.That(guides.Move(0, 1001, 1000, 600), Is.False);
        Assert.That(guides.Move(0, -0.1, 1000, 600), Is.False);
        Assert.That(guides.Positions(true), Is.EqualTo(new[] { 600.0 }));
        Assert.That(guides.Prune(400, 400), Is.EqualTo(2));
        Assert.That(guides.Count, Is.Zero);
        Assert.That(changes, Is.EqualTo(5));

        for (var index = 0; index < ManualGuideSet.MaxGuides; index++)
            Assert.That(guides.Add(true, index + 1, 1000, 600),
                Is.EqualTo(ManualGuideAddResult.Added));
        Assert.That(guides.Add(true, 999, 1000, 600),
            Is.EqualTo(ManualGuideAddResult.LimitReached));
        Assert.That(guides.Clear(), Is.EqualTo(ManualGuideSet.MaxGuides));
    }

    [Test]
    public void MoveSnappingUsesManualGuidesWithinThePhysicalPixelThreshold()
    {
        var moving = Shape(100, 100);
        var stationary = Shape(345, 300, 100, 40);
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving, stationary] };
        var guides = new ManualGuideSet();
        Assert.That(guides.Add(true, 340, 1000, 600), Is.EqualTo(ManualGuideAddResult.Added));
        var snap = MoveSnapping.Solve(document, [moving.Id], moving.Bounds,
            241, 0, 8, 8, guides);
        Assert.Multiple(() =>
        {
            Assert.That(snap.Delta.X, Is.EqualTo(240));
            Assert.That(snap.VerticalGuide, Is.EqualTo(340));
        });

        var outside = MoveSnapping.Solve(document, [moving.Id], moving.Bounds,
            241, 0, 2, 2, guides);
        Assert.Multiple(() =>
        {
            Assert.That(outside.Delta.X, Is.EqualTo(240));
            Assert.That(outside.VerticalGuide, Is.EqualTo(340),
                "a closer manual guide still wins inside a narrower tolerance");
        });
        Assert.That(guides.Move(0, 330, 1000, 600), Is.True);
        var layerWins = MoveSnapping.Solve(document, [moving.Id], moving.Bounds,
            241, 0, 6, 6, guides);
        Assert.Multiple(() =>
        {
            Assert.That(layerWins.Delta.X, Is.EqualTo(245));
            Assert.That(layerWins.VerticalGuide, Is.EqualTo(345));
        });

        var bypass = MoveSnapping.Solve(document, [moving.Id], moving.Bounds,
            241, 0, 2, 2);
        Assert.Multiple(() =>
        {
            Assert.That(bypass.Delta.X, Is.EqualTo(241));
            Assert.That(bypass.VerticalGuide, Is.Null);
        });
    }

    [Test]
    public void ResizeSnappingUsesManualGuidesAndSupportsBothAxes()
    {
        var moving = Shape(100, 100);
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving] };
        var guides = new ManualGuideSet();
        Assert.Multiple(() =>
        {
            Assert.That(guides.Add(true, 300, 1000, 600), Is.EqualTo(ManualGuideAddResult.Added));
            Assert.That(guides.Add(false, 250, 1000, 600), Is.EqualTo(ManualGuideAddResult.Added));
        });
        var snap = ResizeSnapping.Solve(document, [moving.Id], moving.Bounds,
            97, 47, 4, 4, uniform: false, ResizeHandle.BottomRight, guides);
        Assert.Multiple(() =>
        {
            Assert.That(snap.Delta.X, Is.EqualTo(100));
            Assert.That(snap.Delta.Y, Is.EqualTo(50));
            Assert.That(snap.VerticalGuide, Is.EqualTo(300));
            Assert.That(snap.HorizontalGuide, Is.EqualTo(250));
        });
    }

    [Test]
    public void GuidesRemainOutsideDocumentHistoryPersistenceAndPng()
    {
        AvaloniaTestHost.EnsureStarted();
        var moving = Shape(100, 100);
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving] });
        var canvas = new SceneCanvas(edit);
        var before = PngExporter.Render(edit.Document);
        Assert.That(canvas.ManualGuides.Add(true, 240, 1000, 600),
            Is.EqualTo(ManualGuideAddResult.Added));
        Assert.Multiple(() =>
        {
            Assert.That(edit.IsDirty, Is.False, "guide state is not a document edit");
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(before),
                "manual guides are preview-only and never reach PNG");
        });

        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3-guides-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "r3-test");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.StateEquals(edit.Document), Is.True);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void ReplacingTheDocumentClearsGuidesButUndoKeepsThem()
    {
        var moving = Shape(100, 100);
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [moving] });
        var canvas = new SceneCanvas(edit);
        Assert.That(canvas.ManualGuides.Add(true, 240, 1000, 600),
            Is.EqualTo(ManualGuideAddResult.Added));

        edit.BeginGesture(moving.Id);
        edit.MoveBy(60, 20);
        edit.CommitGesture();
        Assert.That(edit.Undo(), Is.True);
        canvas.Refresh();
        Assert.That(canvas.ManualGuides.Count, Is.EqualTo(1),
            "undo replaces the document snapshot but does not replace the editing context");

        edit.ReplaceDocument(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 });
        Assert.That(canvas.ManualGuides.Count, Is.Zero,
            "new/open/import resets session-only guides");
    }

    [Test]
    public void HeadlessCanvasMovesACombinedGuideAndAltBypassesItsSnap()
    {
        AvaloniaTestHost.EnsureStarted();
        var mover = Shape(100, 100);
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [mover] });
        var canvas = new SceneCanvas(edit);
        canvas.ManualGuides.Add(true, 300, 1000, 600);
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

            window.MouseDown(View(150, 150), MouseButton.Left);
            window.MouseMove(View(345, 150), RawInputModifiers.LeftMouseButton);
            Assert.That(edit.Document.Find(mover.Id).Bounds.X,
                Is.EqualTo(300).Within(1e-6));
            window.MouseMove(View(345, 150),
                RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            Assert.That(edit.Document.Find(mover.Id).Bounds.X,
                Is.EqualTo(295).Within(1e-6));
            window.MouseUp(View(345, 150), MouseButton.Left);
            Assert.Multiple(() =>
            {
                Assert.That(edit.UndoCount, Is.EqualTo(1));
                Assert.That(canvas.ManualGuides.Count, Is.EqualTo(1));
            });
        }
        finally { window.Close(); }
    }

    [Test]
    public void HeadlessGuideCanBeDraggedAndDraggedBackToDelete()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 });
        var canvas = new SceneCanvas(edit);
        canvas.ManualGuides.Add(true, 300, 1000, 600);
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

            window.MouseDown(View(300, 250), MouseButton.Left);
            window.MouseMove(View(500, 250), RawInputModifiers.LeftMouseButton);
            window.MouseUp(View(500, 250), MouseButton.Left);
            Assert.That(canvas.ManualGuides.Items.Single().Position,
                Is.EqualTo(500).Within(1e-6));
            Assert.That(edit.UndoCount, Is.Zero);

            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.That(canvas.ManualGuides.Count, Is.Zero,
                "a hovered guide can be removed without touching document history");
        }
        finally { window.Close(); }
    }

    [Test]
    public void HeadlessMainWindowRulerCreatesGuideAndToggleHidesRulers()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 });
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var ruler = window.GetVisualDescendants().OfType<CanvasRuler>()
                .Single(item => item.Orientation == CanvasRulerOrientation.Horizontal);
            var start = ruler.TranslatePoint(new Point(ruler.Bounds.Width / 2, 10), window)!.Value;
            var drop = canvas.TranslatePoint(
                new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(drop, RawInputModifiers.LeftMouseButton);
            window.MouseUp(drop, MouseButton.Left);
            Assert.Multiple(() =>
            {
                Assert.That(canvas.ManualGuides.Count, Is.EqualTo(1));
                Assert.That(canvas.ManualGuides.Items[0].Vertical, Is.False);
                Assert.That(canvas.ManualGuides.Items[0].Position,
                    Is.EqualTo(300).Within(1e-6));
                Assert.That(edit.UndoCount, Is.Zero);
                Assert.That(edit.IsDirty, Is.False);
                Assert.That(ruler.IsVisible, Is.True);
            });

            var view = canvas.CurrentViewTransform;
            var guideView = view.ToView(new PointD(500, 300));
            var guideAt = canvas.TranslatePoint(new Point(guideView.X, guideView.Y), window)!.Value;
            var backToRuler = ruler.TranslatePoint(new Point(ruler.Bounds.Width / 2, 10), window)!.Value;
            window.MouseDown(guideAt, MouseButton.Left);
            window.MouseMove(backToRuler, RawInputModifiers.LeftMouseButton);
            window.MouseUp(backToRuler, MouseButton.Left);
            Assert.That(canvas.ManualGuides.Count, Is.Zero,
                "dragging a guide back to the ruler removes it");

            var show = window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(check => check.Name == "ShowRulers");
            show.IsChecked = false;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.That(ruler.IsVisible, Is.False);
            show.IsChecked = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.That(ruler.IsVisible, Is.True);
        }
        finally { window.Close(); }
    }

    [Test]
    public void HeadlessGuideFieldsAddAndClearExactCoordinates()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 });
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var x = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "GuideX");
            var y = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "GuideY");
            x.Text = "512.5";
            UiTestActions.Click(UiTestActions.FindNamed(window, "AddVerticalGuide"));
            y.Text = "240";
            UiTestActions.Click(UiTestActions.FindNamed(window, "AddHorizontalGuide"));
            Assert.Multiple(() =>
            {
                Assert.That(canvas.ManualGuides.Positions(true), Is.EqualTo(new[] { 512.5 }));
                Assert.That(canvas.ManualGuides.Positions(false), Is.EqualTo(new[] { 240.0 }));
                Assert.That(edit.UndoCount, Is.Zero);
                Assert.That(edit.IsDirty, Is.False);
            });
            UiTestActions.Click(UiTestActions.FindNamed(window, "ClearManualGuides"));
            Assert.That(canvas.ManualGuides.Count, Is.Zero);
        }
        finally { window.Close(); }
    }
}
