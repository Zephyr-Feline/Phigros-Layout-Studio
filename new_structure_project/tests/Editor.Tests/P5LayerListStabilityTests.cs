using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5LayerListStabilityTests
{
    [Test]
    public void DuplicateThenDeleteFromTheLayerListDoesNotRequireRefocusing()
    {
        using var host = new Host();
        host.ClickRow("Layer 03");
        host.Window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(5));
        host.Window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(4));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
    }

    [Test]
    public void UnchangedCanvasSettingsDoNotResetTheLayerScrollPosition()
    {
        using var host = new Host(60);
        host.ClickRow("Layer 59");
        var scroll = host.List.GetVisualDescendants().OfType<ScrollViewer>().Single();
        scroll.Offset = new Vector(0, 500); host.Window.UpdateLayout();
        Assert.That(scroll.Offset.Y, Is.GreaterThan(0)); var before = scroll.Offset;
        host.ClickAction("Apply canvas settings"); host.Window.UpdateLayout();
        Assert.That(scroll.Offset, Is.EqualTo(before));
        Assert.That(host.Edit.UndoCount, Is.Zero);
    }

    [Test]
    public void GeometryOnlyRefreshKeepsTheExistingLayerRowObjects()
    {
        using var host = new Host();
        host.ClickRow("Layer 03");
        var items = host.List.Items.Cast<object>().ToArray();
        host.Edit.UpdateLayer(host.Edit.SelectedLayerId!.Value, layer => layer.Bounds = layer.Bounds with { X = 120 });
        host.ClickAction("Undo");
        var after = host.List.Items.Cast<object>().ToArray();
        for (var index = 0; index < items.Length; index++) Assert.That(after[index], Is.SameAs(items[index]));
        Assert.That(host.Edit.IsDirty, Is.False);
    }

    [Test]
    public void KeyboardDuplicateDeleteUndoRedoAndArrowSelectionKeepWorkingWithoutAClick()
    {
        using var host = new Host(); host.ClickRow("Layer 03");
        host.Key(Key.D, RawInputModifiers.Control); Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(5));
        host.Key(Key.Delete); Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(4));
        Assert.That(host.List.IsKeyboardFocusWithin, Is.True); Assert.That(host.Edit.SelectedLayerIds, Is.Empty);
        host.Key(Key.Z, RawInputModifiers.Control); Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(5));
        host.Key(Key.Y, RawInputModifiers.Control); Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(4));
        host.Key(Key.Down); Assert.That(host.Edit.SelectedLayerIds.Count, Is.EqualTo(1));
        host.Key(Key.Delete); Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(3));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(3));
    }

    [TestCase("Move forward", "02,03,00,01")]
    [TestCase("Move backward", "03,01,02,00")]
    [TestCase("Bring to front", "02,00,03,01")]
    [TestCase("Send to back", "03,01,02,00")]
    public void ReorderPreservesSelectionRowIdentitiesAndKeyboardUndoRedoDelete(string action, string order)
    {
        using var host = new Host(); host.ClickRow("Layer 02"); host.ClickRow("Layer 00", RawInputModifiers.Control);
        var selected = host.Edit.SelectedLayerIds.ToArray(); var original = host.Edit.Document.Copy();
        var items = host.Items(); host.ClickAction(action); host.Window.UpdateLayout();
        Assert.That(host.Labels(), Is.EqualTo(order.Split(',').Select(i => "形状：Layer " + i)));
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selected)); Assert.That(host.List.IsKeyboardFocusWithin, Is.True);
        Assert.That(host.Items(), Is.EquivalentTo(items));
        foreach (var item in items) Assert.That(host.Items().Single(current => current.ToString() == item.ToString()), Is.SameAs(item));
        host.Key(Key.Z, RawInputModifiers.Control); Assert.That(host.Edit.Document.StateEquals(original), Is.True);
        Assert.That(host.Labels(), Is.EqualTo(new[] { "形状：Layer 03", "形状：Layer 02", "形状：Layer 01", "形状：Layer 00" }));
        host.Key(Key.Y, RawInputModifiers.Control); Assert.That(host.Labels(), Is.EqualTo(order.Split(',').Select(i => "形状：Layer " + i)));
        host.Key(Key.Delete); Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(2)); Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
    }

    [TestCase("Show / hide", "[隐藏] 形状：Layer 02")]
    [TestCase("Lock / unlock", "[锁定] 形状：Layer 02")]
    [TestCase("Apply properties", "形状：Renamed layer")]
    public void MetadataChangesUpdateOnlyTheirRowAndPreserveKeyboardFocus(string action, string label)
    {
        using var host = new Host(); host.ClickRow("Layer 02"); var original = host.Items();
        if (action == "Apply properties") host.Field("LayerName").Text = "Renamed layer";
        host.ClickAction(action); host.Window.UpdateLayout();
        Assert.That(host.Labels()[1], Is.EqualTo(label)); Assert.That(host.List.IsKeyboardFocusWithin, Is.True);
        foreach (var index in new[] { 0, 2, 3 }) Assert.That(host.Items()[index], Is.SameAs(original[index]));
        host.Key(Key.Z, RawInputModifiers.Control); Assert.That(host.Labels()[1], Is.EqualTo("形状：Layer 02"));
        host.Key(Key.Delete); Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(3)); Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
    }

    [TestCase("geometry")]
    [TestCase("rename")]
    [TestCase("reorder")]
    public void ShiftSelectionKeepsItsAnchorLayerAcrossRefreshAndRowMoves(string change)
    {
        using var host = new Host(); host.ClickRow("Layer 02"); host.ClickRow("Layer 00", RawInputModifiers.Control);
        var anchorId = host.Edit.Document.Layers[0].Id; var targetId = host.Edit.Document.Layers[1].Id;
        if (change == "reorder") host.ClickAction("Bring to front");
        else
        {
            host.Edit.UpdateLayer(anchorId, layer =>
            {
                if (change == "rename") layer.Name = "Anchor renamed";
                else layer.Bounds = layer.Bounds with { X = 180 };
            });
            host.Refresh();
        }
        var expected = change == "reorder" ? new[] { anchorId, host.Edit.Document.Layers.Single(layer => layer.Name == "Layer 03").Id, targetId }
            : new[] { anchorId, targetId };
        host.ClickRow("Layer 01", RawInputModifiers.Shift);
        Assert.That(host.Edit.SelectedLayerIds, Is.EquivalentTo(expected));
    }

    [Test]
    public void RepeatedUnchangedRefreshesPreserveOrderedSelectionFocusAndRender()
    {
        using var host = new Host(); host.ClickRow("Layer 00"); host.ClickRow("Layer 02", RawInputModifiers.Control);
        var selection = host.Edit.SelectedLayerIds.ToArray(); var items = host.Items();
        var focused = host.Window.FocusManager!.GetFocusedElement(); var document = host.Edit.Document.Copy();
        var png = TestImages.RenderPng(document);
        for (var repeat = 0; repeat < 30; repeat++) host.Refresh();
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That(host.Window.FocusManager.GetFocusedElement(), Is.SameAs(focused));
        for (var index = 0; index < items.Length; index++) Assert.That(host.Items()[index], Is.SameAs(items[index]));
        Assert.That(host.Edit.Document.StateEquals(document), Is.True); Assert.That(host.Edit.IsDirty, Is.False);
        Assert.That(host.Edit.UndoCount, Is.Zero); Assert.That(TestImages.RenderPng(host.Edit.Document), Is.EqualTo(png));
        host.ClickRow("Layer 03", RawInputModifiers.Control);
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selection.Append(document.Layers[3].Id)));
    }

    [TestCase(60, "geometry")]
    [TestCase(200, "geometry")]
    [TestCase(60, "rename")]
    [TestCase(200, "rename")]
    [TestCase(60, "hide")]
    [TestCase(200, "hide")]
    public void RefreshOfAnOffscreenSelectionKeepsTheViewportAndUnchangedRows(int count, string change)
    {
        using var host = new Host(count); host.ClickRow($"Layer {count - 1:00}");
        var scroll = host.List.GetVisualDescendants().OfType<ScrollViewer>().Single();
        scroll.Offset = new Vector(0, 500); host.Window.UpdateLayout(); var offset = scroll.Offset;
        Assert.That(offset.Y, Is.GreaterThan(0)); var items = host.Items();
        host.Edit.UpdateLayer(host.Edit.SelectedLayerId!.Value, layer =>
        {
            if (change == "geometry") layer.Bounds = layer.Bounds with { X = 180 };
            else if (change == "rename") layer.Name = "Offscreen name";
            else layer.Visible = false;
        });
        host.Refresh(); host.Window.UpdateLayout();
        Assert.That(scroll.Offset, Is.EqualTo(offset));
        for (var index = 1; index < items.Length; index++) Assert.That(host.Items()[index], Is.SameAs(items[index]));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
    }

    [TestCase("LayerName")]
    [TestCase("LayerX")]
    public void ListUpdatesDoNotTakeFocusFromPropertyTextFields(string fieldName)
    {
        using var host = new Host(); host.ClickRow("Layer 02");
        var field = host.Field(fieldName); UiTestActions.Reveal(field); host.Window.UpdateLayout(); Assert.That(field.Focus(), Is.True);
        field.Text = fieldName == "LayerName" ? "Input focus" : "220";
        host.ClickAction("Apply properties"); host.Window.UpdateLayout();
        Assert.That(field.IsFocused, Is.True);
        host.Key(Key.D, RawInputModifiers.Control); Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(4));
    }

    [TestCase("marquee")]
    [TestCase("pan")]
    public void ARejectedListSelectionDoesNotEndTheCanvasInteractionOrTakeItsFocus(string mode)
    {
        using var host = new Host(); host.ClickRow("Layer 02");
        var canvas = host.Window.GetVisualDescendants().OfType<SceneCanvas>().Single();
        var logical = new ViewportTransform(1000, 600, canvas.Bounds.Width, canvas.Bounds.Height).ToView(new PointD(10, 10));
        var point = canvas.TranslatePoint(new Point(logical.X, logical.Y), host.Window)!.Value;
        host.Window.MouseDown(point, mode == "pan" ? MouseButton.Middle : MouseButton.Left);
        var selected = host.Edit.SelectedLayerIds.ToArray();
        host.List.SelectedItems!.Add(host.Items()[0]);
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selected)); Assert.That(canvas.IsInteracting, Is.True);
        Assert.That(canvas.IsFocused, Is.True); Assert.That(host.Edit.UndoCount, Is.Zero);
        host.Key(Key.Escape); Assert.That(canvas.IsInteracting, Is.False);
    }

    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    public void ListEditsAndUndoKeepNativeReopenAndPngConsistent(int width, int height)
    {
        using var host = new Host(); host.Field("CanvasWidth").Text = width.ToString(); host.Field("CanvasHeight").Text = height.ToString();
        host.Refresh(); host.Edit.MarkSaved(); host.ClickRow("Layer 02");
        var original = TestImages.RenderPng(host.Edit.Document);
        host.ClickAction("Bring to front"); var rendered = TestImages.RenderPng(host.Edit.Document);
        Assert.That(rendered, Is.Not.EqualTo(original));
        var path = Path.Combine(Path.GetTempPath(), "p5-list-" + Guid.NewGuid() + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, host.Edit.Document, "list-tests");
            Assert.That(TestImages.RenderPng(NativePackageStore.Read(path)), Is.EqualTo(rendered));
            host.Key(Key.Z, RawInputModifiers.Control); Assert.That(TestImages.RenderPng(host.Edit.Document), Is.EqualTo(original));
            Assert.That(host.Edit.IsDirty, Is.False);
        }
        finally { File.Delete(path); }
    }

    [TestCase("New")]
    [TestCase("Open fresh")]
    [TestCase("Open same identities")]
    public void NewAndOpenResetListSelectionScrollAndRangeAnchor(string action)
    {
        var folder = Path.Combine(Path.GetTempPath(), "p5-list-open-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "fresh.ppgproj");
        using var host = new Host(60, _ => Task.FromResult<string?>(path));
        try
        {
            var incoming = action == "Open same identities" ? host.Edit.Document.Copy() : new EditorDocument
            {
                CanvasWidth = 1440, CanvasHeight = 1080,
                Layers = Enumerable.Range(0, 80).Select(i => new Layer { Kind = LayerKind.Shape, Name = $"Fresh {i:00}" }).ToList()
            };
            NativePackageStore.Save(path, incoming, "list-open-test");
            host.ClickRow("Layer 59"); var scroll = host.List.GetVisualDescendants().OfType<ScrollViewer>().Single();
            scroll.Offset = new Vector(0, 500); host.Window.UpdateLayout(); Assert.That(scroll.Offset.Y, Is.GreaterThan(0));
            Assert.That(host.List.Focus(), Is.True);
            if (action == "New")
            {
                host.ClickAction("New"); host.Window.UpdateLayout();
                Assert.That(host.Items(), Is.Empty); Assert.That(host.Edit.SelectedLayerIds, Is.Empty); Assert.That(scroll.Offset.Y, Is.Zero);
            }
            host.ClickAction("Open Project"); host.Window.UpdateLayout();
            Assert.That(host.Edit.Document.StateEquals(incoming), Is.True); Assert.That(host.Edit.SelectedLayerIds, Is.Empty);
            Assert.That(scroll.Offset.Y, Is.Zero); Assert.That(host.List.Selection.AnchorIndex, Is.EqualTo(-1));
            Assert.That(host.List.IsKeyboardFocusWithin, Is.True); Assert.That(host.Edit.IsDirty, Is.False);
            host.Key(Key.Down); Assert.That(host.Edit.SelectedLayerId, Is.EqualTo(incoming.Layers[^1].Id));
        }
        finally { File.Delete(path); Directory.Delete(folder); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedOpenKeepsTheExistingListStateAndKeyboardContext(bool missing)
    {
        var folder = Path.Combine(Path.GetTempPath(), "p5-list-failed-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "invalid.ppgproj"); if (!missing) File.WriteAllBytes(path, [1, 2, 3]);
        using var host = new Host(60, _ => Task.FromResult<string?>(path));
        try
        {
            host.ClickRow("Layer 59"); var items = host.Items(); var selected = host.Edit.SelectedLayerIds.ToArray();
            var document = host.Edit.Document.Copy(); var scroll = host.List.GetVisualDescendants().OfType<ScrollViewer>().Single();
            scroll.Offset = new Vector(0, 500); host.Window.UpdateLayout(); var offset = scroll.Offset; host.List.Focus();
            host.ClickAction("Open Project"); host.Window.UpdateLayout();
            Assert.That(host.Edit.Document.StateEquals(document), Is.True); Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selected));
            for (var index = 0; index < items.Length; index++) Assert.That(host.Items()[index], Is.SameAs(items[index]));
            Assert.That(scroll.Offset, Is.EqualTo(offset)); Assert.That(host.List.IsKeyboardFocusWithin, Is.True);
            Assert.That(host.Edit.UndoCount, Is.Zero); Assert.That(host.Edit.IsDirty, Is.False);
        }
        finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(folder); }
    }

    [Test]
    public void IdenticallyNamedLayersRetainDistinctSelectionAndRowIdentities()
    {
        using var host = new Host(); host.Edit.UpdateDocument(document => { foreach (var layer in document.Layers) layer.Name = "Same name"; });
        host.Refresh(); host.Edit.MarkSaved(); host.List.SelectedIndex = 1; host.List.Focus();
        var originalId = host.Edit.Document.Layers[2].Id; Assert.That(host.Edit.SelectedLayerId, Is.EqualTo(originalId)); var items = host.Items();
        host.Key(Key.D, RawInputModifiers.Control); Assert.That(host.Edit.SelectedLayerId, Is.Not.EqualTo(originalId));
        host.Key(Key.Delete); Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(4));
        for (var index = 0; index < items.Length; index++) Assert.That(host.Items()[index], Is.SameAs(items[index]));
        Assert.That(host.Edit.Document.Layers.Any(layer => layer.Id == originalId), Is.True);
    }

    [Test]
    public void ARejectedDuplicateAtTheLayerLimitLeavesTheListUsableForKeyboardDeletion()
    {
        using var host = new Host(200); host.ClickRow("Layer 199"); var items = host.Items();
        host.Key(Key.D, RawInputModifiers.Control);
        Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(200)); Assert.That(host.Edit.IsDirty, Is.False);
        for (var index = 0; index < items.Length; index++) Assert.That(host.Items()[index], Is.SameAs(items[index]));
        host.Key(Key.Delete); Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(199)); Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
    }

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public ListBox List => Window.GetVisualDescendants().OfType<ListBox>().Single();
        public Host(int count = 4, Func<EditorFileAction, Task<string?>>? picker = null)
        {
            AvaloniaTestHost.EnsureStarted(); Avalonia.Application.Current!.Styles.Add(_theme);
            Edit = new(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
                Layers = Enumerable.Range(0, count).Select(i => new Layer { Kind = LayerKind.Shape,
                    Name = $"Layer {i:00}", Bounds = new(100 + i * 10, 100, 100, 100),
                    ColorArgb = new uint[] { 0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFF9900 }[i % 4] }).ToList() });
            Window = new MainWindow(Edit, filePathPicker: picker); Window.Show();
        }
        public void ClickAction(string text) => UiTestActions.Click(UiTestActions.Find(Window, text));
        public object[] Items() => List.Items.Cast<object>().ToArray();
        public string[] Labels() => Items().Select(item => item.ToString()!).ToArray();
        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>().Single(field => field.Name == name);
        public void Refresh() => ClickAction("Apply canvas settings");
        public void Key(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            Window.KeyPress(key, modifiers, PhysicalKey.None, null); Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
        }
        public void ClickRow(string name, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            var index = Array.FindIndex(Labels(), label => label.EndsWith("：" + name, StringComparison.Ordinal));
            Assert.That(index, Is.GreaterThanOrEqualTo(0), $"Layer '{name}' must have a row before clicking it.");
            List.ScrollIntoView(index); Window.UpdateLayout();
            var row = List.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.Content?.ToString()?.EndsWith("：" + name, StringComparison.Ordinal) == true);
            var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(point, MouseButton.Left, modifiers); Window.MouseUp(point, MouseButton.Left, modifiers);
        }
        public void Dispose() { Edit.MarkSaved(); Window.Close(); Avalonia.Application.Current!.Styles.Remove(_theme); }
    }
}
