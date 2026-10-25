using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5InteractionLifecycleTests
{
    [Test]
    public void NewCannotReplaceTheDocumentDuringMarqueeSelection()
    {
        var edit = Session(); var window = new MainWindow(edit);
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var point = WindowPoint(window, canvas, new(10, 10));
            window.MouseDown(point, MouseButton.Left);
            Assert.That(canvas.IsInteracting, Is.True);
            var before = edit.Document;
            UiTestActions.Click(FindButton(window, "New"));
            Assert.That(edit.Document, Is.SameAs(before));
            window.MouseUp(point, MouseButton.Left);
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); window.Close(); }
    }

    [Test]
    public void CanvasSettingsCannotChangeTheCoordinateSpaceDuringPanning()
    {
        var edit = Session(); var window = new MainWindow(edit);
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var point = canvas.TranslatePoint(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Middle);
            Assert.That(canvas.IsInteracting, Is.True, "The pointer must start a canvas pan before changing settings.");
            window.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Name == "CanvasWidth").Text = "1200";
            UiTestActions.Click(FindButton(window, "Apply canvas settings"));
            Assert.That(edit.Document.CanvasWidth, Is.EqualTo(1000)); Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(canvas.IsInteracting, Is.True);
            window.MouseUp(point, MouseButton.Middle);
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); window.Close(); }
    }

    [Test]
    public void AViewportResizeCannotChangeTheLogicalDeltaOfAnActiveMove()
    {
        var edit = Session(); using var host = new CanvasHost(edit);
        var start = host.View(150, 150); var end = host.View(250, 150);
        host.Window.MouseDown(start, MouseButton.Left);
        host.Canvas.Measure(new Size(400, 300)); host.Canvas.Arrange(new Rect(0, 0, 400, 300));
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        host.Window.MouseUp(end, MouseButton.Left);
        Assert.That(edit.Document.Layers[0].Bounds, Is.EqualTo(new RectD(200, 100, 200, 100)));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void AStaleMarqueeCannotRestoreSelectionIntoAReplacementDocument()
    {
        var edit = Session(); using var host = new CanvasHost(edit);
        edit.SelectedLayerId = edit.Document.Layers[0].Id;
        host.Window.MouseDown(host.View(10, 10), MouseButton.Left);
        edit.ReplaceDocument(new EditorDocument());
        Assert.DoesNotThrow(() => host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null));
        Assert.That(host.Canvas.IsInteracting, Is.False); Assert.That(edit.SelectedLayerIds, Is.Empty);
    }

    [TestCase("move", 1000, 600)]
    [TestCase("move", 1920, 1080)]
    [TestCase("move", 1440, 1080)]
    [TestCase("resize", 1000, 600)]
    [TestCase("resize", 1920, 1080)]
    [TestCase("resize", 1440, 1080)]
    [TestCase("marquee", 1000, 600)]
    [TestCase("marquee", 1920, 1080)]
    [TestCase("marquee", 1440, 1080)]
    public void ActiveInputAndPreviewKeepTheirPressTransformUntilRelease(string mode, int width, int height)
    {
        var edit = Session(width, height); edit.Document.Layers[0].KeepAspect = true;
        using var host = new CanvasHost(edit); var before = edit.Document.Copy();
        var start = mode switch { "resize" => host.View(300, 200), "marquee" => host.View(10, 10), _ => host.View(150, 150) };
        var end = mode switch { "resize" => host.View(400, 250), "marquee" => host.View(350, 230), _ => host.View(250, 150) };
        if (mode == "resize") edit.SelectedLayerId = edit.Document.Layers[0].Id;
        host.Window.MouseDown(start, MouseButton.Left);
        host.Canvas.Measure(new(400, 300)); host.Canvas.Arrange(new Rect(0, 0, 400, 300));
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        if (mode == "marquee")
        {
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { edit.Document.Layers[0].Id }));
            Assert.That(edit.Document.StateEquals(before), Is.True);
        }
        else
        {
            var expected = mode == "move" ? new RectD(200, 100, 200, 100) : new RectD(100, 100, 300, 150);
            var actual = edit.Document.Layers[0].Bounds;
            Assert.Multiple(() =>
            {
                Assert.That(actual.X, Is.EqualTo(expected.X).Within(1e-8));
                Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(1e-8));
                Assert.That(actual.Width, Is.EqualTo(expected.Width).Within(1e-8));
                Assert.That(actual.Height, Is.EqualTo(expected.Height).Within(1e-8));
            });
            // Interior pixels remain at the same screen position as the input transform.
            var inside = host.View(mode == "move" ? 230 : 180, 140);
            using var bitmap = new RenderTargetBitmap(new(800, 600), new(96, 96));
            using (var context = bitmap.CreateDrawingContext()) host.Canvas.Render(context);
            using var stream = new MemoryStream(); bitmap.Save(stream, new PngBitmapEncoderOptions());
            var (_, _, pixels) = TestImages.DecodePng(stream.ToArray());
            var offset = ((int)inside.Y * 800 + (int)inside.X) * 4;
            Assert.That(pixels[offset..(offset + 4)], Is.EqualTo(new byte[] { 255, 255, 255, 255 }));
        }
        host.Window.MouseUp(end, MouseButton.Left); Assert.That(host.Canvas.IsInteracting, Is.False);
        Assert.That(edit.UndoCount, Is.EqualTo(mode == "marquee" ? 0 : 1));
        var path = Path.Combine(Path.GetTempPath(), "interaction-" + Guid.NewGuid() + ".ppgproj");
        try
        {
            var exported = TestImages.RenderPng(edit.Document);
            NativePackageStore.Save(path, edit.Document, "interaction-tests");
            Assert.That(TestImages.RenderPng(NativePackageStore.Read(path)), Is.EqualTo(exported));
        }
        finally { File.Delete(path); }
        if (mode != "marquee") { edit.Undo(); Assert.That(edit.Document.StateEquals(before), Is.True); }
    }

    [TestCase("move", "escape")]
    [TestCase("resize", "escape")]
    [TestCase("marquee", "escape")]
    [TestCase("pan", "escape")]
    [TestCase("move", "capture")]
    [TestCase("resize", "capture")]
    [TestCase("marquee", "capture")]
    [TestCase("pan", "capture")]
    [TestCase("move", "detach")]
    [TestCase("resize", "detach")]
    [TestCase("marquee", "detach")]
    [TestCase("pan", "detach")]
    public void CancellingAnyInteractionRestoresStateAndLeavesHistoryAndCaptureUsable(string mode, string cancellation)
    {
        var edit = Session(); var id = edit.Document.Layers[0].Id;
        edit.UpdateLayer(id, layer => layer.Name = "redo branch"); edit.Undo(); edit.SelectedLayerId = id;
        using var host = new CanvasHost(edit); var before = edit.Document.Copy();
        var png = TestImages.RenderPng(before); var originalView = Paint(host.Canvas);
        var (start, end, button, modifiers) = Interaction(host, mode);
        host.Window.MouseDown(start, button); host.Window.MouseMove(end, modifiers);
        Assert.That(host.Canvas.IsInteracting, Is.True);
        var pointer = (IPointer)typeof(SceneCanvas).GetField("_capturedPointer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host.Canvas)!;
        if (cancellation == "escape") host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        else if (cancellation == "capture")
        {
            pointer.Capture(host.Window);
            Assert.That(pointer.Captured, Is.SameAs(host.Window), "Cancellation must preserve the new capture owner.");
        }
        else { host.Window.Content = null; host.Window.Content = host.Canvas; }
        Assert.That(host.Canvas.IsInteracting, Is.False); Assert.That(edit.ActiveGestureIds, Is.Empty);
        Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(edit.IsDirty, Is.False);
        Assert.That(edit.UndoCount, Is.Zero); Assert.That(edit.RedoCount, Is.EqualTo(1));
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { id }));
        Assert.That(TestImages.RenderPng(edit.Document), Is.EqualTo(png));
        Assert.That(Paint(host.Canvas), Is.EqualTo(originalView));
        pointer.Capture(null); host.Window.MouseUp(end, button);
        host.Window.MouseDown(host.View(150, 150), MouseButton.Left);
        host.Window.MouseMove(host.View(220, 150), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        host.Window.MouseUp(host.View(220, 150), MouseButton.Left);
        Assert.That(edit.UndoCount, Is.EqualTo(1)); Assert.That(edit.RedoCount, Is.Zero);
    }

    [TestCase("move", "escape")]
    [TestCase("resize", "move")]
    [TestCase("marquee", "release")]
    [TestCase("pan", "refresh")]
    [TestCase("move", "render")]
    [TestCase("marquee", "capture")]
    public void OutdatedPointerStateCannotEditOrReselectAReplacementDocument(string mode, string notification)
    {
        var edit = Session(); using var host = new CanvasHost(edit);
        edit.SelectedLayerId = edit.Document.Layers[0].Id;
        var (start, end, button, modifiers) = Interaction(host, mode);
        host.Window.MouseDown(start, button); host.Window.MouseMove(end, modifiers);
        var replacement = Session().Document;
        edit.ReplaceDocument(replacement); edit.SelectedLayerId = edit.Document.Layers[0].Id;
        var state = edit.Document.Copy(); var selection = edit.SelectedLayerIds.ToArray();
        Assert.DoesNotThrow(() =>
        {
            switch (notification)
            {
                case "escape": host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null); break;
                case "move": host.Window.MouseMove(end, modifiers); break;
                case "release": host.Window.MouseUp(end, button); break;
                case "refresh": host.Canvas.Refresh(); break;
                case "render": Paint(host.Canvas); break;
                case "capture":
                    var pointer = (IPointer)typeof(SceneCanvas).GetField("_capturedPointer",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host.Canvas)!;
                    pointer.Capture(null); break;
            }
        });
        Assert.That(host.Canvas.IsInteracting, Is.False); Assert.That(edit.Document.StateEquals(state), Is.True);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection)); Assert.That(edit.UndoCount, Is.Zero); Assert.That(edit.IsDirty, Is.False);
    }

    [TestCase("marquee", "New")]
    [TestCase("pan", "New")]
    [TestCase("marquee", "Save Project As")]
    [TestCase("pan", "Save Project As")]
    [TestCase("marquee", "Save Temporary JSON")]
    [TestCase("pan", "Save Temporary JSON")]
    [TestCase("marquee", "Import Image")]
    [TestCase("pan", "Import Image")]
    [TestCase("marquee", "Open Project")]
    [TestCase("pan", "Open Project")]
    public void FileAndDocumentActionsRejectTransientSelectionAndPanThenAllowRetry(string mode, string action)
    {
        var edit = Session(); var picks = 0;
        var window = new MainWindow(edit, nativeSavePathPicker: () => { picks++; return Task.FromResult<string?>(null); },
            filePathPicker: _ => { picks++; return Task.FromResult<string?>(null); });
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var start = WindowPoint(window, canvas, new(10, 10)); var before = edit.Document;
            window.MouseDown(start, mode == "pan" ? MouseButton.Middle : MouseButton.Left);
            UiTestActions.Click(FindButton(window, action));
            Assert.That(picks, Is.Zero); Assert.That(edit.Document, Is.SameAs(before)); Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(Status(window), Does.Contain("拖动或缩放"));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            UiTestActions.Click(FindButton(window, "Import Image"));
            Assert.That(picks, Is.EqualTo(1));
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); window.Close(); }
    }

    [TestCase("move")]
    [TestCase("resize")]
    [TestCase("marquee")]
    [TestCase("pan")]
    public void ClosingRequiresEveryKindOfCanvasInteractionToFinish(string mode)
    {
        var edit = Session(); var window = new MainWindow(edit);
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            edit.SelectedLayerId = edit.Document.Layers[0].Id;
            var logical = mode switch { "resize" => new PointD(300, 200), "marquee" => new PointD(10, 10), _ => new PointD(150, 150) };
            window.MouseDown(WindowPoint(window, canvas, logical), mode == "pan" ? MouseButton.Middle : MouseButton.Left);
            window.Close(); Assert.That(window.IsVisible, Is.True); Assert.That(Status(window), Does.Contain("拖动或缩放"));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            window.Close(); Assert.That(window.IsVisible, Is.False);
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); if (window.IsVisible) window.Close(); }
    }

    [TestCase("marquee")]
    [TestCase("pan")]
    public void AllDocumentEditingButtonsAndImmediateBackgroundCheckboxPreserveAnActiveViewInteraction(string mode)
    {
        var edit = Session(); var window = new MainWindow(edit);
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var start = WindowPoint(window, canvas, new(10, 10)); var before = edit.Document.Copy();
            window.MouseDown(start, mode == "pan" ? MouseButton.Middle : MouseButton.Left);
            foreach (var action in new[] { "Add Shape", "Add Text", "Undo", "Apply canvas settings" })
            {
                UiTestActions.Click(FindButton(window, action));
                Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(Status(window), Does.Contain("拖动或缩放"));
            }
            var automatic = window.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "AutomaticBackground");
            automatic.IsChecked = !automatic.IsChecked;
            Assert.That(edit.Document.StateEquals(before), Is.True);
            Assert.That(automatic.IsChecked, Is.EqualTo(before.BackgroundBlend.Enabled)); Assert.That(edit.UndoCount, Is.Zero);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            UiTestActions.Click(FindButton(window, "Add Shape")); Assert.That(edit.UndoCount, Is.EqualTo(1));
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); window.Close(); }
    }

    [Test]
    public void ResizedViewportPanningUsesScreenDeltaThenTheNewFitAndRemainsUndoFree()
    {
        var edit = Session(); using var host = new CanvasHost(edit); var start = host.View(150, 150);
        host.Window.MouseDown(start, MouseButton.Middle);
        host.Canvas.Measure(new(400, 300)); host.Canvas.Arrange(new Rect(0, 0, 400, 300));
        host.Window.MouseMove(new(start.X + 60, start.Y + 35), RawInputModifiers.MiddleMouseButton);
        var (_, _, during) = TestImages.DecodePng(Paint(host.Canvas));
        var inside = host.View(150, 150); var offset = ((int)(inside.Y + 35) * 800 + (int)(inside.X + 60)) * 4;
        Assert.That(during[offset..(offset + 4)], Is.EqualTo(new byte[] { 255, 255, 255, 255 }));
        host.Window.MouseUp(new(start.X + 60, start.Y + 35), MouseButton.Middle);
        var point = new ViewportTransform(1000, 600, 400, 300, pan: new(60, 35)).ToView(new PointD(150, 150));
        host.Window.MouseDown(new(point.X, point.Y), MouseButton.Left); host.Window.MouseUp(new(point.X, point.Y), MouseButton.Left);
        Assert.That(edit.SelectedLayerId, Is.EqualTo(edit.Document.Layers[0].Id)); Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void ProgrammaticCanvasResizeCancelsMarqueeWithoutUndoingTheResizeOrRestoringMissingIds()
    {
        var edit = Session(); using var host = new CanvasHost(edit);
        edit.SelectedLayerId = edit.Document.Layers[0].Id; host.Window.MouseDown(host.View(10, 10), MouseButton.Left);
        edit.UpdateDocument(document => document.CanvasWidth = 1200); host.Canvas.Refresh();
        Assert.That(host.Canvas.IsInteracting, Is.False); Assert.That(edit.Document.CanvasWidth, Is.EqualTo(1200));
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { edit.Document.Layers[0].Id })); Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void NormalCommitNotifiesTheHostOnlyAfterInteractionEndsAndRecordsOneEntry()
    {
        var edit = Session(); using var host = new CanvasHost(edit);
        var commits = 0;
        host.Canvas.Changed += () => { if (edit.UndoCount == 1) { commits++; Assert.That(host.Canvas.IsInteracting, Is.False); host.Canvas.Refresh(); } };
        host.Window.MouseDown(host.View(150, 150), MouseButton.Left);
        host.Window.MouseMove(host.View(220, 170), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        host.Window.MouseUp(host.View(220, 170), MouseButton.Left);
        Assert.That(commits, Is.EqualTo(1)); Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [TestCase("marquee")]
    [TestCase("pan")]
    public void SaveShortcutsCannotOpenAPickerDuringTransientViewInteractions(string mode)
    {
        var edit = Session(); var calls = 0;
        var window = new MainWindow(edit, nativeSavePathPicker: () => { calls++; return Task.FromResult<string?>(null); });
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            window.MouseDown(WindowPoint(window, canvas, new(10, 10)), mode == "pan" ? MouseButton.Middle : MouseButton.Left);
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            window.KeyPress(Key.S, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
            Assert.That(calls, Is.Zero); Assert.That(Status(window), Does.Contain("拖动或缩放"));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null); Assert.That(calls, Is.EqualTo(1));
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); window.Close(); }
    }

    [TestCase("move")]
    [TestCase("pan")]
    public void RepeatedSecondaryButtonReleasesDoNotEndTheOwningInteraction(string mode)
    {
        var edit = Session(); using var host = new CanvasHost(edit);
        var primary = mode == "pan" ? MouseButton.Middle : MouseButton.Left;
        var held = mode == "pan" ? RawInputModifiers.MiddleMouseButton : RawInputModifiers.LeftMouseButton;
        host.Window.MouseDown(host.View(150, 150), primary);
        host.Window.MouseMove(host.View(220, 150), held | RawInputModifiers.Alt);
        host.Window.MouseDown(host.View(220, 150), MouseButton.Right, held);
        host.Window.MouseUp(host.View(220, 150), MouseButton.Right, held);
        Assert.That(host.Canvas.IsInteracting, Is.True); Assert.That(edit.UndoCount, Is.Zero);
        host.Window.MouseUp(host.View(220, 150), primary);
        Assert.That(host.Canvas.IsInteracting, Is.False); Assert.That(edit.UndoCount, Is.EqualTo(mode == "pan" ? 0 : 1));
    }

    [TestCase("marquee", "Save Project As")]
    [TestCase("pan", "Save Project As")]
    [TestCase("marquee", "Save Temporary JSON")]
    [TestCase("pan", "Save Temporary JSON")]
    [TestCase("marquee", "Import local font (TTF / OTF)")]
    [TestCase("pan", "Import local font (TTF / OTF)")]
    [TestCase("marquee", "Open layout template")]
    [TestCase("pan", "Open layout template")]
    [TestCase("marquee", "Save layout template")]
    [TestCase("pan", "Save layout template")]
    [TestCase("marquee", "Open Project")]
    [TestCase("pan", "Open Project")]
    [TestCase("marquee", "Save selection as project")]
    [TestCase("pan", "Save selection as project")]
    [TestCase("marquee", "Import project layers")]
    [TestCase("pan", "Import project layers")]
    public void APathChosenAfterAViewInteractionStartsCannotReadWriteOrReplaceTheDocument(string mode, string action)
    {
        var edit = Session();
        if (action == "Import local font (TTF / OTF)")
        {
            edit.Document.Layers[0].Kind = LayerKind.Text;
            edit.ReplaceDocument(edit.Document);
        }
        edit.SelectedLayerId = edit.Document.Layers[0].Id;
        var folder = Path.Combine(Path.GetTempPath(), "p5-interaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, "existing.ppgproj"); File.WriteAllBytes(path, [1, 2, 3]);
        var pending = new TaskCompletionSource<string?>(); var picks = 0;
        Task<string?> Choose() { picks++; return pending.Task; }
        var fonts = new LocalFontLibrary(Path.Combine(folder, "fonts"));
        var window = new MainWindow(edit, fonts, nativeSavePathPicker: Choose, selectionProjectPathPicker: Choose,
            projectLayerPathPicker: Choose, filePathPicker: _ => Choose());
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            UiTestActions.Click(FindButton(window, action)); Assert.That(picks, Is.EqualTo(1));
            window.MouseDown(WindowPoint(window, canvas, new(10, 10)), mode == "pan" ? MouseButton.Middle : MouseButton.Left);
            var current = edit.Document; var state = current.Copy(); var selection = edit.SelectedLayerIds.ToArray();
            pending.SetResult(path); Dispatcher.UIThread.RunJobs();
            Assert.That(Status(window), Does.Contain("拖动或缩放")); Assert.That(canvas.IsInteracting, Is.True);
            Assert.That(edit.Document, Is.SameAs(current)); Assert.That(edit.Document.StateEquals(state), Is.True);
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection)); Assert.That(edit.UndoCount, Is.Zero); Assert.That(edit.IsDirty, Is.False);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 })); Assert.That(Directory.Exists(fonts.DirectoryPath), Is.False);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            pending = new TaskCompletionSource<string?>(); pending.SetResult(null);
            UiTestActions.Click(FindButton(window, "Import Image")); Assert.That(picks, Is.EqualTo(2));
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); window.Close(); Directory.Delete(folder, recursive: true); }
    }

    [TestCase("marquee", "Import Image")]
    [TestCase("pan", "Import Image")]
    [TestCase("marquee", "Replace image")]
    [TestCase("pan", "Replace image")]
    [TestCase("marquee", "Set background image")]
    [TestCase("pan", "Set background image")]
    public void AnImageLoadedDuringAViewInteractionCannotCommitOrDisturbThatInteraction(string mode, string action)
    {
        var edit = Session(); var picture = Picture(); var layer = edit.Document.Layers[0];
        layer.Kind = LayerKind.Image; layer.AssetId = Guid.NewGuid(); edit.Document.AddAsset(layer.AssetId.Value, picture.Image);
        edit.ReplaceDocument(edit.Document); edit.SelectedLayerId = layer.Id;
        var pending = new TaskCompletionSource<LoadedImage>(); var picks = 0;
        var window = new MainWindow(edit, filePathPicker: _ => Task.FromResult<string?>(++picks == 1 ? "image.png" : null),
            imageLoader: _ => pending.Task);
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            UiTestActions.Click(FindButton(window, action));
            window.MouseDown(WindowPoint(window, canvas, new(10, 10)), mode == "pan" ? MouseButton.Middle : MouseButton.Left);
            var state = edit.Document.Copy(); var selection = edit.SelectedLayerIds.ToArray();
            pending.SetResult(picture); Dispatcher.UIThread.RunJobs();
            Assert.That(Status(window), Does.Contain("拖动或缩放")); Assert.That(canvas.IsInteracting, Is.True);
            Assert.That(edit.Document.StateEquals(state), Is.True); Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection));
            Assert.That(edit.UndoCount, Is.Zero); Assert.That(edit.IsDirty, Is.False);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            UiTestActions.Click(FindButton(window, "Import Image")); Assert.That(picks, Is.EqualTo(2));
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); window.Close(); }
    }

    [TestCase("marquee", 3)]
    [TestCase("pan", 3)]
    [TestCase("marquee", 5)]
    [TestCase("pan", 5)]
    public void AViewInteractionDuringBatchImageLoadingRejectsTheWholeBatch(string mode, int count)
    {
        var edit = Session(); var pending = new TaskCompletionSource<LoadedImage>(); var reads = 0; var picks = 0;
        var window = new MainWindow(edit, songImagePathsPicker: () => Task.FromResult<IReadOnlyList<string>>(++picks == 1
            ? Enumerable.Range(0, count).Select(i => $"image-{i}.png").ToArray() : []),
            imageLoader: _ => { reads++; return pending.Task; });
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            UiTestActions.Click(FindButton(window, "Build 3 / 5 songs")); Assert.That(reads, Is.EqualTo(1));
            window.MouseDown(WindowPoint(window, canvas, new(10, 10)), mode == "pan" ? MouseButton.Middle : MouseButton.Left);
            var state = edit.Document.Copy(); pending.SetResult(Picture()); Dispatcher.UIThread.RunJobs();
            Assert.That(Status(window), Does.Contain("拖动或缩放")); Assert.That(canvas.IsInteracting, Is.True);
            Assert.That(edit.Document.StateEquals(state), Is.True); Assert.That(edit.UndoCount, Is.Zero); Assert.That(reads, Is.EqualTo(1));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            UiTestActions.Click(FindButton(window, "Build 3 / 5 songs")); Assert.That(picks, Is.EqualTo(2));
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); window.Close(); }
    }

    [TestCase("marquee")]
    [TestCase("pan")]
    public void CancellingAPendingPathChoiceKeepsTheNewViewInteractionAndReleasesTheFileGate(string mode)
    {
        var edit = Session(); var pending = new TaskCompletionSource<string?>(); var picks = 0;
        var window = new MainWindow(edit, filePathPicker: _ => { picks++; return pending.Task; });
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            UiTestActions.Click(FindButton(window, "Import Image"));
            window.MouseDown(WindowPoint(window, canvas, new(10, 10)), mode == "pan" ? MouseButton.Middle : MouseButton.Left);
            pending.SetResult(null); Dispatcher.UIThread.RunJobs();
            Assert.That(UiTestActions.Error(window), Is.Null); Assert.That(Status(window), Is.EqualTo("已取消导入图片。"));
            Assert.That(canvas.IsInteracting, Is.True); Assert.That(edit.UndoCount, Is.Zero);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            UiTestActions.Click(FindButton(window, "Import Image")); Assert.That(picks, Is.EqualTo(2));
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); window.Close(); }
    }

    [TestCase("marquee")]
    [TestCase("pan")]
    public void ABatchPathChoiceReturningDuringAViewInteractionCannotStartAnyImageRead(string mode)
    {
        var edit = Session(); var pending = new TaskCompletionSource<IReadOnlyList<string>>(); var reads = 0;
        var window = new MainWindow(edit, songImagePathsPicker: () => pending.Task,
            imageLoader: _ => { reads++; return Task.FromResult(Picture()); });
        try
        {
            window.Show(); var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            UiTestActions.Click(FindButton(window, "Build 3 / 5 songs"));
            window.MouseDown(WindowPoint(window, canvas, new(10, 10)), mode == "pan" ? MouseButton.Middle : MouseButton.Left);
            var state = edit.Document.Copy(); pending.SetResult(["1.png", "2.png", "3.png"]); Dispatcher.UIThread.RunJobs();
            Assert.That(Status(window), Does.Contain("拖动或缩放")); Assert.That(canvas.IsInteracting, Is.True);
            Assert.That(reads, Is.Zero); Assert.That(edit.Document.StateEquals(state), Is.True); Assert.That(edit.UndoCount, Is.Zero);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); window.Close(); }
    }

    private static LoadedImage Picture() => new(new(TestImages.RenderPng(new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 })), 320, 180);

    private static (Point Start, Point End, MouseButton Button, RawInputModifiers Modifiers) Interaction(CanvasHost host, string mode)
        => mode switch
        {
            "resize" => (host.View(300, 200), host.View(390, 250), MouseButton.Left, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt),
            "marquee" => (host.View(10, 10), host.View(350, 230), MouseButton.Left, RawInputModifiers.LeftMouseButton),
            "pan" => (host.View(150, 150), host.View(220, 180), MouseButton.Middle, RawInputModifiers.MiddleMouseButton),
            _ => (host.View(150, 150), host.View(220, 180), MouseButton.Left, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt)
        };

    private static byte[] Paint(SceneCanvas canvas)
    {
        using var target = new RenderTargetBitmap(new(800, 600), new(96, 96));
        using (var context = target.CreateDrawingContext()) canvas.Render(context);
        using var stream = new MemoryStream(); target.Save(stream, new PngBitmapEncoderOptions()); return stream.ToArray();
    }

    private static string? Status(Window window) => UiTestActions.Status(window);

    private static EditSession Session(int width = 1000, int height = 600) => new(new EditorDocument { CanvasWidth = width, CanvasHeight = height,
        Layers = [new Layer { Kind = LayerKind.Shape, Bounds = new(100, 100, 200, 100) }] });

    private static Control FindButton(Window window, string label) => UiTestActions.Find(window, label);

    private static Point WindowPoint(Window window, SceneCanvas canvas, PointD logical)
    {
        var position = new ViewportTransform(1000, 600, canvas.Bounds.Width, canvas.Bounds.Height).ToView(logical);
        return canvas.TranslatePoint(new Point(position.X, position.Y), window)!.Value;
    }

    private sealed class CanvasHost : IDisposable
    {
        public SceneCanvas Canvas { get; }
        public Window Window { get; }
        private readonly ViewportTransform _view;
        public CanvasHost(EditSession edit)
        {
            AvaloniaTestHost.EnsureStarted(); Canvas = new SceneCanvas(edit);
            Window = new Window { Width = 800, Height = 600, Content = Canvas }; Window.Show();
            _view = new(edit.Document.CanvasWidth, edit.Document.CanvasHeight, Canvas.Bounds.Width, Canvas.Bounds.Height);
        }
        public Point View(double x, double y) { var point = _view.ToView(new PointD(x, y)); return new(point.X, point.Y); }
        public void Dispose() => Window.Close();
    }
}
