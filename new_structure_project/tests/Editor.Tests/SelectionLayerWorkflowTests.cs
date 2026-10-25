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

public sealed class SelectionLayerWorkflowTests
{
    [TestCase(1920)]
    [TestCase(1440)]
    public void OverlapSelectionPassesThroughHiddenAndLockedLayersAfterListEditsAndUndo(int width)
    {
        var back = Shape("Back", new(300, 280, 600, 360), 0xFFFF0000);
        var front = Shape("Front", back.Bounds, 0xFF00FF00);
        var locked = Shape("Locked overlay", back.Bounds, 0xFF0000FF); locked.Locked = true;
        var hidden = Shape("Hidden overlay", back.Bounds, 0xFFFFFF00); hidden.Visible = false;
        using var host = new Host(width, [back, front, locked, hidden]);
        var original = host.Edit.Document.Copy();
        host.ClickCanvas(600, 460);
        host.AssertSelection(front.Id);
        Assert.That(host.Edit.UndoCount, Is.Zero);

        // The layer list can explicitly select an otherwise unclickable hidden or locked row.
        host.ClickRow("Hidden overlay");
        host.AssertSelection(hidden.Id);
        host.ClickRow("Locked overlay", RawInputModifiers.Control);
        host.AssertSelection(hidden.Id, locked.Id);
        host.Action("Bring to front");
        Assert.That(host.Edit.Document.Layers.Select(layer => layer.Id),
            Is.EqualTo(new[] { back.Id, front.Id, locked.Id, hidden.Id }));
        Assert.That(host.Edit.UndoCount, Is.Zero, "Already-front selection is a no-op.");
        host.Action("Send to back");
        host.AssertSelection(hidden.Id, locked.Id);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        host.ClickCanvas(600, 460);
        host.AssertSelection(front.Id);

        host.Action("Lock / unlock");
        Assert.That(host.Edit.Document.Find(front.Id).Locked, Is.True);
        host.AssertSelection(front.Id);
        host.ClickCanvas(600, 460);
        host.AssertSelection(back.Id);
        host.Action("Show / hide");
        Assert.That(host.Edit.Document.Find(back.Id).Visible, Is.False);
        host.ClickCanvas(600, 460);
        host.AssertSelection();
        Assert.That(host.Edit.UndoCount, Is.EqualTo(3));

        host.Key(Key.Z, RawInputModifiers.Control);
        host.Key(Key.Z, RawInputModifiers.Control);
        host.Key(Key.Z, RawInputModifiers.Control);
        Assert.That(host.Edit.Document.StateEquals(original), Is.True);
        Assert.That(host.Edit.IsDirty, Is.False);
        host.ClickCanvas(600, 460);
        host.AssertSelection(front.Id);
        Assert.That(host.Edit.RedoCount, Is.EqualTo(3), "Changing selection keeps the redo branch.");
        host.AssertNativeRoundTrip();
    }

    [TestCase(1920, true)]
    [TestCase(1440, true)]
    [TestCase(1920, false)]
    [TestCase(1440, false)]
    public void TwoSongCanvasSelectionMovesTheExpectedMembersAndKeepsListHistoryAndReopenInSync(
        int width, bool includeGroups)
    {
        var groupA = Guid.NewGuid(); var groupB = Guid.NewGuid();
        var a = Shape("Song A", new(140, 200, 240, 180), 0xFFCF1313); a.GroupId = groupA;
        var aHidden = Shape("A hidden title", new(400, 200, 180, 120)); aHidden.GroupId = groupA; aHidden.Visible = false;
        var aLocked = Shape("A locked badge", new(580, 200, 100, 180)); aLocked.GroupId = groupA; aLocked.Locked = true;
        var b = Shape("Song B", new(860, 200, 240, 180), 0xFF00AAFF); b.GroupId = groupB;
        var bHidden = Shape("B hidden title", new(1140, 200, 180, 120)); bHidden.GroupId = groupB; bHidden.Visible = false;
        var bLocked = Shape("B locked badge", new(1360, 200, 100, 180)); bLocked.GroupId = groupB; bLocked.Locked = true;
        using var host = new Host(width, [a, aHidden, aLocked, b, bHidden, bLocked]);
        host.Window.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "EditWholeGroup")
            .IsChecked = includeGroups;
        var before = host.Edit.Document.Copy(); var beforePng = TestImages.RenderPng(before);
        host.ClickCanvas(260, 290); host.ClickCanvas(980, 290, RawInputModifiers.Control);
        host.AssertSelection(a.Id, b.Id);
        var selectedOrder = host.Edit.SelectedLayerIds.ToArray();
        host.Window.MouseDown(host.View(260, 290), MouseButton.Left);
        host.Window.MouseMove(host.View(314, 321), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        host.Window.MouseUp(host.View(314, 321), MouseButton.Left, RawInputModifiers.Alt);
        var expectedMoved = includeGroups ? new[] { a.Id, aHidden.Id, b.Id, bHidden.Id } : [a.Id, b.Id];
        foreach (var layer in host.Edit.Document.Layers)
        {
            var initial = before.Find(layer.Id);
            if (expectedMoved.Contains(layer.Id))
            {
                Assert.That(layer.Bounds.X, Is.EqualTo(initial.Bounds.X + 54).Within(1e-7), layer.Name);
                Assert.That(layer.Bounds.Y, Is.EqualTo(initial.Bounds.Y + 31).Within(1e-7), layer.Name);
            }
            else Assert.That(layer.StateEquals(initial), Is.True, layer.Name);
        }
        host.AssertSelection(a.Id, b.Id);
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selectedOrder));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        var after = host.Edit.Document.Copy(); var afterPng = TestImages.RenderPng(after);
        Assert.That(afterPng, Is.Not.EqualTo(beforePng));
        host.Key(Key.Z, RawInputModifiers.Control);
        host.AssertSelection(a.Id, b.Id);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(TestImages.RenderPng(host.Edit.Document), Is.EqualTo(beforePng));
        host.Key(Key.Y, RawInputModifiers.Control);
        host.AssertSelection(a.Id, b.Id);
        Assert.That(host.Edit.Document.StateEquals(after), Is.True);
        Assert.That(TestImages.RenderPng(host.Edit.Document), Is.EqualTo(afterPng));
        host.AssertNativeRoundTrip();
    }

    private static Layer Shape(string name, RectD bounds, uint color = 0xFFFFFFFF) =>
        new() { Kind = LayerKind.Shape, Name = name, Bounds = bounds, ColorArgb = color };

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        private SceneCanvas Canvas => Window.GetVisualDescendants().OfType<SceneCanvas>().Single();
        private ListBox List => Window.GetVisualDescendants().OfType<ListBox>().Single();

        public Host(int width, List<Layer> layers)
        {
            AvaloniaTestHost.EnsureStarted(); Avalonia.Application.Current!.Styles.Add(_theme);
            Edit = new(new EditorDocument { CanvasWidth = width, CanvasHeight = 1080, Layers = layers });
            Window = new MainWindow(Edit); Window.Show();
        }

        public Point View(double x, double y)
        {
            var transform = new ViewportTransform(Edit.Document.CanvasWidth, Edit.Document.CanvasHeight,
                Canvas.Bounds.Width, Canvas.Bounds.Height);
            var point = transform.ToView(new PointD(x, y));
            return Canvas.TranslatePoint(new(point.X, point.Y), Window)!.Value;
        }

        public void ClickCanvas(double x, double y, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            Window.MouseDown(View(x, y), MouseButton.Left, modifiers);
            Window.MouseUp(View(x, y), MouseButton.Left, modifiers);
            Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
        }

        public void ClickRow(string name, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            var index = List.Items.Cast<object>().ToList().FindIndex(item => item.ToString()!.EndsWith("：" + name, StringComparison.Ordinal));
            Assert.That(index, Is.GreaterThanOrEqualTo(0));
            // The list is inside a second ScrollViewer. Selection/focus can queue a later
            // BringIntoView, so settle both viewports before translating the next row.
            UiTestActions.Reveal(List);
            List.ScrollIntoView(index);
            SettleRowLayout();
            var row = List.GetVisualDescendants().OfType<ListBoxItem>().Single(item =>
                item.Content?.ToString()?.EndsWith("：" + name, StringComparison.Ordinal) == true);
            // Use the row label area, away from the inline visibility/lock actions, and
            // finish selection-triggered layout before computing the next row's position.
            var label = row.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Text == row.Content!.ToString());
            Point LabelPoint() => label.TranslatePoint(new Point(label.Bounds.Width / 2,
                label.Bounds.Height / 2), Window)!.Value;
            Window.MouseMove(LabelPoint(), modifiers);
            SettleRowLayout();
            var at = LabelPoint();
            var hit = Window.InputHitTest(at) as Visual;
            var hitRow = hit?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            var offsets = string.Join("; ", row.GetVisualAncestors().OfType<ScrollViewer>()
                .Select(scroll => $"offset={scroll.Offset}, viewport={scroll.Viewport}, extent={scroll.Extent}"));
            Assert.That(hitRow, Is.SameAs(row),
                $"Expected row '{name}' at {at}; row bounds={row.Bounds}; label bounds={label.Bounds}; " +
                $"hit={hit?.GetType().Name}, hit row={hitRow?.Content}; {offsets}");
            Window.MouseDown(at, MouseButton.Left, modifiers); Window.MouseUp(at, MouseButton.Left, modifiers);
            SettleRowLayout();
        }

        private void SettleRowLayout()
        {
            Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
        }

        public void AssertSelection(params Guid[] ids)
        {
            Assert.That(Edit.SelectedLayerIds, Is.EquivalentTo(ids));
            var names = ids.Select(id => Edit.Document.Find(id).Name).ToArray();
            Assert.That(List.SelectedItems!.Cast<object>().Select(item => item.ToString()!.Split('：')[^1]),
                Is.EquivalentTo(names));
        }

        public void Action(string label) { UiTestActions.Click(Window, label); Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); }
        public void Key(Key key, RawInputModifiers modifiers)
        {
            Canvas.Focus(); Window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs(); Window.UpdateLayout();
        }

        public void AssertNativeRoundTrip()
        {
            var path = Path.Combine(Path.GetTempPath(), "selection-workflow-" + Guid.NewGuid() + ".ppgproj");
            try
            {
                NativePackageStore.Save(path, Edit.Document, "selection-workflow-tests");
                var reopened = NativePackageStore.Read(path);
                Assert.That(reopened.StateEquals(Edit.Document), Is.True);
                Assert.That(TestImages.RenderPng(reopened), Is.EqualTo(TestImages.RenderPng(Edit.Document)));
                Assert.That(TestImages.RenderPng(reopened, true), Is.EqualTo(TestImages.RenderPng(Edit.Document, true)));
            }
            finally { File.Delete(path); }
        }

        public void Dispose() { Edit.MarkSaved(); Window.Close(); Avalonia.Application.Current!.Styles.Remove(_theme); }
    }
}
