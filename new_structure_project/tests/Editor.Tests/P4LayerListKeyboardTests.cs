using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4LayerListKeyboardTests
{
    [Test]
    public void ControlSelectionCanMoveAndDuplicateTwoLayersWithOneUndoStepEach()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var layers = CreateLayers();
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = layers.ToList() });
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single();
            var rows = Rows(list);
            Click(window, rows[0]);
            Click(window, rows[2], RawInputModifiers.Control);
            Assert.That(edit.SelectedLayerIds, Is.EquivalentTo(new[] { layers[3].Id, layers[1].Id }));

            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var transform = new ViewportTransform(1000, 600, canvas.Bounds.Width, canvas.Bounds.Height);
            Point View(double x, double y)
            {
                var p = transform.ToView(new PointD(x, y));
                return canvas.TranslatePoint(new Point(p.X, p.Y), window)
                    ?? throw new AssertionException("The canvas is not laid out.");
            }
            window.MouseDown(View(750, 150), MouseButton.Left);
            window.MouseMove(View(780, 150), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            window.MouseUp(View(780, 150), MouseButton.Left);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(layers[3].Id).Bounds.X, Is.EqualTo(730));
                Assert.That(edit.Document.Find(layers[1].Id).Bounds.X, Is.EqualTo(330));
                Assert.That(edit.Document.Find(layers[2].Id).Bounds.X, Is.EqualTo(500));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });

            window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(6));
            Assert.That(edit.SelectedLayerIds.Count, Is.EqualTo(2));
            Assert.That(edit.UndoCount, Is.EqualTo(2));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(4));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(layers[3].Id).Bounds.X, Is.EqualTo(700));
            Assert.That(edit.Document.Find(layers[1].Id).Bounds.X, Is.EqualTo(300));
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void ShiftRangeGroupsOnlyTheListSelectionAndTextFocusDoesNotDuplicate()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var layers = CreateLayers();
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = layers.ToList() });
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var rows = Rows(window.GetVisualDescendants().OfType<ListBox>().Single());
            Click(window, rows[0]);
            Click(window, rows[2], RawInputModifiers.Shift);
            Assert.That(edit.SelectedLayerIds,
                Is.EquivalentTo(new[] { layers[3].Id, layers[2].Id, layers[1].Id }));
            Assert.That(edit.SelectedLayerIds, Does.Not.Contain(layers[0].Id));

            UiTestActions.Click(UiTestActions.Find(window, "Group selected"));
            var group = edit.Document.Find(layers[3].Id).GroupId;
            Assert.That(group, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(layers[2].Id).GroupId, Is.EqualTo(group));
                Assert.That(edit.Document.Find(layers[1].Id).GroupId, Is.EqualTo(group));
                Assert.That(edit.Document.Find(layers[0].Id).GroupId, Is.Null);
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });

            rows[0].Focus();
            window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(7));
            Assert.That(edit.SelectedLayerIds.Count, Is.EqualTo(3));
            Assert.That(edit.UndoCount, Is.EqualTo(2));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(4));

            var field = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerName");
            UiTestActions.Focus(field);
            window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(4));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void DuplicateShortcutAtLayerLimitReportsFailureWithoutChangingTheDocument()
    {
        AvaloniaTestHost.EnsureStarted();
        var layers = Enumerable.Range(0, 200).Select(index => new Layer
        {
            Kind = LayerKind.Shape, Name = "Layer " + index,
            Bounds = new RectD(10 + index, 10, 20, 20)
        }).ToList();
        var edit = new EditSession(new EditorDocument { Layers = layers });
        edit.SelectedLayerId = layers[0].Id;
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            window.GetVisualDescendants().OfType<SceneCanvas>().Single().Focus();
            window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Layers.Count, Is.EqualTo(200));
                Assert.That(edit.UndoCount, Is.Zero);
                Assert.That(edit.IsDirty, Is.False);
                Assert.That(window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(block => block.Text?.StartsWith("错误：", StringComparison.Ordinal) == true), Is.True);
            });
        }
        finally { window.Close(); }
    }

    [Test]
    public void DuplicateShortcutDuringDragDoesNotInterruptTheGesture()
    {
        AvaloniaTestHost.EnsureStarted();
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(100, 100, 100, 100) };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [layer] });
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var transform = new ViewportTransform(1000, 600, canvas.Bounds.Width, canvas.Bounds.Height);
            Point View(double x, double y)
            {
                var p = transform.ToView(new PointD(x, y));
                return canvas.TranslatePoint(new Point(p.X, p.Y), window)
                    ?? throw new AssertionException("The canvas is not laid out.");
            }
            window.MouseDown(View(150, 150), MouseButton.Left);
            window.MouseMove(View(180, 150), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(1));
            window.MouseUp(View(180, 150), MouseButton.Left);
            Assert.That(edit.Document.Find(layer.Id).Bounds.X, Is.EqualTo(130).Within(1e-6));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
        }
    }

    [Test]
    public void DeleteFromLayerListRemovesTheSelectionAndTextFieldsKeepTheirOwnDeleteKey()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var layers = CreateLayers();
        var edit = new EditSession(new EditorDocument { Layers = layers.ToList() });
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var rows = Rows(window.GetVisualDescendants().OfType<ListBox>().Single());
            Click(window, rows[0]);
            Click(window, rows[2], RawInputModifiers.Control);
            rows[0].Focus();
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Layers.Select(layer => layer.Id),
                    Is.EquivalentTo(new[] { layers[0].Id, layers[2].Id }));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(4));

            window.GetLogicalDescendants().OfType<TextBox>()
                .Single(field => field.Name == "LayerName").Focus();
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(4));
            Assert.That(edit.UndoCount, Is.Zero);
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static Layer[] CreateLayers() => Enumerable.Range(0, 4).Select(index => new Layer
    {
        Kind = LayerKind.Shape, Name = "Layer " + index,
        Bounds = new RectD(100 + 200 * index, 100, 100, 100)
    }).ToArray();

    private static ListBoxItem[] Rows(ListBox list)
    {
        var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        Assert.That(rows.Length, Is.EqualTo(4));
        return rows;
    }

    private static void Click(Window window, ListBoxItem row,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)
            ?? throw new AssertionException("The layer row is not laid out.");
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
    }
}
