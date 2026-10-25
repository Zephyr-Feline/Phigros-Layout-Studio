using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4SelectionPolishTests
{
    [TestCase(LayerOrderAction.Forward, "02143")]
    [TestCase(LayerOrderAction.Backward, "10324")]
    [TestCase(LayerOrderAction.Front, "02413")]
    [TestCase(LayerOrderAction.Back, "13024")]
    public void BatchOrderIsStableUndoableAndPersisted(LayerOrderAction action, string expected)
    {
        var layers = Enumerable.Range(0, 5).Select(i => new Layer { Kind = LayerKind.Shape,
            Name = i.ToString(), ColorArgb = 0xFF000000u + (uint)(i * 0x303030),
            Bounds = new RectD(30, 30, 120, 90) }).ToArray();
        var edit = new EditSession(new EditorDocument { CanvasWidth = 320, CanvasHeight = 180,
            Layers = layers.ToList() });
        edit.SetSelection([layers[3].Id, layers[1].Id]); // Input order must not reverse paint order.
        var before = PngExporter.Render(edit.Document);
        edit.ReorderLayers(edit.SelectedLayerIds, action);
        Assert.That(string.Concat(edit.Document.Layers.Select(layer => layer.Name)), Is.EqualTo(expected));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { layers[3].Id, layers[1].Id }));
        var after = PngExporter.Render(edit.Document);
        Assert.That(edit.Undo(), Is.True);
        Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(before));
        Assert.That(edit.Redo(), Is.True);
        Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(after));
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "selection-test");
            Assert.That(PngExporter.Render(NativePackageStore.Read(path)), Is.EqualTo(after));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestCase(LayerOrderAction.Forward)]
    [TestCase(LayerOrderAction.Backward)]
    [TestCase(LayerOrderAction.Front)]
    [TestCase(LayerOrderAction.Back)]
    public void NoOpOrderPreservesSavepointAndRedoAndRejectsActiveGesture(LayerOrderAction action)
    {
        var edit = Session();
        var id = edit.Document.Layers[0].Id;
        edit.UpdateLayer(id, layer => layer.Name = "changed");
        edit.Undo();
        edit.ReorderLayers(edit.Document.Layers.Select(layer => layer.Id), action);
        Assert.That(edit.IsDirty, Is.False);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(edit.RedoCount, Is.EqualTo(1));
        var state = edit.Document.Copy();
        Assert.Throws<KeyNotFoundException>(() => edit.ReorderLayers([id, Guid.NewGuid()], action));
        Assert.That(edit.Document.StateEquals(state), Is.True);
        edit.BeginGesture(id);
        Assert.Throws<InvalidOperationException>(() => edit.ReorderLayers([id], action));
        edit.CancelGesture();
    }

    [Test]
    public void RegionRequiresFullBoundsAndExcludesLockedHiddenWithoutEditingHistory()
    {
        var edit = Session();
        edit.Document.Layers[2].Locked = true;
        edit.Document.Layers[3].Visible = false;
        var state = edit.Document.Copy();
        edit.SelectCanvasRegion(new RectD(50, 50, 700, 200));
        Assert.That(edit.SelectedLayerIds, Is.EquivalentTo(edit.Document.Layers.Take(2).Select(layer => layer.Id)));
        edit.SelectCanvasRegion(new RectD(120, 120, 30, 30));
        Assert.That(edit.SelectedLayerIds, Is.Empty, "A partial intersection is not enclosure.");
        edit.SelectAllEditableLayers();
        Assert.That(edit.SelectedLayerIds.Count, Is.EqualTo(2));
        Assert.That(edit.Document.StateEquals(state), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(edit.IsDirty, Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() => edit.SelectCanvasRegion(new RectD(0, 0, double.NaN, 20)));
    }

    [TestCase(1280, 720, false)]
    [TestCase(1000, 750, true)]
    public void MarqueeSelectsInBothDirectionsAndDoesNotAffectExport(int width, int height, bool reverse)
    {
        var edit = Session(width, height);
        using var host = new CanvasHost(edit);
        var before = PngExporter.Render(edit.Document);
        var a = host.View(50, 50);
        var b = host.View(410, 230);
        if (reverse) (a, b) = (b, a);
        host.Window.MouseDown(a, MouseButton.Left);
        host.Window.MouseMove(b, RawInputModifiers.LeftMouseButton);
        Assert.That(edit.SelectedLayerIds, Is.EquivalentTo(edit.Document.Layers.Take(2).Select(layer => layer.Id)));
        Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(before));
        host.Window.MouseUp(b, MouseButton.Left);
        Assert.That(host.Canvas.IsInteracting, Is.False);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(edit.IsDirty, Is.False);
        // Existing multi-selection drag still moves both, with a single history entry.
        host.Window.MouseDown(host.View(150, 150), MouseButton.Left);
        host.Window.MouseMove(host.View(180, 150), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        host.Window.MouseUp(host.View(180, 150), MouseButton.Left);
        Assert.That(edit.Document.Layers[0].Bounds.X, Is.EqualTo(130).Within(1e-6));
        Assert.That(edit.Document.Layers[1].Bounds.X, Is.EqualTo(330).Within(1e-6));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [TestCase(RawInputModifiers.Control)]
    [TestCase(RawInputModifiers.Shift)]
    public void AdditiveMarqueeAndEscapeRestoreOriginalSelection(RawInputModifiers modifier)
    {
        var edit = Session();
        edit.SelectedLayerId = edit.Document.Layers[3].Id;
        using var host = new CanvasHost(edit);
        host.Window.MouseDown(host.View(50, 50), MouseButton.Left, modifier);
        host.Window.MouseMove(host.View(410, 230), RawInputModifiers.LeftMouseButton | modifier);
        Assert.That(edit.SelectedLayerIds.Count, Is.EqualTo(3));
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { edit.Document.Layers[3].Id }));
        Assert.That(host.Canvas.IsInteracting, Is.False);
        host.Window.MouseUp(host.View(410, 230), MouseButton.Left);
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void ControlClickTogglesShiftAddsAndCaptureLossRestoresSelection()
    {
        var edit = Session();
        using var host = new CanvasHost(edit);
        void Click(int index, RawInputModifiers modifiers)
        {
            var at = host.View(150 + index * 200, 150);
            host.Window.MouseDown(at, MouseButton.Left, modifiers);
            host.Window.MouseUp(at, MouseButton.Left, modifiers);
        }
        Click(0, RawInputModifiers.None);
        Click(1, RawInputModifiers.Control);
        Click(2, RawInputModifiers.Shift);
        Click(0, RawInputModifiers.Control);
        Assert.That(edit.SelectedLayerIds, Is.EquivalentTo(edit.Document.Layers.Skip(1).Take(2).Select(layer => layer.Id)));
        var selected = edit.SelectedLayerIds.ToArray();
        IPointer? pointer = null;
        host.Canvas.AddHandler(InputElement.PointerPressedEvent, (_, args) => pointer = args.Pointer,
            RoutingStrategies.Bubble, handledEventsToo: true);
        host.Window.MouseDown(host.View(40, 40), MouseButton.Left);
        host.Window.MouseMove(host.View(220, 220), RawInputModifiers.LeftMouseButton);
        // Capture transfer, as when a native popup or another control takes the pointer.
        Assert.That(pointer, Is.Not.Null);
        pointer!.Capture(host.Window);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(selected));
        Assert.That(edit.UndoCount, Is.Zero);
        pointer.Capture(null);
        host.Window.MouseUp(host.View(220, 220), MouseButton.Left);
    }

    [TestCase(1280, 720)]
    [TestCase(1000, 750)]
    public void ZoomKeepsPointerAnchorAndFitRestoresInputWithoutDocumentChanges(int width, int height)
    {
        var edit = Session(width, height);
        using var host = new CanvasHost(edit);
        var state = edit.Document.Copy();
        var anchor = host.View(150, 150);
        host.Window.MouseWheel(anchor, new Vector(0, 5));
        host.Window.MouseDown(anchor, MouseButton.Left);
        Assert.That(edit.SelectedLayerId, Is.EqualTo(edit.Document.Layers[0].Id));
        var end = new Point(anchor.X + 40, anchor.Y);
        // Wheel and middle button cannot change the view during an edit.
        host.Window.MouseWheel(anchor, new Vector(0, 10), RawInputModifiers.LeftMouseButton);
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        var scale = host.Transform.Scale * Math.Pow(1.1, 5);
        Assert.That(edit.Document.Layers[0].Bounds.X, Is.EqualTo(100 + 40 / scale).Within(1e-6));
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        host.Window.MouseUp(end, MouseButton.Left);
        host.Canvas.FitView();
        host.Window.MouseDown(host.View(750, 150), MouseButton.Left);
        host.Window.MouseUp(host.View(750, 150), MouseButton.Left);
        Assert.That(edit.SelectedLayerId, Is.EqualTo(edit.Document.Layers[3].Id));
        Assert.That(edit.Document.StateEquals(state), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [TestCase("Bring to front", "2301")]
    [TestCase("Send to back", "0123")]
    [TestCase("Move forward", "2013")]
    [TestCase("Move backward", "0123")]
    public void MainWindowButtonsReorderWholeSelectionAndCanvasSelectAllExcludesTextFocus(string buttonText, string expected)
    {
        var edit = Session();
        edit.Document.Layers[2].Locked = true;
        edit.Document.Layers[3].Visible = false;
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            canvas.Focus();
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(edit.SelectedLayerIds.Count, Is.EqualTo(2));
            UiTestActions.Click(window, buttonText);
            Assert.That(string.Concat(edit.Document.Layers.Select(layer => layer.Name)), Is.EqualTo(expected));
            Assert.That(edit.UndoCount, Is.EqualTo(expected == "0123" ? 0 : 1));
            if (buttonText == "Bring to front")
            {
                window.UpdateLayout();
                var frontButton = window.GetLogicalDescendants().OfType<Menu>().Single(menu => menu.Name == "MainMenu");
                var buttonAt = frontButton.TranslatePoint(default, window)!.Value;
                Assert.That(buttonAt.X, Is.GreaterThanOrEqualTo(0));
                Assert.That(buttonAt.X + frontButton.Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width));
                using var frame = new RenderTargetBitmap(new PixelSize(1200, 760));
                frame.Render(window);
                frame.Save(Path.Combine(TestContext.CurrentContext.WorkDirectory, "selection-editor-preview.png"),
                    new PngBitmapEncoderOptions());
            }
            edit.SetSelection([edit.Document.Layers[0].Id]);
            var field = window.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Name == "CanvasWidth");
            field.BringIntoView();
            window.UpdateLayout();
            Assert.That(UiTestActions.Focus(field), Is.True, "Ctrl+A exclusion must be tested with a real input field focused.");
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(edit.SelectedLayerIds.Count, Is.EqualTo(1));
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void AdditiveMarqueeCommitsAndEmptyClickClearsWithoutHistory()
    {
        var edit = Session();
        edit.SelectedLayerId = edit.Document.Layers[3].Id;
        using var host = new CanvasHost(edit);
        host.Window.MouseDown(host.View(50, 50), MouseButton.Left, RawInputModifiers.Control);
        host.Window.MouseMove(host.View(220, 220), RawInputModifiers.LeftMouseButton | RawInputModifiers.Control);
        host.Window.MouseUp(host.View(220, 220), MouseButton.Left, RawInputModifiers.Control);
        Assert.That(edit.SelectedLayerIds, Is.EquivalentTo(new[] { edit.Document.Layers[0].Id, edit.Document.Layers[3].Id }));
        host.Window.MouseDown(host.View(900, 500), MouseButton.Left);
        host.Window.MouseUp(host.View(900, 500), MouseButton.Left);
        Assert.That(edit.SelectedLayerIds, Is.Empty);
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void PanCancellationAndFitButtonRestoreTheView()
    {
        var edit = Session();
        using var host = new CanvasHost(edit);
        var start = host.View(900, 500);
        var end = new Point(start.X - 180, start.Y - 80);
        host.Window.MouseDown(start, MouseButton.Middle);
        host.Window.MouseMove(end, RawInputModifiers.MiddleMouseButton);
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        host.Window.MouseUp(end, MouseButton.Middle);
        host.Window.MouseDown(host.View(150, 150), MouseButton.Left);
        host.Window.MouseUp(host.View(150, 150), MouseButton.Left);
        Assert.That(edit.SelectedLayerId, Is.EqualTo(edit.Document.Layers[0].Id));
        host.Window.MouseDown(start, MouseButton.Middle);
        host.Window.MouseMove(end, RawInputModifiers.MiddleMouseButton);
        host.Window.MouseUp(end, MouseButton.Middle);
        host.Canvas.FitView();
        host.Window.MouseDown(host.View(750, 150), MouseButton.Left);
        host.Window.MouseUp(host.View(750, 150), MouseButton.Left);
        Assert.That(edit.SelectedLayerId, Is.EqualTo(edit.Document.Layers[3].Id));
        Assert.That(edit.IsDirty, Is.False);
    }

    [Test]
    public void ZoomedMarqueeUsesPairedTransformAndIgnoresWheelUntilRelease()
    {
        var edit = Session();
        using var host = new CanvasHost(edit);
        var anchor = host.View(200, 200);
        host.Window.MouseWheel(anchor, new Vector(0, 2));
        Point Zoomed(double x, double y)
        {
            var point = host.View(x, y);
            return new Point(anchor.X + (point.X - anchor.X) * 1.21,
                anchor.Y + (point.Y - anchor.Y) * 1.21);
        }
        var start = Zoomed(50, 50);
        var end = Zoomed(410, 230);
        host.Window.MouseDown(start, MouseButton.Left);
        host.Window.MouseWheel(start, new Vector(0, 8), RawInputModifiers.LeftMouseButton);
        host.Canvas.FitView(); // Also ignored while selecting.
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        host.Window.MouseUp(end, MouseButton.Left);
        Assert.That(edit.SelectedLayerIds, Is.EquivalentTo(edit.Document.Layers.Take(2).Select(layer => layer.Id)));
        Assert.That(edit.IsDirty, Is.False);
    }

    [Test]
    public void ExplicitLayerOrderCanIncludeLockedHiddenMembersWithoutExpandingGroups()
    {
        var edit = Session();
        var group = Guid.NewGuid();
        edit.Document.Layers[0].GroupId = group;
        edit.Document.Layers[1].GroupId = group;
        edit.Document.Layers[0].Locked = true;
        edit.Document.Layers[2].Visible = false;
        edit.ReorderLayers([edit.Document.Layers[0].Id, edit.Document.Layers[2].Id], LayerOrderAction.Front);
        Assert.That(string.Concat(edit.Document.Layers.Select(layer => layer.Name)), Is.EqualTo("1302"));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        edit.Undo();
        Assert.That(string.Concat(edit.Document.Layers.Select(layer => layer.Name)), Is.EqualTo("0123"));
    }

    private static EditSession Session(int width = 1000, int height = 600) => new(new EditorDocument
    {
        CanvasWidth = width, CanvasHeight = height,
        Layers = Enumerable.Range(0, 4).Select(i => new Layer { Kind = LayerKind.Shape,
            Name = i.ToString(), Bounds = new RectD(100 + i * 200, 100, 100, 100) }).ToList()
    });

    private sealed class CanvasHost : IDisposable
    {
        public SceneCanvas Canvas { get; }
        public Window Window { get; }
        public ViewportTransform Transform { get; }
        public CanvasHost(EditSession edit)
        {
            Canvas = new SceneCanvas(edit);
            Window = new Window { Width = 800, Height = 600, Content = Canvas };
            Window.Show();
            Transform = new ViewportTransform(edit.Document.CanvasWidth, edit.Document.CanvasHeight,
                Canvas.Bounds.Width, Canvas.Bounds.Height);
        }
        public Point View(double x, double y)
        {
            var point = Transform.ToView(new PointD(x, y));
            return new Point(point.X, point.Y);
        }
        public void Dispose() => Window.Close();
    }
}
