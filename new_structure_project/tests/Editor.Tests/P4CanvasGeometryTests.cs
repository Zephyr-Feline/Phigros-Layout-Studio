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
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4CanvasGeometryTests
{
    [TestCase(1200, 600, 1.0, 200.0, 0.0)]
    [TestCase(600, 800, 0.75, 0.0, 175.0)]
    [TestCase(1600, 1200, 2.0, 0.0, 0.0)]
    public void CanvasFitMapsAllLayersThroughOneCentredLogicalScale(
        int width, int height, double scale, double offsetX, double offsetY)
    {
        var group = Guid.NewGuid();
        var badge = EditorDocument.NewBadge(new PointD(100, 120));
        badge.GroupId = group;
        var text = new Layer { Kind = LayerKind.Text, GroupId = group, Locked = true,
            Visible = false, Bounds = new RectD(300, 250, 200, 80), FontSize = 40 };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 800, CanvasHeight = 600, Layers = [badge, text]
        });
        edit.ResizeCanvasToFit(width, height);
        var fittedBadge = edit.Document.Find(badge.Id);
        var fittedText = edit.Document.Find(text.Id);
        Assert.Multiple(() =>
        {
            Assert.That((edit.Document.CanvasWidth, edit.Document.CanvasHeight), Is.EqualTo((width, height)));
            Assert.That(fittedBadge.Bounds.X, Is.EqualTo(offsetX + 100 * scale).Within(1e-9));
            Assert.That(fittedBadge.Bounds.Y, Is.EqualTo(offsetY + 120 * scale).Within(1e-9));
            Assert.That(fittedBadge.Bounds.Width / fittedBadge.Bounds.Height,
                Is.EqualTo(EditorDocument.BadgeRatio).Within(1e-9));
            Assert.That(fittedText.Bounds,
                Is.EqualTo(new RectD(offsetX + 300 * scale, offsetY + 250 * scale,
                    200 * scale, 80 * scale)));
            Assert.That(fittedText.FontSize, Is.EqualTo(40 * scale));
            Assert.That(fittedText.Visible, Is.False);
            Assert.That(fittedText.Locked, Is.True);
            Assert.That(fittedBadge.GroupId, Is.EqualTo(group));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
        edit.Document.Validate();
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Find(badge.Id).Bounds, Is.EqualTo(badge.Bounds));
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(text.Id).FontSize, Is.EqualTo(40 * scale));
    }

    [Test]
    public void CanvasFitClampsFontAndRejectsUnrepresentableGeometryWithoutPartialMutation()
    {
        var text = new Layer { Kind = LayerKind.Text, FontSize = 900,
            Bounds = new RectD(100, 100, 100, 50) };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 800, CanvasHeight = 600, Layers = [text]
        });
        edit.ResizeCanvasToFit(1600, 1200);
        Assert.That(edit.Document.Find(text.Id).FontSize, Is.EqualTo(1000));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Find(text.Id).FontSize, Is.EqualTo(900));

        var edge = new Layer { Kind = LayerKind.Shape,
            Bounds = new RectD(19900, 100, 100, 100) };
        var limited = new EditSession(new EditorDocument
        {
            CanvasWidth = 800, CanvasHeight = 600, Layers = [edge]
        });
        var before = limited.Document.Copy();
        Assert.Throws<InvalidDataException>(() => limited.ResizeCanvasToFit(1600, 1200));
        Assert.That(limited.Document.StateEquals(before), Is.True);
        Assert.That(limited.UndoCount, Is.Zero);
        Assert.Throws<InvalidDataException>(() => limited.ResizeCanvasToFit(100, 100));
        Assert.That(limited.Document.StateEquals(before), Is.True);
    }

    [Test]
    public void CanvasFitSurvivesNativeRoundTripAndKeepsTheRenderedCanvasSize()
    {
        AvaloniaTestHost.EnsureStarted();
        var badge = EditorDocument.NewBadge(new PointD(100, 100));
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 800, CanvasHeight = 600, Layers = [badge]
        });
        edit.ResizeCanvasToFit(600, 800);
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p4-canvas-fit-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "p4-test");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.Find(badge.Id).Bounds, Is.EqualTo(edit.Document.Find(badge.Id).Bounds));
            var (width, height, _) = TestImages.DecodePng(TestImages.RenderPng(reopened));
            Assert.That((width, height), Is.EqualTo((600, 800)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void NudgeUsesGroupTargetsSharedLimitsAndOneHistoryEntryPerKey()
    {
        var group = Guid.NewGuid();
        var moving = new Layer { Kind = LayerKind.Shape, GroupId = group,
            Bounds = new RectD(100, 100, 100, 100) };
        var edge = new Layer { Kind = LayerKind.Shape, GroupId = group,
            Bounds = new RectD(19999, 200, 100, 100) };
        var locked = new Layer { Kind = LayerKind.Shape, GroupId = group, Locked = true,
            Bounds = new RectD(300, 300, 100, 100) };
        var edit = new EditSession(new EditorDocument { Layers = [moving, edge, locked] });
        edit.SelectedLayerId = moving.Id;
        Assert.That(edit.NudgeSelection(10, 0), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Find(moving.Id).Bounds.X, Is.EqualTo(101));
            Assert.That(edit.Document.Find(edge.Id).Bounds.X, Is.EqualTo(20000));
            Assert.That(edit.Document.Find(locked.Id).Bounds.X, Is.EqualTo(300));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
        Assert.That(edit.NudgeSelection(1, 0), Is.False);
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Find(edge.Id).Bounds.X, Is.EqualTo(19999));
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(moving.Id).Bounds.X, Is.EqualTo(101));
        Assert.That(edit.NudgeSelection(-10, 0, includeGroup: false), Is.True);
        Assert.That(edit.Document.Find(moving.Id).Bounds.X, Is.EqualTo(91));
        Assert.That(edit.Document.Find(edge.Id).Bounds.X, Is.EqualTo(20000));
    }

    [Test]
    public void HeadlessCanvasKeysNudgeDeleteAndResizeButtonUsesActualUiEntry()
    {
        AvaloniaTestHost.EnsureStarted();
        var shape = new Layer { Kind = LayerKind.Shape,
            Bounds = new RectD(100, 100, 100, 100) };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 800, CanvasHeight = 600, Layers = [shape]
        });
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var view = new ViewportTransform(800, 600, canvas.Bounds.Width, canvas.Bounds.Height)
                .ToView(new PointD(150, 150));
            var point = new Point(view.X + canvas.Bounds.X, view.Y + canvas.Bounds.Y);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyPress(Key.Down, RawInputModifiers.Shift, PhysicalKey.None, null);
            Assert.That(edit.Document.Find(shape.Id).Bounds, Is.EqualTo(new RectD(101, 110, 100, 100)));
            Assert.That(edit.UndoCount, Is.EqualTo(2));

            var field = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "CanvasWidth");
            var height = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "CanvasHeight");
            field.Text = "1200";
            height.Text = "600";
            UiTestActions.Click(UiTestActions.FindNamed(window, "ResizeCanvasToFit"));
            Assert.That(edit.Document.Find(shape.Id).Bounds,
                Is.EqualTo(new RectD(301, 110, 100, 100)));
            Assert.That(edit.UndoCount, Is.EqualTo(3));

            UiTestActions.Focus(field);
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.That(edit.Document.Find(shape.Id).Bounds.X, Is.EqualTo(301),
                "an arrow in a text field must not move the canvas selection");
            canvas.Focus();
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.That(edit.Document.Layers, Is.Empty);
            Assert.That(edit.UndoCount, Is.EqualTo(4));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(shape.Id).Bounds.X, Is.EqualTo(301));
        }
        finally { window.Close(); }
    }
}
