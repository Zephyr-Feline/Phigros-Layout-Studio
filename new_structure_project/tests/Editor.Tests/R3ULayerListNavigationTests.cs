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

public sealed class R3ULayerListNavigationTests
{
    [Test]
    public void EdgeScrollPolicyIsBoundedAndOnlyActiveNearTheEdges()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LayerListAutoScroll.NextOffset(100, 500, 155 / 2.0, 155),
                Is.EqualTo(100));
            Assert.That(LayerListAutoScroll.NextOffset(100, 500, 0, 155),
                Is.LessThan(100));
            Assert.That(LayerListAutoScroll.NextOffset(100, 500, 155, 155),
                Is.GreaterThan(100));
            Assert.That(LayerListAutoScroll.NextOffset(0, 500, 0, 155), Is.EqualTo(0));
            Assert.That(LayerListAutoScroll.NextOffset(500, 500, 155, 155),
                Is.EqualTo(500));
            Assert.That(LayerListAutoScroll.NextOffset(100, 500, 10, 0),
                Is.EqualTo(100));
        });
    }

    [Test]
    public void DraggingAtTheListEdgeScrollsWithoutChangingHistoryBeforeRelease()
    {
        using var host = new Host(80);
        var start = host.RowPoint("Layer 79");
        var listTop = host.List.TranslatePoint(default, host.Window)!.Value;
        var bottom = new Point(start.X, listTop.Y + host.List.Bounds.Height - 2);
        host.Window.MouseDown(start, MouseButton.Left);
        for (var step = 0; step < 24; step++)
        {
            var y = bottom.Y - step % 2;
            host.Window.MouseMove(new Point(bottom.X, y), RawInputModifiers.LeftMouseButton);
            host.Settle();
        }
        Assert.That(host.Scroll.Offset.Y, Is.GreaterThan(0));
        Assert.That(host.Edit.UndoCount, Is.Zero);
        host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        host.Window.MouseUp(bottom, MouseButton.Left);
        host.Settle();
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(host.Edit.IsDirty, Is.False);
    }

    [Test]
    public void SelectionSummaryReportsCountsWithoutEditingTheDocument()
    {
        using var host = new Host(4);
        host.Edit.Document.Layers[0].Visible = false;
        host.Edit.Document.Layers[1].Locked = true;
        host.Edit.SetSelection(host.Edit.Document.Layers.Take(3).Select(layer => layer.Id));
        host.Refresh();
        Assert.That(host.Summary.Text, Is.EqualTo("已选 3 · 隐藏 1 · 锁定 1"));

        host.Edit.SetSelection([host.Edit.Document.Layers[2].Id]);
        host.Refresh();
        Assert.That(host.Summary.Text, Is.EqualTo("已选 1"));

        host.Edit.SetSelection([]);
        host.Refresh();
        Assert.That(host.Summary.Text, Is.EqualTo("未选择图层"));
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(host.Edit.IsDirty, Is.False);
    }

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public ListBox List => Window.GetVisualDescendants().OfType<ListBox>()
            .Single(list => list.Name == "LayerList");
        public ScrollViewer Scroll => List.GetVisualDescendants().OfType<ScrollViewer>().Single();
        public TextBlock Summary => Window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(text => text.Name == "LayerSelectionSummary");

        public Host(int count)
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            Edit = new(new EditorDocument
            {
                CanvasWidth = 1000,
                CanvasHeight = 600,
                Layers = Enumerable.Range(0, count).Select(index => new Layer
                {
                    Kind = LayerKind.Shape,
                    Name = $"Layer {index:00}",
                    Bounds = new RectD(100 + index % 10 * 20, 100, 80, 80)
                }).ToList()
            });
            Window = new MainWindow(Edit);
            Window.Show();
            Settle();
        }

        public Point RowPoint(string name)
        {
            var index = List.Items.Cast<object>()
                .Select((item, itemIndex) => (item, itemIndex))
                .Single(candidate => candidate.item.ToString()!.EndsWith("：" + name,
                    StringComparison.Ordinal)).itemIndex;
            List.ScrollIntoView(index);
            Settle();
            var row = List.GetVisualDescendants().OfType<ListBoxItem>().Single(item =>
                item.DataContext?.ToString()?.EndsWith("：" + name, StringComparison.Ordinal) == true);
            return row.TranslatePoint(new Point(12, row.Bounds.Height / 2), Window)!.Value;
        }

        public void Refresh() => UiTestActions.ClickNamed(Window, "ApplyCanvasSettings");

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
