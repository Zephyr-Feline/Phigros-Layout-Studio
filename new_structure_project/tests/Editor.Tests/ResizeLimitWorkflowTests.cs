using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class ResizeLimitWorkflowTests
{
    [TestCase(true, false, 0)]
    [TestCase(false, false, 0)]
    [TestCase(true, true, 37)]
    [TestCase(false, true, -29)]
    public void DecimalWidthOrHeightCanReachItsLegalMinimumWithoutMovingTheOppositeAnchor(
        bool width, bool keepAspect, double angle)
    {
        var layer = new Layer { Kind = LayerKind.Shape, KeepAspect = keepAspect, RotationDegrees = angle,
            Bounds = new(100, 100, width ? 1.9 : 100, width ? 100 : 1.9) };
        var edit = Session([layer]); edit.SetSelection([layer.Id]); var before = edit.Document.Copy();
        var anchor = OppositeAnchor(layer, width);

        edit.BeginGesture(layer.Id);
        ResizeAxis(edit, width, -1000, angle);

        Assert.DoesNotThrow(edit.Document.Validate, "A clamped preview must already satisfy strict model bounds.");
        var resized = edit.Document.Find(layer.Id);
        Assert.That(width ? resized.Bounds.Width : resized.Bounds.Height,
            Is.GreaterThanOrEqualTo(1).And.EqualTo(1).Within(1e-12));
        if (keepAspect) Assert.That(resized.Bounds.Width / resized.Bounds.Height,
            Is.EqualTo(layer.Bounds.Width / layer.Bounds.Height).Within(1e-12));
        else Assert.That(width ? resized.Bounds.Height : resized.Bounds.Width, Is.EqualTo(100));
        AssertPoint(OppositeAnchor(resized, width), anchor);
        Assert.That(edit.CommitGesture(), Is.True); Assert.That(edit.UndoCount, Is.EqualTo(1));
        AssertHistoryAndNativeRoundTrip(edit, before);
    }

    [TestCase(0)]
    [TestCase(37)]
    public void AWidthFortyNineBadgeReachesEightWithItsRatioAndRotatedAnchorIntact(double angle)
    {
        var badge = EditorDocument.NewBadge(new(200, 150), 49); badge.RotationDegrees = angle;
        var edit = Session([badge]); edit.SetSelection([badge.Id]); var before = edit.Document.Copy();
        var anchor = OppositeAnchor(badge, width: true);

        edit.BeginGesture(badge.Id); ResizeAxis(edit, width: true, -1000, angle);

        Assert.DoesNotThrow(edit.Document.Validate);
        var resized = edit.Document.Find(badge.Id);
        Assert.That(resized.Bounds.Width, Is.GreaterThanOrEqualTo(8).And.EqualTo(8).Within(1e-12));
        Assert.That(resized.Bounds.Height, Is.EqualTo(6.75).Within(1e-12));
        Assert.That(resized.Bounds.Width / resized.Bounds.Height, Is.EqualTo(EditorDocument.BadgeRatio).Within(1e-12));
        Assert.That(resized.KeepAspect, Is.True); AssertPoint(OppositeAnchor(resized, width: true), anchor);
        Assert.That(edit.CommitGesture(), Is.True); Assert.That(edit.UndoCount, Is.EqualTo(1));
        AssertHistoryAndNativeRoundTrip(edit, before);
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    [TestCase(false, true)]
    public void ASizeOfOneHundredFortyFiveReachesTheUpperLimitWithoutRoundingOutsideIt(bool width, bool keepAspect)
    {
        var layer = new Layer { Kind = LayerKind.Shape, KeepAspect = keepAspect,
            Bounds = new(100, 100, width ? 145 : 100, width ? 100 : 145) };
        var edit = Session([layer]); edit.SetSelection([layer.Id]); var before = edit.Document.Copy();
        var anchor = OppositeAnchor(layer, width);

        edit.BeginGesture(layer.Id); ResizeAxis(edit, width, 1e6);

        Assert.DoesNotThrow(edit.Document.Validate, "The upper-size clamp cannot leave 20000.000000000004.");
        var resized = edit.Document.Find(layer.Id);
        Assert.That(width ? resized.Bounds.Width : resized.Bounds.Height,
            Is.LessThanOrEqualTo(20000).And.EqualTo(20000).Within(1e-9));
        if (keepAspect) Assert.That(resized.Bounds.Width / resized.Bounds.Height,
            Is.EqualTo(layer.Bounds.Width / layer.Bounds.Height).Within(1e-12));
        else Assert.That(width ? resized.Bounds.Height : resized.Bounds.Width, Is.EqualTo(100));
        AssertPoint(OppositeAnchor(resized, width), anchor);
        Assert.That(edit.CommitGesture(), Is.True); Assert.That(edit.UndoCount, Is.EqualTo(1));
        AssertHistoryAndNativeRoundTrip(edit, before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GroupLimitsUseOneScaleForHiddenMembersWhileLockedGeometryStaysUntouched(bool upper)
    {
        var group = Guid.NewGuid();
        var first = new Layer { Kind = LayerKind.Shape, GroupId = group,
            Bounds = new(100, 100, upper ? 145 : 1.9, 100) };
        var hidden = new Layer { Kind = LayerKind.Text, GroupId = group, Visible = false, Text = "隐藏曲名", FontSize = 40,
            RotationDegrees = upper ? 0 : 17,
            Bounds = upper ? new(136.25, 125, 72.5, 50) : new(150, 130, 3.8, 60) };
        var inside = new Layer { Kind = LayerKind.Shape, GroupId = group, Bounds = new(130, 120, 29, 20) };
        var locked = new Layer { Kind = LayerKind.Shape, GroupId = group, Locked = true, Bounds = new(400, 100, 1.1, 1.1) };
        var layers = upper ? new[] { first, hidden, inside, locked } : [first, hidden, locked];
        var edit = Session(layers); edit.SetSelection([hidden.Id, first.Id]); var before = edit.Document.Copy();

        edit.BeginGesture(first.Id, includeGroup: true);
        Assert.That(edit.ActiveGestureIds, Does.Contain(hidden.Id).And.Not.Contain(locked.Id));
        var anchor = LayerGeometry.HandlePoint(edit.ActiveGestureBounds, ResizeHandle.Left);
        ResizeAxis(edit, width: true, upper ? 1e6 : -1000);

        Assert.DoesNotThrow(edit.Document.Validate);
        var resized = edit.Document.Find(first.Id);
        Assert.That(resized.Bounds.Width, upper
            ? Is.LessThanOrEqualTo(20000).And.EqualTo(20000).Within(1e-9)
            : Is.GreaterThanOrEqualTo(1).And.EqualTo(1).Within(1e-12));
        var scale = resized.Bounds.Width / first.Bounds.Width;
        var firstCentre = LayerGeometry.Centre(first.Bounds);
        var resizedCentre = LayerGeometry.Centre(resized.Bounds);
        foreach (var original in layers.Where(layer => !layer.Locked))
        {
            var current = edit.Document.Find(original.Id);
            var originalCentre = LayerGeometry.Centre(original.Bounds);
            var currentCentre = LayerGeometry.Centre(current.Bounds);
            Assert.That(current.Bounds.Width / original.Bounds.Width, Is.EqualTo(scale).Within(1e-12));
            Assert.That(current.Bounds.Height / original.Bounds.Height, Is.EqualTo(scale).Within(1e-12));
            Assert.That(currentCentre.X - resizedCentre.X,
                Is.EqualTo((originalCentre.X - firstCentre.X) * scale).Within(1e-8));
            Assert.That(currentCentre.Y - resizedCentre.Y,
                Is.EqualTo((originalCentre.Y - firstCentre.Y) * scale).Within(1e-8));
            Assert.That(current.RotationDegrees, Is.EqualTo(original.RotationDegrees));
            Assert.That(current.Visible, Is.EqualTo(original.Visible));
        }
        Assert.That(edit.Document.Find(hidden.Id).FontSize, Is.EqualTo(Math.Clamp(hidden.FontSize * scale, 1, 1000)).Within(1e-9));
        Assert.That(edit.Document.Find(locked.Id).StateEquals(locked), Is.True);
        // A uniform resize keeps the union's opposite anchor; hidden members participate in that union.
        var targets = edit.ActiveGestureIds.Select(id => LayerGeometry.VisualBounds(edit.Document.Find(id)));
        AssertPoint(LayerGeometry.HandlePoint(MoveSnapping.BoundsOf(targets), ResizeHandle.Left), anchor);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { hidden.Id, first.Id }));
        Assert.That(edit.CommitGesture(), Is.True); Assert.That(edit.UndoCount, Is.EqualTo(1));
        AssertHistoryAndNativeRoundTrip(edit, before);
    }

    [Test]
    public void CancellingALimitPreviewPreservesRedoAndAllowsTheSameBoundaryToBeCommittedOnRetry()
    {
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new(100, 100, 1.9, 100) };
        var edit = Session([layer]); edit.SetSelection([layer.Id]);
        edit.UpdateLayer(layer.Id, current => current.Name = "redo checkpoint"); edit.Undo();
        var before = edit.Document.Copy(); var png = TestImages.RenderPng(before, true, 320);

        edit.BeginGesture(layer.Id); ResizeAxis(edit, width: true, -1000);
        Assert.DoesNotThrow(edit.Document.Validate);
        edit.CancelGesture();

        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { layer.Id }));
        Assert.That(edit.UndoCount, Is.Zero); Assert.That(edit.RedoCount, Is.EqualTo(1)); Assert.That(edit.IsDirty, Is.False);
        Assert.That(TestImages.RenderPng(edit.Document, true, 320), Is.EqualTo(png));
        edit.BeginGesture(layer.Id); ResizeAxis(edit, width: true, -1000);
        Assert.That(edit.CommitGesture(), Is.True);
        Assert.That(edit.UndoCount, Is.EqualTo(1)); Assert.That(edit.RedoCount, Is.Zero); Assert.That(edit.IsDirty, Is.True);
        AssertHistoryAndNativeRoundTrip(edit, before);
    }

    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    public void ActualCanvasInputCommitsTheUpperWidthAndKeepsPropertiesReopenAndPngConsistent(int width, int height)
    {
        AvaloniaTestHost.EnsureStarted(); var theme = new FluentTheme(); Avalonia.Application.Current!.Styles.Add(theme);
        var layer = new Layer { Kind = LayerKind.Shape, Name = "边界缩放", Bounds = new(100, 100, 145, 100) };
        var edit = new EditSession(new EditorDocument { CanvasWidth = width, CanvasHeight = height, Layers = [layer] });
        edit.SetSelection([layer.Id]); var before = edit.Document.Copy();
        var window = new MainWindow(edit);
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var start = LayerGeometry.HandlePoint(layer.Bounds, ResizeHandle.Right);
            var from = View(start); var to = View(new(start.X + 1e6, start.Y));
            window.MouseMove(from); window.MouseDown(from, MouseButton.Left);
            Assert.That(canvas.IsInteracting, Is.True);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            window.MouseUp(to, MouseButton.Left);

            Assert.That(UiTestActions.Error(window), Is.Null);
            Assert.That(canvas.IsInteracting, Is.False); Assert.That(edit.ActiveGestureIds, Is.Empty);
            Assert.That(edit.Document.Find(layer.Id).Bounds.Width,
                Is.LessThanOrEqualTo(20000).And.EqualTo(20000).Within(1e-9));
            Assert.That(edit.Document.Find(layer.Id).Bounds.Height, Is.EqualTo(100));
            Assert.That(edit.UndoCount, Is.EqualTo(1)); Assert.That(edit.SelectedLayerId, Is.EqualTo(layer.Id));
            Assert.That(window.GetLogicalDescendants().OfType<TextBox>().Single(field => field.Name == "LayerWidth").Text,
                Is.EqualTo("20000"));
            Assert.That(window.Title, Does.Contain("*"));
            AssertHistoryAndNativeRoundTrip(edit, before);

            Point View(PointD logical)
            {
                var transform = new ViewportTransform(width, height, canvas.Bounds.Width, canvas.Bounds.Height);
                var point = transform.ToView(logical);
                return canvas.TranslatePoint(new(point.X, point.Y), window)!.Value;
            }
        }
        finally
        {
            edit.CancelGesture();
            window.GetVisualDescendants().OfType<SceneCanvas>().Single().Refresh();
            edit.MarkSaved(); window.Close(); Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static EditSession Session(Layer[] layers) => new(new EditorDocument
        { CanvasWidth = 640, CanvasHeight = 360, Layers = layers.ToList() });

    private static void ResizeAxis(EditSession edit, bool width, double delta, double angle = 0)
    {
        var change = LayerGeometry.Rotate(width ? new(delta, 0) : new(0, delta), default, angle);
        edit.ResizeFromHandle(change.X, change.Y, width ? ResizeHandle.Right : ResizeHandle.Bottom);
    }

    private static PointD OppositeAnchor(Layer layer, bool width) =>
        LayerGeometry.HandlePoint(layer.Bounds, width ? ResizeHandle.Left : ResizeHandle.Top, layer.RotationDegrees);

    private static void AssertPoint(PointD actual, PointD expected)
    {
        Assert.That(actual.X, Is.EqualTo(expected.X).Within(1e-8));
        Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(1e-8));
    }

    private static void AssertHistoryAndNativeRoundTrip(EditSession edit, EditorDocument before)
    {
        var after = edit.Document.Copy(); var selection = edit.SelectedLayerIds.ToArray();
        Assert.That(edit.Undo(), Is.True); Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.Redo(), Is.True); Assert.That(edit.Document.StateEquals(after), Is.True);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection));
        var history = (edit.UndoCount, edit.RedoCount, edit.IsDirty);
        var outputs = new[] { false, true }.Select(mode => TestImages.RenderPng(after, mode, 320)).ToArray();
        var path = Path.Combine(Path.GetTempPath(), "resize-limit-" + Guid.NewGuid() + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "resize-limit-workflow-tests");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.StateEquals(after), Is.True);
            for (var mode = 0; mode < outputs.Length; mode++)
                Assert.That(TestImages.RenderPng(reopened, mode == 1, 320), Is.EqualTo(outputs[mode]));
        }
        finally { File.Delete(path); }
        Assert.That(edit.Document.StateEquals(after), Is.True);
        Assert.That((edit.UndoCount, edit.RedoCount, edit.IsDirty), Is.EqualTo(history));
    }
}
