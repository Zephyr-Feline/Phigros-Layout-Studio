using System.Diagnostics;
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
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>Exercise the same window across operations; isolated gesture tests cannot catch stale UI state.</summary>
public sealed class ContinuousEditingWorkflowTests
{
    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    public void ImportEditTransformHistorySaveReopenAndExportRemainConsistent(int width, int height)
    {
        AvaloniaTestHost.EnsureStarted();
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "continuous-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "自制 曲绘.png");
        var project = Path.Combine(folder, "连续编辑.ppgproj");
        var imageBytes = TestImages.EncodePng(80, 45, TestImages.SyntheticBgra(80, 45));
        File.WriteAllBytes(source, imageBytes);
        var edit = new EditSession(new EditorDocument { CanvasWidth = width, CanvasHeight = height });
        var native = new NativeProjectWorkflow(edit, TestImages.ValidateImages, "workflow-test");
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var window = new MainWindow(edit, nativeWorkflow: native,
            nativeSavePathPicker: () => Task.FromResult<string?>(project),
            filePathPicker: action => Task.FromResult<string?>(action switch
            {
                EditorFileAction.ImportImage => source,
                EditorFileAction.OpenProject => project,
                _ => throw new AssertionException("Unexpected file action: " + action)
            }));
        try
        {
            window.Show();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var checkpoints = new List<EditorDocument> { edit.Document.Copy() };
            UiTestActions.Click(window, "Import Image");
            PumpUntil(() => edit.UndoCount == 1 || UiTestActions.Error(window) is not null);
            Assert.That(UiTestActions.Error(window), Is.Null);
            var id = edit.SelectedLayerId!.Value;
            Checkpoint();

            Field("LayerName").Text = "连续编辑后的曲绘";
            Field("LayerX").Text = "220.12345678912345";
            Field("LayerY").Text = "240.98765432198765";
            Field("LayerWidth").Text = "320";
            Field("LayerHeight").Text = "180";
            Assert.That(UiTestActions.Focus(Field("LayerName")), Is.True);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.That(edit.Document.Find(id).Bounds, Is.EqualTo(new RectD(220.12345678912345, 240.98765432198765, 320, 180)));
            Checkpoint();

            var beforeMove = edit.Document.Find(id).Bounds;
            var centre = LayerGeometry.Centre(beforeMove);
            Drag(centre, new(centre.X + 42, centre.Y + 27));
            Assert.That(edit.Document.Find(id).Bounds.X, Is.EqualTo(beforeMove.X + 42).Within(1e-8));
            Assert.That(edit.Document.Find(id).Bounds.Y, Is.EqualTo(beforeMove.Y + 27).Within(1e-8));
            Checkpoint();

            var handle = LayerGeometry.HandlePoint(edit.Document.Find(id).Bounds, ResizeHandle.BottomRight);
            Drag(handle, new(handle.X + 96, handle.Y + 54));
            Assert.That(edit.Document.Find(id).Bounds.Width, Is.EqualTo(416).Within(1e-8));
            Assert.That(edit.Document.Find(id).Bounds.Height, Is.EqualTo(234).Within(1e-8));
            Checkpoint();

            var box = edit.Document.Find(id).Bounds;
            var pivot = LayerGeometry.Centre(box);
            var rotationHandle = new PointD(pivot.X, box.Y - 24 / Transform().Scale);
            Drag(rotationHandle, LayerGeometry.Rotate(rotationHandle, pivot, 45));
            Assert.That(edit.Document.Find(id).RotationDegrees, Is.EqualTo(45).Within(1e-8));
            Checkpoint();

            var final = edit.Document.Copy();
            var finalPng = TestImages.RenderPng(final, true, 640);
            centre = LayerGeometry.Centre(edit.Document.Find(id).Bounds);
            window.MouseDown(View(centre), MouseButton.Left);
            window.MouseMove(View(new(centre.X + 31, centre.Y + 18)), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            window.MouseUp(View(centre), MouseButton.Left);
            Assert.That(edit.Document.StateEquals(final), Is.True, "Cancelling after rotation must retain all committed edits.");
            Assert.That(edit.UndoCount, Is.EqualTo(checkpoints.Count - 1));
            Assert.That(canvas.IsInteracting, Is.False);

            for (var index = checkpoints.Count - 2; index >= 0; index--)
            {
                canvas.Focus();
                window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.None, null);
                Assert.That(edit.Document.StateEquals(checkpoints[index]), Is.True, $"Undo checkpoint {index}");
                Assert.That(edit.UndoCount, Is.EqualTo(index));
            }
            Assert.That(edit.IsDirty, Is.False);
            for (var index = 1; index < checkpoints.Count; index++)
            {
                window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.None, null);
                Assert.That(edit.Document.StateEquals(checkpoints[index]), Is.True, $"Redo checkpoint {index}");
                Assert.That(edit.UndoCount, Is.EqualTo(index));
            }
            Assert.That(TestImages.RenderPng(edit.Document, true, 640), Is.EqualTo(finalPng));

            // Select through the canvas after redo (history intentionally does not restore selection).
            centre = LayerGeometry.Centre(edit.Document.Find(id).Bounds);
            window.MouseDown(View(centre), MouseButton.Left);
            window.MouseUp(View(centre), MouseButton.Left);
            Assert.That(edit.SelectedLayerId, Is.EqualTo(id));
            Assert.That(Field("LayerName").Text, Is.EqualTo("连续编辑后的曲绘"));
            Assert.That(UiTestActions.Focus(Field("LayerName")), Is.True);
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            PumpUntil(() => File.Exists(project) || UiTestActions.Error(window) is not null);
            Assert.That(UiTestActions.Error(window), Is.Null);
            Assert.That(edit.IsDirty, Is.False);
            Assert.That(window.Title, Does.Not.Contain("*"));
            Assert.That(NativePackageStore.Read(project).StateEquals(final), Is.True);

            var exporter = new PngExportWorkflow(edit, TestImages.ValidateImages,
                TestImages.RenderPng, NativeDocumentStore.WriteBytesAtomically, new(), () => native.CurrentPath);
            var pngPath = Path.Combine(folder, "导出.png");
            exporter.Export(pngPath, true, 640);
            Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(finalPng));
            Assert.That(edit.UndoCount, Is.EqualTo(checkpoints.Count - 1));
            Assert.That(edit.IsDirty, Is.False);

            File.Delete(source); // The saved project must be independent of the original artwork path.
            UiTestActions.Click(window, "Open Project");
            PumpUntil(() => edit.UndoCount == 0 || UiTestActions.Error(window) is not null);
            Assert.That(UiTestActions.Error(window), Is.Null);
            Assert.That(edit.Document.StateEquals(final), Is.True);
            Assert.That(edit.SelectedLayerIds, Is.Empty);
            Assert.That(edit.RedoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
            Assert.That(window.GetVisualDescendants().OfType<ListBox>().Single().SelectedItems, Is.Empty);
            exporter.Export(pngPath, true, 640);
            Assert.That(File.ReadAllBytes(pngPath), Is.EqualTo(finalPng));
            Assert.That(edit.Document.Assets.Values.Single().ToArray(), Is.EqualTo(imageBytes));

            void Checkpoint()
            {
                Assert.That(edit.UndoCount, Is.EqualTo(checkpoints.Count), "Each completed edit must record exactly once.");
                Assert.That(edit.RedoCount, Is.Zero);
                Assert.That(canvas.IsInteracting, Is.False);
                checkpoints.Add(edit.Document.Copy());
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
            TextBox Field(string name) => window.GetLogicalDescendants().OfType<TextBox>().Single(field => field.Name == name);
            ViewportTransform Transform() => new(width, height, canvas.Bounds.Width, canvas.Bounds.Height);
            Point View(PointD logical)
            {
                var point = Transform().ToView(logical);
                return canvas.TranslatePoint(new(point.X, point.Y), window)!.Value;
            }
            void Drag(PointD start, PointD end)
            {
                var from = View(start); var to = View(end);
                window.MouseMove(from);
                window.MouseDown(from, MouseButton.Left);
                window.MouseMove(new((from.X + to.X) / 2, (from.Y + to.Y) / 2), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
                window.MouseMove(to, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
                window.MouseUp(to, MouseButton.Left);
            }
        }
        finally
        {
            edit.CancelGesture(); edit.MarkSaved(); window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var timer = Stopwatch.StartNew();
        while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(complete(), Is.True, "The file operation did not complete.");
        Dispatcher.UIThread.RunJobs();
    }
}
