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
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3SLayerListReorderTests
{
    [TestCase(true, "01324")]
    [TestCase(false, "13024")]
    public void MovingMultipleLayersNextToATargetIsOneOrderedUndoableEdit(bool above, string expected)
    {
        var edit = Session();
        var original = edit.Document.Copy();
        edit.SetSelection([edit.Document.Layers[3].Id, edit.Document.Layers[1].Id]);
        var selection = edit.SelectedLayerIds.ToArray();

        var changed = edit.MoveLayersRelativeTo(selection, edit.Document.Layers[0].Id, above);

        Assert.That(changed, Is.True);
        Assert.That(string.Concat(edit.Document.Layers.Select(layer => layer.Name)), Is.EqualTo(expected));
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.StateEquals(original), Is.True);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(string.Concat(edit.Document.Layers.Select(layer => layer.Name)), Is.EqualTo(expected));
    }

    [Test]
    public void SelectedTargetIsANoOpAndKeepsSavepointAndRedo()
    {
        var edit = Session();
        var id = edit.Document.Layers[1].Id;
        edit.UpdateLayer(id, layer => layer.Name = "changed");
        edit.Undo();
        var state = edit.Document.Copy();

        var changed = edit.MoveLayersRelativeTo([id], id, aboveTarget: true);

        Assert.That(changed, Is.False);
        Assert.That(edit.Document.StateEquals(state), Is.True);
        Assert.That(edit.IsDirty, Is.False);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(edit.RedoCount, Is.EqualTo(1));
    }

    [Test]
    public void CtrlArrowReordersTheWholeSelectionAndKeepsLayerListFocus()
    {
        using var host = new Host();
        host.ClickRow("Layer 01");
        host.Key(Key.Up, RawInputModifiers.Control);
        Assert.That(host.Labels(), Is.EqualTo(new[] { "形状：Layer 03", "形状：Layer 01",
            "形状：Layer 02", "形状：Layer 00" }));
        Assert.That(host.List.IsKeyboardFocusWithin, Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));

        host.Key(Key.Z, RawInputModifiers.Control);
        host.Key(Key.Down, RawInputModifiers.Control);
        Assert.That(host.Labels(), Is.EqualTo(new[] { "形状：Layer 03", "形状：Layer 02",
            "形状：Layer 00", "形状：Layer 01" }));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        Assert.That(host.List.IsKeyboardFocusWithin, Is.True);
    }

    [Test]
    public void PointerDragShowsAnInsertionLineAndReordersAsOneUndoableEdit()
    {
        using var host = new Host();
        var original = host.Edit.Document.Copy();
        var start = host.RowPoint("Layer 01");
        var end = host.RowPoint("Layer 03", 0.05);

        host.Window.MouseDown(start, MouseButton.Left);
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        host.Settle();
        Assert.That(host.DropIndicator.IsVisible, Is.True);
        host.Window.MouseUp(end, MouseButton.Left);
        host.Settle();

        Assert.That(host.Labels(), Is.EqualTo(new[] { "形状：Layer 01", "形状：Layer 03",
            "形状：Layer 02", "形状：Layer 00" }));
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(new[] { host.Edit.Document.Layers[3].Id }));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        Assert.That(host.DropIndicator.IsVisible, Is.False);
        host.Key(Key.Z, RawInputModifiers.Control);
        Assert.That(host.Edit.Document.StateEquals(original), Is.True);
    }

    [Test]
    public void DraggingAMultiSelectionKeepsItsRelativeOrder()
    {
        using var host = new Host();
        host.ClickRow("Layer 02");
        host.ClickRow("Layer 00", RawInputModifiers.Control);
        var ids = host.Edit.SelectedLayerIds.ToArray();
        var start = host.RowPoint("Layer 02");
        var end = host.RowPoint("Layer 03", 0.05);

        host.Window.MouseDown(start, MouseButton.Left);
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        host.Window.MouseUp(end, MouseButton.Left);
        host.Settle();

        Assert.That(host.Labels(), Is.EqualTo(new[] { "形状：Layer 02", "形状：Layer 00",
            "形状：Layer 03", "形状：Layer 01" }));
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(ids));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void FilteredListAndEscapeCancelWithoutChangingOrderOrHistory()
    {
        using var host = new Host();
        host.SetFilterVisible(true);
        var original = host.Edit.Document.Copy();
        var start = host.RowPoint("Layer 01");
        var end = host.RowPoint("Layer 03", 0.05);
        host.Window.MouseDown(start, MouseButton.Left);
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        host.Window.MouseUp(end, MouseButton.Left);
        host.Settle();
        Assert.That(host.Edit.Document.StateEquals(original), Is.True);
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(host.Status.Text, Does.Contain("筛选视图"));

        host.SetFilterVisible(false);
        start = host.RowPoint("Layer 01");
        end = host.RowPoint("Layer 03", 0.05);
        host.Window.MouseDown(start, MouseButton.Left);
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        host.Key(Key.Escape);
        Assert.That(host.DropIndicator.IsVisible, Is.False);
        host.Window.MouseUp(end, MouseButton.Left);
        host.Settle();
        Assert.That(host.Edit.Document.StateEquals(original), Is.True);
        Assert.That(host.Edit.UndoCount, Is.Zero);
    }

    private static EditSession Session() => new(new EditorDocument
    {
        CanvasWidth = 1000,
        CanvasHeight = 600,
        Layers = Enumerable.Range(0, 5).Select(i => new Layer
        {
            Kind = LayerKind.Shape,
            Name = i.ToString(),
            Bounds = new RectD(100 + i * 150, 100, 100, 100),
            ColorArgb = 0xFF000000u + (uint)(i * 0x202020)
        }).ToList()
    });

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public ListBox List => Window.GetVisualDescendants().OfType<ListBox>()
            .Single(list => list.Name == "LayerList");
        public Border DropIndicator => Window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Name == "LayerDropIndicator");
        public TextBlock Status => Window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(text => text.Name == "EditorStatus");

        public Host()
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            Edit = new(new EditorDocument
            {
                CanvasWidth = 1000,
                CanvasHeight = 600,
                Layers = Enumerable.Range(0, 4).Select(i => new Layer
                {
                    Kind = LayerKind.Shape,
                    Name = $"Layer {i:00}",
                    Bounds = new RectD(100 + i * 180, 100, 120, 120),
                    ColorArgb = new uint[] { 0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFF9900 }[i]
                }).ToList()
            });
            Window = new MainWindow(Edit);
            Window.Show();
            Window.UpdateLayout();
        }

        public string[] Labels() => List.Items.Cast<object>().Select(item => item.ToString()!).ToArray();

        public Point RowPoint(string name, double fraction = 0.5)
        {
            var index = Array.FindIndex(Labels(), label => label.EndsWith("：" + name, StringComparison.Ordinal));
            Assert.That(index, Is.GreaterThanOrEqualTo(0));
            List.ScrollIntoView(index);
            Window.UpdateLayout();
            var row = List.GetVisualDescendants().OfType<ListBoxItem>().Single(item =>
                item.DataContext?.ToString()?.EndsWith("：" + name, StringComparison.Ordinal) == true);
            return row.TranslatePoint(new Point(12, row.Bounds.Height * fraction), Window)!.Value;
        }

        public void ClickRow(string name, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            var point = RowPoint(name);
            Window.MouseDown(point, MouseButton.Left, modifiers);
            Window.MouseUp(point, MouseButton.Left, modifiers);
            Settle();
        }

        public void SetFilterVisible(bool visible)
        {
            var search = Window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerSearch");
            search.Text = "Layer";
            var filter = Window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "FilterLayerMatches");
            filter.IsChecked = visible;
            Settle();
        }

        public void Key(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            Window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Settle();
        }

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            Edit.MarkSaved();
            Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
