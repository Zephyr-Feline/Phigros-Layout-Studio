using Avalonia.LogicalTree;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4ArrangementTests
{
    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    [TestCase(2740, 1080)]
    public void EveryAlignmentUsesLogicalReferenceAndPreservesAllNonpositionState(int width, int height)
    {
        foreach (var reference in Enum.GetValues<ArrangeReference>())
        foreach (var action in Enum.GetValues<ArrangeAction>().Take(6))
        {
            var edit = Session(width, height);
            edit.SetSelection(edit.Document.Layers.Select(l => l.Id).Reverse());
            var before = edit.Document.Copy();
            var target = reference == ArrangeReference.Canvas ? new RectD(0, 0, width, height)
                : MoveSnapping.BoundsOf(before.Layers.Select(l => l.Bounds));
            edit.ArrangeSelection(action, reference, false);
            foreach (var layer in edit.Document.Layers)
            {
                var b = layer.Bounds;
                double Anchor(RectD box) => action switch
                {
                    ArrangeAction.Left => box.X,
                    ArrangeAction.CenterHorizontal => box.X + box.Width / 2,
                    ArrangeAction.Right => box.X + box.Width,
                    ArrangeAction.Top => box.Y,
                    ArrangeAction.CenterVertical => box.Y + box.Height / 2,
                    _ => box.Y + box.Height
                };
                Assert.That(Anchor(b), Is.EqualTo(Anchor(target)).Within(1e-8), $"{reference}/{action}");
                var original = before.Find(layer.Id);
                Assert.That(action <= ArrangeAction.Right ? b.Y : b.X,
                    Is.EqualTo(action <= ArrangeAction.Right ? original.Bounds.Y : original.Bounds.X));
                var comparable = layer.Copy();
                comparable.Bounds = original.Bounds;
                Assert.That(comparable.StateEquals(original), Is.True);
            }
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            edit.ArrangeSelection(action, reference, false);
            Assert.That(edit.UndoCount, Is.EqualTo(1), "Repeated alignment is a no-op.");
            var after = edit.Document.Copy();
            edit.Undo();
            Assert.That(edit.Document.StateEquals(before), Is.True);
            edit.Redo();
            Assert.That(edit.Document.StateEquals(after), Is.True);
        }
    }

    [TestCase(ArrangeAction.HorizontalGaps, ArrangeReference.SelectionBounds, 3)]
    [TestCase(ArrangeAction.HorizontalGaps, ArrangeReference.Canvas, 5)]
    [TestCase(ArrangeAction.VerticalGaps, ArrangeReference.SelectionBounds, 5)]
    [TestCase(ArrangeAction.VerticalGaps, ArrangeReference.Canvas, 3)]
    public void UnequalSizeUnitsGetEqualGapsInSpatialOrder(ArrangeAction action, ArrangeReference reference, int count)
    {
        var horizontal = action == ArrangeAction.HorizontalGaps;
        var layers = Enumerable.Range(0, count).Select(i => new Layer { Kind = LayerKind.Shape,
            Bounds = new RectD(20 + i * 170, 30 + i * 150, 40 + i * 7, 35 + i * 9) }).ToList();
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1440, CanvasHeight = 1080, Layers = layers });
        edit.SetSelection(layers.Select(l => l.Id).Reverse());
        var before = edit.Document.Copy();
        var target = reference == ArrangeReference.Canvas ? new RectD(0, 0, 1440, 1080)
            : MoveSnapping.BoundsOf(layers.Select(l => l.Bounds));
        edit.ArrangeSelection(action, reference, false);
        double Start(RectD b) => horizontal ? b.X : b.Y;
        double Size(RectD b) => horizontal ? b.Width : b.Height;
        var boxes = edit.Document.Layers.Select(l => l.Bounds).ToArray();
        var gap = (Size(target) - boxes.Sum(Size)) / (count - 1);
        Assert.That(Start(boxes[0]), Is.EqualTo(Start(target)).Within(1e-8));
        Assert.That(Start(boxes[^1]) + Size(boxes[^1]), Is.EqualTo(Start(target) + Size(target)).Within(1e-8));
        for (var i = 1; i < count; i++)
            Assert.That(Start(boxes[i]) - Start(boxes[i - 1]) - Size(boxes[i - 1]), Is.EqualTo(gap).Within(1e-8));
        for (var i = 0; i < count; i++)
            Assert.That(horizontal ? boxes[i].Y : boxes[i].X,
                Is.EqualTo(horizontal ? before.Layers[i].Bounds.Y : before.Layers[i].Bounds.X));
        edit.ArrangeSelection(action, reference, false);
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
    }

    [Test]
    public void GroupExpansionDeduplicatesAndMovesHiddenBadgeWithRelativeGeometryIntact()
    {
        var edit = Session();
        var group = Guid.NewGuid();
        var art = edit.Document.Layers[0];
        art.GroupId = group;
        var badge = EditorDocument.NewBadge(new PointD(art.Bounds.X + 50, art.Bounds.Y + 30));
        badge.GroupId = group;
        badge.Visible = false;
        edit.Document.Layers.Add(badge);
        edit.SetSelection([badge.Id, art.Id, edit.Document.Layers[1].Id]);
        var before = edit.Document.Copy();
        edit.ArrangeSelection(ArrangeAction.Bottom, ArrangeReference.Canvas);
        var a = edit.Document.Find(art.Id).Bounds;
        var b = edit.Document.Find(badge.Id).Bounds;
        Assert.That(b.X - a.X, Is.EqualTo(50));
        Assert.That(b.Y - a.Y, Is.EqualTo(30));
        Assert.That(b.Width / b.Height, Is.EqualTo(EditorDocument.BadgeRatio));
        Assert.That(b.Y + b.Height, Is.EqualTo(edit.Document.CanvasHeight));
        Assert.That(edit.Document.Find(badge.Id).Visible, Is.False);
        Assert.That(edit.Document.Layers[2].StateEquals(before.Layers[2]), Is.True);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { badge.Id, art.Id, edit.Document.Layers[1].Id }));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
    }

    [Test]
    public void DisabledGroupModeMovesOnlySelectedMembers()
    {
        var edit = Session();
        var group = Guid.NewGuid();
        edit.Document.Layers[0].GroupId = group;
        edit.Document.Layers[1].GroupId = group;
        edit.Document.Layers[1].Locked = true;
        edit.SetSelection([edit.Document.Layers[0].Id]);
        var untouched = edit.Document.Layers[1].Copy();
        edit.ArrangeSelection(ArrangeAction.Right, ArrangeReference.Canvas, false);
        Assert.That(edit.Document.Layers[1].StateEquals(untouched), Is.True);
        Assert.That(edit.Document.Layers[0].Bounds.X + edit.Document.Layers[0].Bounds.Width, Is.EqualTo(1440));
    }

    [TestCase(ArrangeAction.Left)]
    [TestCase(ArrangeAction.HorizontalGaps)]
    public void LockedHiddenGroupMemberRejectsEntireTransactionAndPreservesRedo(ArrangeAction action)
    {
        var edit = Session();
        var group = Guid.NewGuid();
        var first = edit.Document.Layers[0];
        first.GroupId = group;
        edit.Document.Layers.Add(new Layer { Kind = LayerKind.Shape, GroupId = group, Locked = true, Visible = false });
        edit.SetSelection(edit.Document.Layers.Take(3).Select(l => l.Id));
        edit.UpdateLayer(first.Id, l => l.Name = "edit");
        edit.Undo();
        var before = edit.Document.Copy();
        Assert.Throws<InvalidOperationException>(() => edit.ArrangeSelection(action, ArrangeReference.Canvas));
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.IsDirty, Is.False);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(edit.RedoCount, Is.EqualTo(1));
    }

    [Test]
    public void TooLittleSpaceAndTooFewUnitsFailWithoutMutation()
    {
        var edit = Session();
        var ids = edit.Document.Layers.Select(l => l.Id).ToArray();
        foreach (var layer in edit.Document.Layers) layer.Bounds = new RectD(0, 0, 600, 600);
        var before = edit.Document.Copy();
        edit.SetSelection(ids);
        foreach (var reference in Enum.GetValues<ArrangeReference>())
            Assert.Throws<InvalidOperationException>(() => edit.ArrangeSelection(ArrangeAction.HorizontalGaps, reference));
        edit.SetSelection(ids.Take(2));
        Assert.Throws<InvalidOperationException>(() => edit.ArrangeSelection(ArrangeAction.HorizontalGaps, ArrangeReference.Canvas));
        edit.SetSelection(ids.Take(1));
        Assert.Throws<InvalidOperationException>(() => edit.ArrangeSelection(ArrangeAction.Left, ArrangeReference.SelectionBounds));
        edit.SetSelection([]);
        Assert.Throws<InvalidOperationException>(() => edit.ArrangeSelection(ArrangeAction.Left, ArrangeReference.Canvas));
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void BoundsFailureAndActiveGestureCannotLeavePartialArrangement()
    {
        var edit = Session();
        edit.Document.Layers[0].Bounds = new RectD(20000, 0, 20000, 100);
        edit.SetSelection(edit.Document.Layers.Select(l => l.Id));
        var before = edit.Document.Copy();
        Assert.Throws<InvalidDataException>(() => edit.ArrangeSelection(ArrangeAction.Right, ArrangeReference.SelectionBounds));
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
        edit.BeginGesture(edit.SelectedLayerId!.Value);
        Assert.Throws<InvalidOperationException>(() => edit.ArrangeSelection(ArrangeAction.Top, ArrangeReference.Canvas));
        edit.CancelGesture();
        Assert.That(edit.Document.StateEquals(before), Is.True);
    }

    [Test]
    public void GeometricTiesUseDocumentOrderNotSelectionOrderAndZeroGapIsAllowed()
    {
        var edit = Session(600, 600);
        foreach (var layer in edit.Document.Layers) layer.Bounds = new RectD(0, 0, 200, 100);
        edit.SetSelection(edit.Document.Layers.Select(l => l.Id).Reverse());
        edit.ArrangeSelection(ArrangeAction.HorizontalGaps, ArrangeReference.Canvas);
        Assert.That(edit.Document.Layers.Select(l => l.Bounds.X), Is.EqualTo(new double[] { 0, 200, 400 }));
    }

    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    public void ArrangedLayersRemainHittableAndDraggable(int width, int height)
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = Session(width, height);
        edit.SetSelection(edit.Document.Layers.Select(l => l.Id));
        edit.ArrangeSelection(ArrangeAction.HorizontalGaps, ArrangeReference.Canvas);
        edit.SetSelection([]);
        var layer = edit.Document.Layers[0];
        var original = layer.Bounds;
        var canvas = new SceneCanvas(edit);
        var window = new Window { Width = 800, Height = 600, Content = canvas };
        try
        {
            window.Show();
            var transform = new ViewportTransform(width, height, canvas.Bounds.Width, canvas.Bounds.Height);
            Point View(double x, double y)
            {
                var p = transform.ToView(new PointD(x, y));
                return new Point(p.X, p.Y);
            }
            window.MouseDown(View(original.X + 40, original.Y + 30), MouseButton.Left);
            window.MouseMove(View(original.X + 70, original.Y + 50), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            window.MouseUp(View(original.X + 70, original.Y + 50), MouseButton.Left);
            Assert.That(edit.SelectedLayerId, Is.EqualTo(layer.Id));
            Assert.That(edit.Document.Find(layer.Id).Bounds.X, Is.EqualTo(original.X + 30).Within(1e-8));
            Assert.That(edit.Document.Find(layer.Id).Bounds.Y, Is.EqualTo(original.Y + 20).Within(1e-8));
            Assert.That(edit.UndoCount, Is.EqualTo(2));
            edit.Undo();
            Assert.That(edit.Document.Find(layer.Id).Bounds, Is.EqualTo(original));
        }
        finally { window.Close(); }
    }

    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    public void DesktopApplyUndoExportAndNativeReopenUseSameArrangement(int width, int height)
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var edit = Session(width, height);
        edit.SetSelection(edit.Document.Layers.Select(l => l.Id));
        var before = TestImages.RenderPng(edit.Document);
        var window = new MainWindow(edit);
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".ppgproj");
        try
        {
            window.Show();
            var controls = window.GetLogicalDescendants().ToArray();
            controls.OfType<ComboBox>().Single(c => c.Name == "ArrangeAction").SelectedIndex = (int)ArrangeAction.HorizontalGaps;
            controls.OfType<ComboBox>().Single(c => c.Name == "ArrangeReference").SelectedIndex = (int)ArrangeReference.Canvas;
            UiTestActions.ClickNamed(window, "ApplyArrangement");
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.Document.Layers[0].Bounds.X, Is.Zero);
            if (width == 1440)
            {
                var button = controls.OfType<Button>().Single(b => b.Name == "ApplyArrangement");
                UiTestActions.Reveal(button);
                window.UpdateLayout();
                var at = button.TranslatePoint(default, window)!.Value;
                Assert.That(at.X, Is.GreaterThanOrEqualTo(0));
                Assert.That(at.X + button.Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width));
                using var frame = new RenderTargetBitmap(new PixelSize(1200, 760));
                frame.Render(window);
                frame.Save(Path.Combine(TestContext.CurrentContext.WorkDirectory, "arrangement-editor-preview.png"),
                    new PngBitmapEncoderOptions());
            }
            var after = edit.Document.Copy();
            var png = TestImages.RenderPng(after);
            var transparent = TestImages.RenderPng(after, true);
            Assert.That(png, Is.Not.EqualTo(before));
            NativePackageStore.Save(path, after, "arrange-test");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.StateEquals(after), Is.True);
            Assert.That(TestImages.RenderPng(reopened), Is.EqualTo(png));
            Assert.That(TestImages.RenderPng(reopened, true), Is.EqualTo(transparent));
            edit.Undo();
            Assert.That(TestImages.RenderPng(edit.Document), Is.EqualTo(before));
            edit.Redo();
            Assert.That(TestImages.RenderPng(edit.Document), Is.EqualTo(png));
            // The existing template path still consumes the same three units after arrangement.
            edit.ApplyLayoutTemplate("three-columns", edit.SelectedLayerIds);
            Assert.That(edit.UndoCount, Is.EqualTo(2));
        }
        finally
        {
            edit.MarkSaved(); window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static EditSession Session(int width = 1440, int height = 1080) => new(new EditorDocument
    {
        CanvasWidth = width, CanvasHeight = height,
        Layers = [
            new Layer { Kind = LayerKind.Shape, Name = "A", Bounds = new(100, 100, 120, 80), ColorArgb = 0xFFCF1313 },
            new Layer { Kind = LayerKind.Text, Name = "B", Bounds = new(350, 200, 160, 60), Text = "Title", FontSize = 20 },
            new Layer { Kind = LayerKind.Shape, Name = "C", Bounds = new(700, 300, 100, 100), Slanted = true }
        ]
    });
}
