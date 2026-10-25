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

public sealed class R1LayerManagementTests
{
    [Test]
    public void SearchMatchesNamesAndTextWithoutChangingTheDocumentOrSelection()
    {
        using var host = new Host();
        host.Edit.SetSelection([host.Layers[1].Id, host.Layers[3].Id]);
        host.WindowRefreshed();
        var before = host.Edit.Document.Copy();
        var selection = host.Edit.SelectedLayerIds.ToArray();

        host.Search.Text = "谱师";
        Dispatcher.UIThread.RunJobs();

        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(host.Edit.IsDirty, Is.False);
        Assert.That(host.MatchInfo.Text, Does.Contain("匹配 2 个图层"));
        Assert.That(host.Rows().Count(row => row.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.Background is not null && text.Background != Avalonia.Media.Brushes.Transparent)), Is.EqualTo(2));
    }

    [Test]
    public void SelectAllMatchesUsesListOrderAndIncludesHiddenAndLockedLayers()
    {
        using var host = new Host();
        host.Search.Text = "谱师";
        Dispatcher.UIThread.RunJobs();
        var expected = new[] { host.Layers[4].Id, host.Layers[2].Id };

        UiTestActions.ClickNamed(host.Window, "SelectAllLayerMatches");

        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(expected));
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(UiTestActions.Status(host.Window), Does.Contain("隐藏 1 个").And.Contain("锁定 1 个"));
        Assert.That(host.Search.Text, Is.EqualTo("谱师"));
    }

    [Test]
    public void InlineVisibilityAndLockButtonsLeaveSelectionAndDraftsAlone()
    {
        using var host = new Host();
        host.Edit.SetSelection([host.Layers[1].Id, host.Layers[2].Id]);
        host.WindowRefreshed();
        var name = host.Field("LayerName");
        var canvasWidth = host.Field("CanvasWidth");
        name.Text = "尚未应用的单层名称";
        canvasWidth.Text = "未应用的画布宽度";
        var selection = host.Edit.SelectedLayerIds.ToArray();
        var target = host.Layers[0];
        Assert.That(host.List.Focus(), Is.True);

        host.ClickRowButton("文字：Layer 00", "LayerVisibilityButton");
        Assert.That(host.Edit.Document.Find(target.Id).Visible, Is.False);
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        Assert.That(name.Text, Is.EqualTo("尚未应用的单层名称"));
        Assert.That(canvasWidth.Text, Is.EqualTo("未应用的画布宽度"));
        Assert.That(host.List.IsKeyboardFocusWithin, Is.True);

        host.ClickRowButton("文字：Layer 00", "LayerLockButton");
        Assert.That(host.Edit.Document.Find(target.Id).Locked, Is.True);
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
        Assert.That(name.Text, Is.EqualTo("尚未应用的单层名称"));

        host.ClickRowButton("文字：Layer 00", "LayerLockButton");
        Assert.That(host.Edit.Document.Find(target.Id).Locked, Is.False);
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(3));
    }

    [Test]
    public void SearchInputKeysStayInTheTextBoxAndMatchStateSurvivesRenameDeleteAndUndo()
    {
        using var host = new Host();
        host.Search.Focus();
        host.Search.Text = "谱师";
        Dispatcher.UIThread.RunJobs();
        var selection = host.Edit.SelectedLayerIds.ToArray();
        host.Window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.None, null);
        host.Window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(5));
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selection));
        host.Search.Text = "谱师";
        Dispatcher.UIThread.RunJobs();

        host.Edit.UpdateLayer(host.Layers[2].Id, layer => layer.Text = "普通文字");
        host.WindowRefreshed();
        Assert.That(host.MatchInfo.Text, Does.Contain("匹配 1 个图层"));
        host.Edit.DeleteLayer(host.Layers[4].Id);
        host.WindowRefreshed();
        Assert.That(host.MatchInfo.Text, Is.EqualTo("没有匹配项。"));
        host.Edit.Undo();
        host.Edit.Undo();
        host.WindowRefreshed();
        Assert.That(host.MatchInfo.Text, Does.Contain("匹配 2 个图层"));
        Assert.That(host.Search.Text, Is.EqualTo("谱师"));
    }

    [Test]
    public void LocateStepsThroughTwoHundredLayersAndKeepsTheQuery()
    {
        using var host = new Host(200);
        host.Search.Text = "Layer 19";
        Dispatcher.UIThread.RunJobs();
        Assert.That(host.MatchInfo.Text, Does.Contain("匹配 11 个图层"));
        UiTestActions.ClickNamed(host.Window, "NextLayerMatch");
        var first = host.Edit.SelectedLayerId;
        Assert.That(first, Is.Not.Null);
        UiTestActions.ClickNamed(host.Window, "NextLayerMatch");
        Assert.That(host.Edit.SelectedLayerId, Is.Not.EqualTo(first));
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(host.Search.Text, Is.EqualTo("Layer 19"));
    }

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public Layer[] Layers { get; }
        public ListBox List => Window.GetVisualDescendants().OfType<ListBox>().Single();
        public TextBox Search => Field("LayerSearch");
        public TextBlock MatchInfo => Window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(block => block.Name == "LayerMatchInfo");

        public Host(int count = 5)
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            Layers = Enumerable.Range(0, count).Select(index => index % 2 == 0
                ? new Layer
                {
                    Kind = LayerKind.Text, Name = $"Layer {index:00}",
                    Text = index == 2 ? "谱师：测试" : index == 4 ? "谱师：另一位" : $"正文 {index}",
                    Bounds = new RectD(40, 40 + index * 2, 180, 60)
                }
                : new Layer
                {
                    Kind = LayerKind.Shape, Name = $"Layer {index:00}",
                    Bounds = new RectD(40, 40 + index * 2, 180, 60)
                }).ToArray();
            if (count >= 5)
            {
                Layers[4].Visible = false;
                Layers[2].Locked = true;
            }
            Edit = new EditSession(new EditorDocument
            {
                CanvasWidth = 1000, CanvasHeight = 600, Layers = Layers.ToList()
            });
            Window = new MainWindow(Edit);
            Window.Show();
        }

        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>()
            .Single(field => field.Name == name);
        public void WindowRefreshed()
        {
            // A no-op canvas application refreshes the list without changing the document.
            UiTestActions.ClickNamed(Window, "ApplyCanvasSettings");
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }
        public List<ListBoxItem> Rows()
        {
            List.ScrollIntoView(0);
            Window.UpdateLayout();
            return List.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        }
        public void ClickRowButton(string label, string buttonName)
        {
            var index = List.Items.Cast<object>().Select(item => item.ToString()!)
                .ToList().FindIndex(text => text.EndsWith(label, StringComparison.Ordinal));
            Assert.That(index, Is.GreaterThanOrEqualTo(0), label);
            List.ScrollIntoView(index);
            Window.UpdateLayout();
            var row = List.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(item => item.Content?.ToString()?.EndsWith(label, StringComparison.Ordinal) == true);
            var button = row.GetVisualDescendants().OfType<Button>().Single(item => item.Name == buttonName);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
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
