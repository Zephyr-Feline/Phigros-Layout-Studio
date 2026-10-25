using Avalonia.LogicalTree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4AspectPropertyTests
{
    [Test]
    public void ShapeCanLockRatioEditEitherDimensionAndReturnToFreeSizing()
    {
        AvaloniaTestHost.EnsureStarted();
        var shape = new Layer { Kind = LayerKind.Shape,
            Bounds = new RectD(100, 100, 200, 100), ColorArgb = 0xFFFFFFFF };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1440, CanvasHeight = 1080,
            Layers = [shape] });
        edit.SelectedLayerId = shape.Id;
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            TextBox Field(string name) => window.GetLogicalDescendants().OfType<TextBox>()
                .Single(field => field.Name == name);
            var aspect = window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(control => control.Name == "LayerKeepAspect");
            var apply = UiTestActions.FindNamed(window, "ApplyLayerProperties");
            Assert.Multiple(() =>
            {
                Assert.That(aspect.IsChecked, Is.False);
                Assert.That(aspect.IsEnabled, Is.True);
                Assert.That(Field("LayerHeight").IsEnabled, Is.True);
            });

            aspect.IsChecked = true;
            UiTestActions.Click(apply);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(shape.Id).KeepAspect, Is.True);
                Assert.That(edit.Document.Find(shape.Id).Bounds, Is.EqualTo(shape.Bounds));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
                Assert.That(Field("LayerHeight").IsEnabled, Is.True);
            });
            Field("LayerWidth").Text = "300";
            UiTestActions.Click(apply);
            Assert.That(edit.Document.Find(shape.Id).Bounds, Is.EqualTo(new RectD(100, 100, 300, 150)));
            Field("LayerHeight").Text = "200";
            UiTestActions.Click(apply);
            Assert.That(edit.Document.Find(shape.Id).Bounds, Is.EqualTo(new RectD(100, 100, 400, 200)));

            edit.BeginGesture(shape.Id);
            edit.ResizeBy(80, 0);
            edit.CommitGesture();
            Assert.That(edit.Document.Find(shape.Id).Bounds, Is.EqualTo(new RectD(100, 100, 480, 240)));
            Assert.That(edit.UndoCount, Is.EqualTo(4));
            var undo = UiTestActions.Find(window, "Undo");
            UiTestActions.Click(undo);
            Assert.That(edit.Document.Find(shape.Id).Bounds, Is.EqualTo(new RectD(100, 100, 400, 200)));

            Assert.That(aspect.IsChecked, Is.True);
            aspect.IsChecked = false;
            Field("LayerWidth").Text = "500";
            Field("LayerHeight").Text = "260";
            UiTestActions.Click(apply);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(shape.Id).KeepAspect, Is.False);
                Assert.That(edit.Document.Find(shape.Id).Bounds, Is.EqualTo(new RectD(100, 100, 500, 260)));
                Assert.That(edit.UndoCount, Is.EqualTo(4), "undo then a new edit clears redo and records one entry");
                Assert.That(edit.RedoCount, Is.Zero);
            });
        }
        finally { window.Close(); }
    }

    [Test]
    public void LastEditedDimensionDrivesLockedRatioAndNativeRoundTripPreservesIt()
    {
        AvaloniaTestHost.EnsureStarted();
        var image = new Layer { Kind = LayerKind.Shape, KeepAspect = true,
            Bounds = new RectD(50, 60, 240, 120), ColorArgb = 0xFFCF1313 };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1920, CanvasHeight = 1080,
            Layers = [image] });
        edit.SelectedLayerId = image.Id;
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            TextBox Field(string name) => window.GetLogicalDescendants().OfType<TextBox>()
                .Single(field => field.Name == name);
            var apply = UiTestActions.FindNamed(window, "ApplyLayerProperties");
            Field("LayerWidth").Text = "300";
            Field("LayerHeight").Text = "180";
            UiTestActions.Click(apply);
            Assert.That(edit.Document.Find(image.Id).Bounds, Is.EqualTo(new RectD(50, 60, 360, 180)),
                "height was edited last and drives the original 2:1 ratio");
            Field("LayerHeight").Text = "240";
            Field("LayerWidth").Text = "400";
            UiTestActions.Click(apply);
            Assert.That(edit.Document.Find(image.Id).Bounds, Is.EqualTo(new RectD(50, 60, 400, 200)),
                "width was edited last");

            var beforeInvalid = edit.Document.Copy();
            var history = edit.UndoCount;
            Field("LayerHeight").Text = "0";
            UiTestActions.Click(apply);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.StateEquals(beforeInvalid), Is.True);
                Assert.That(edit.UndoCount, Is.EqualTo(history));
            });
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
                "p4-aspect-" + Guid.NewGuid().ToString("N") + ".ppgproj");
            try
            {
                NativePackageStore.Save(path, edit.Document, "p4-test");
                var reopened = NativePackageStore.Read(path);
                Assert.That(reopened.StateEquals(edit.Document), Is.True);
                Assert.That(reopened.Find(image.Id).KeepAspect, Is.True);
                Assert.That(TestImages.RenderPng(reopened), Is.EqualTo(TestImages.RenderPng(edit.Document)));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        finally { window.Close(); }
    }

    [Test]
    public void BadgeAspectIsPinnedButHeightCanSetItsCanonicalScale()
    {
        AvaloniaTestHost.EnsureStarted();
        var badge = EditorDocument.NewBadge(new PointD(100, 120));
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1920, CanvasHeight = 1080,
            Layers = [badge] });
        edit.SelectedLayerId = badge.Id;
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var aspect = window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(control => control.Name == "LayerKeepAspect");
            var height = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(control => control.Name == "LayerHeight");
            var apply = UiTestActions.FindNamed(window, "ApplyLayerProperties");
            Assert.Multiple(() =>
            {
                Assert.That(aspect.IsChecked, Is.True);
                Assert.That(aspect.IsEnabled, Is.False);
                Assert.That(height.IsEnabled, Is.True);
            });
            height.Text = "216";
            UiTestActions.Click(apply);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(badge.Id).Bounds,
                    Is.EqualTo(new RectD(100, 120, 256, 216)));
                Assert.That(edit.Document.Find(badge.Id).KeepAspect, Is.True);
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(badge.Id).Bounds.Width, Is.EqualTo(128));
        }
        finally { window.Close(); }
    }
}
