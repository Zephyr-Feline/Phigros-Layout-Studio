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

[NonParallelizable]
public sealed class ReviewHistoryFocusFeedbackTests
{
    [TestCase("LayerVisibilityButton", "显示或隐藏图层")]
    [TestCase("LayerLockButton", "锁定或解锁图层")]
    public void FirstInlineEditEnablesHistoryFeedbackWithoutApplyingDrafts(string button, string label)
    {
        using var host = new Host();
        host.SetDrafts();
        var drafts = host.CaptureDrafts();
        Assert.That(host.UndoButton.IsEnabled, Is.False);
        Assert.That(host.UndoMenu.IsEnabled, Is.False);

        host.ClickTextRowButton(button);

        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        Assert.That(host.Edit.IsDirty, Is.True);
        host.AssertFeedback(label, null);
        host.AssertDrafts(drafts);
        Assert.That(host.Edit.Document.Find(host.TextId).Name, Is.EqualTo(Host.TextName));
        Assert.That(host.Edit.Document.Find(host.TextId).Text, Is.EqualTo(Host.AppliedText));
    }

    [Test]
    public void InlineUnlockRetainsItsHistoryLabelThroughUndoAndRedo()
    {
        using var host = new Host();
        host.ClickTextRowButton("LayerLockButton");
        Assert.That(host.Edit.Document.Find(host.TextId).Locked, Is.True);
        host.ClickTextRowButton("LayerLockButton");

        Assert.That(host.Edit.Document.Find(host.TextId).Locked, Is.False);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
        Assert.That(host.Edit.UndoLabel, Is.EqualTo("锁定或解锁图层"),
            "unlock must not be classified from the wording of a visibility status message");
        host.AssertFeedback("锁定或解锁图层", null);

        host.Action("Undo");
        Assert.That(host.Edit.Document.Find(host.TextId).Locked, Is.True);
        Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
        host.AssertFeedback("锁定或解锁图层", "锁定或解锁图层");

        host.Action("Redo");
        Assert.That(host.Edit.Document.Find(host.TextId).Locked, Is.False);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
        host.AssertFeedback("锁定或解锁图层", null);
    }

    [Test]
    public void InlineEditImmediatelyClearsTheDisplayedRedoBranchAndPreservesDrafts()
    {
        using var host = new Host(withRedo: true);
        host.SetDrafts();
        var drafts = host.CaptureDrafts();
        host.AssertFeedback(null, "重命名图层");

        host.ClickTextRowButton("LayerVisibilityButton");

        Assert.That(host.Edit.RedoCount, Is.Zero);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        host.AssertFeedback("显示或隐藏图层", null);
        host.AssertDrafts(drafts);
    }

    [Test]
    public void ConfirmedTextRefreshesHistoryWhileKeepingUnrelatedPropertyAndCanvasDrafts()
    {
        using var host = new Host();
        host.SetDrafts();
        var drafts = host.CaptureDrafts();
        var before = host.Edit.Document.Copy();
        const string confirmed = "对话框确认的正文\n第二行";
        host.Action("EditSelectedText");
        var dialog = host.Window.OwnedWindows.OfType<TextEditDialog>().Single();
        dialog.GetLogicalDescendants().OfType<TextBox>()
            .Single(field => field.Name == "TextEditValue").Text = confirmed;

        UiTestActions.ClickNamed(dialog, "ConfirmTextEdit");
        host.Pump();

        Assert.That(host.Window.OwnedWindows, Is.Empty);
        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        Assert.That(host.Edit.Document.Find(host.TextId).Text, Is.EqualTo(confirmed));
        Assert.That(host.Field("TextContent").Text, Is.EqualTo(confirmed));
        host.AssertDrafts(drafts, includeText: false);
        host.AssertFeedback("编辑文字", null);

        host.Action("Undo");
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        host.AssertFeedback(null, "编辑文字");
    }

    [TestCase(false, "切换自动背景")]
    [TestCase(true, "应用背景混合设置")]
    public void BackgroundLocalRefreshUpdatesHistoryWithoutDiscardingUnrelatedDrafts(bool applyBlend,
        string label)
    {
        using var host = new Host();
        host.SetDrafts();
        var drafts = host.CaptureDrafts(includeBlend: !applyBlend);
        var before = host.Edit.Document.Copy();

        if (applyBlend) host.Action("ApplyBlendSettings");
        else host.Automatic.IsChecked = !host.Automatic.IsChecked;
        host.Pump();

        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        Assert.That(host.Edit.Document.StateEquals(before), Is.False);
        Assert.That(host.Edit.Document.Find(host.TextId).Text, Is.EqualTo(Host.AppliedText));
        Assert.That(host.Edit.Document.Find(host.TextId).Name, Is.EqualTo(Host.TextName));
        host.AssertDrafts(drafts);
        host.AssertFeedback(label, null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void F6RestoresTheLastPropertyFieldAfterAMouseFocusChangeWithoutApplyingDrafts(bool collapse)
    {
        using var host = new Host();
        host.SetDrafts();
        var drafts = host.CaptureDrafts();
        var before = host.Edit.Document.Copy();
        var field = host.Field("LayerX");
        Assert.That(UiTestActions.Focus(field), Is.True);
        host.MouseFocusCanvas();
        if (collapse)
        {
            host.Window.GetLogicalDescendants().OfType<Expander>()
                .Single(expander => expander.Name == "LayerProperties").IsExpanded = false;
            host.Pump();
        }

        host.PressF6();
        Assert.That(host.Window.FocusManager!.GetFocusedElement(), Is.SameAs(host.List));
        host.PressF6();

        Assert.That(host.Window.FocusManager.GetFocusedElement(), Is.SameAs(field),
            "the last field must be remembered even when the mouse, rather than F6, left it");
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.UndoCount, Is.Zero);
        host.AssertDrafts(drafts);
    }

    [Test]
    public void F6FallsBackWhenTheRememberedFieldIsUnavailableForTheNewLayerKind()
    {
        using var host = new Host();
        var fontSize = host.Field("LayerFontSize");
        Assert.That(UiTestActions.Focus(fontSize), Is.True);
        host.PressF6();
        host.SelectShapeRow();
        Assert.That(host.Edit.SelectedLayerId, Is.EqualTo(host.ShapeId));
        Assert.That(fontSize.GetVisualAncestors().OfType<Control>()
            .Any(ancestor => !ancestor.IsVisible), Is.True,
            "font size is inside the text-only property section");
        host.MouseFocusCanvas();

        host.PressF6();
        host.PressF6();

        Assert.That(host.Window.FocusManager!.GetFocusedElement(),
            Is.SameAs(host.Field("LayerName")),
            "a hidden text-only field cannot be restored for a shape layer");
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(host.Edit.IsDirty, Is.False);
    }

    private sealed record Drafts(Dictionary<string, string?> Fields, int Caret, int Start, int End,
        Guid[] Selection);

    private sealed class Host : IDisposable
    {
        public const string TextName = "历史核验文字";
        public const string AppliedText = "已应用正文";
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public Guid TextId { get; }
        public Guid ShapeId { get; }
        public SceneCanvas Canvas => Window.GetVisualDescendants().OfType<SceneCanvas>().Single();
        public ListBox List => Window.GetLogicalDescendants().OfType<ListBox>()
            .Single(list => list.Name == "LayerList");
        public Button UndoButton => Window.GetLogicalDescendants().OfType<Button>()
            .Single(button => button.Name == "Undo");
        public MenuItem UndoMenu => Menu("Undo");
        public CheckBox Automatic => Window.GetLogicalDescendants().OfType<CheckBox>()
            .Single(box => box.Name == "AutomaticBackground");

        public Host(bool withRedo = false)
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            var text = new Layer
            {
                Kind = LayerKind.Text, Name = TextName, Text = AppliedText,
                Bounds = new RectD(60, 50, 240, 90), FontSize = 42
            };
            var shape = new Layer
            {
                Kind = LayerKind.Shape, Name = "历史核验形状", Bounds = new RectD(360, 180, 160, 100)
            };
            TextId = text.Id;
            ShapeId = shape.Id;
            Edit = new EditSession(new EditorDocument
            {
                CanvasWidth = 640, CanvasHeight = 360,
                BackgroundBlend = new(false, 6, 0.123456789), Layers = [shape, text]
            });
            if (withRedo)
            {
                Edit.UpdateLayer(TextId, layer => layer.Name = "只存在于重做分支的名称", "重命名图层");
                Edit.Undo();
            }
            Edit.SelectedLayerId = TextId;
            Window = new MainWindow(Edit);
            Window.Show();
            Pump();
        }

        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>()
            .Single(field => field.Name == name);

        private MenuItem Menu(string name) => Window.GetLogicalDescendants().OfType<MenuItem>()
            .Single(menu => menu.Name == name);

        public void Action(string name)
        {
            UiTestActions.ClickNamed(Window, name);
            Pump();
        }

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public void SetDrafts()
        {
            Assert.That(UiTestActions.Focus(Field("LayerName")), Is.True);
            Field("LayerName").Text = "尚未应用的名称";
            Field("LayerFontSize").Text = "NaN";
            Field("LayerX").Text = "123.45678912345678";
            Field("CanvasWidth").Text = "未应用的非法画布宽度";
            Field("BackgroundColor").Text = "未应用的颜色";
            Field("BackgroundBlur").Text = "12";
            Field("BackgroundDarkness").Text = "65.12345678912345";
            Field("BackgroundRange").Text = "1.3";
            Field("BackgroundTransition").Text = "0.7";
            var text = Field("TextContent");
            text.Text = "尚未应用的文字草稿第一行\n第二行完整保留";
            text.CaretIndex = text.Text.Length;
            text.SelectionStart = text.Text.Length - 5;
            text.SelectionEnd = text.Text.Length - 1;
        }

        public Drafts CaptureDrafts(bool includeBlend = true)
        {
            string[] names = ["LayerName", "LayerFontSize", "LayerX", "CanvasWidth",
                "BackgroundColor", "TextContent"];
            if (includeBlend)
                names = [.. names, "BackgroundBlur", "BackgroundDarkness", "BackgroundRange",
                    "BackgroundTransition"];
            var text = Field("TextContent");
            return new(names.ToDictionary(name => name, name => Field(name).Text),
                text.CaretIndex, text.SelectionStart, text.SelectionEnd, Edit.SelectedLayerIds.ToArray());
        }

        public void AssertDrafts(Drafts drafts, bool includeText = true)
        {
            Assert.Multiple(() =>
            {
                foreach (var (name, value) in drafts.Fields)
                    if (includeText || name != "TextContent")
                        Assert.That(Field(name).Text, Is.EqualTo(value), "Unapplied field " + name);
                if (includeText)
                {
                    var text = Field("TextContent");
                    Assert.That(text.CaretIndex, Is.EqualTo(drafts.Caret));
                    Assert.That(text.SelectionStart, Is.EqualTo(drafts.Start));
                    Assert.That(text.SelectionEnd, Is.EqualTo(drafts.End));
                }
                Assert.That(Edit.SelectedLayerIds, Is.EqualTo(drafts.Selection));
            });
        }

        public void AssertFeedback(string? undo, string? redo)
        {
            var redoMenu = Menu("Redo");
            var summary = Window.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "HistorySummary").Text ?? "";
            var sections = summary.Split("\n\n", StringSplitOptions.None);
            Assert.Multiple(() =>
            {
                Assert.That(Edit.UndoLabel, Is.EqualTo(undo));
                Assert.That(Edit.RedoLabel, Is.EqualTo(redo));
                Assert.That(UndoMenu.IsEnabled, Is.EqualTo(undo is not null));
                Assert.That(UndoButton.IsEnabled, Is.EqualTo(undo is not null));
                Assert.That(redoMenu.IsEnabled, Is.EqualTo(redo is not null));
                Assert.That(UndoMenu.Header?.ToString(), Is.EqualTo(undo is null ? "撤销" : "撤销：" + undo));
                Assert.That(redoMenu.Header?.ToString(), Is.EqualTo(redo is null ? "重做" : "重做：" + redo));
                Assert.That(ToolTip.GetTip(UndoButton)?.ToString(),
                    Does.StartWith(undo is null ? "没有可撤销" : "撤销：" + undo));
                Assert.That(ToolTip.GetTip(UndoMenu)?.ToString(),
                    Does.StartWith(undo is null ? "没有可撤销" : "撤销：" + undo));
                Assert.That(ToolTip.GetTip(redoMenu)?.ToString(),
                    Does.StartWith(redo is null ? "没有可重做" : "重做：" + redo));
                Assert.That(sections, Has.Length.EqualTo(2));
                Assert.That(sections[0], undo is null
                    ? Is.EqualTo("可撤销：无") : Does.StartWith("可撤销：\n1. " + undo));
                Assert.That(sections[1], redo is null
                    ? Is.EqualTo("可重做：无") : Does.StartWith("可重做：\n1. " + redo));
            });
        }

        public void ClickTextRowButton(string name)
        {
            var row = Row(TextName);
            var button = row.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
            // Draft focus can leave the outer sidebar scrolled during layout. Exercise the real
            // row button's routed action directly; hit-position behavior has separate coverage.
            UiTestActions.Click(button);
            Pump();
        }

        public void SelectShapeRow() => MouseClick(Row("历史核验形状"), MouseButton.Left);

        private ListBoxItem Row(string name)
        {
            UiTestActions.Reveal(List);
            var index = List.Items.Cast<object>().Select(item => item.ToString()!)
                .ToList().FindIndex(label => label.EndsWith(name, StringComparison.Ordinal));
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "expected layer row " + name);
            List.ScrollIntoView(index);
            Pump();
            return List.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(row => row.Content?.ToString()?.EndsWith(name, StringComparison.Ordinal) == true);
        }

        public void MouseFocusCanvas()
        {
            // Middle click changes actual pointer focus without changing selection or document state.
            MouseClick(Canvas, MouseButton.Middle);
            Assert.That(Window.FocusManager!.GetFocusedElement(), Is.SameAs(Canvas));
            Assert.That(Canvas.IsInteracting, Is.False);
        }

        private void MouseClick(Control control, MouseButton button)
        {
            var point = control.TranslatePoint(new Point(control.Bounds.Width / 2,
                control.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(point, button);
            Window.MouseUp(point, button);
            Pump();
        }

        public void PressF6()
        {
            Window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.None, null);
            Pump();
        }

        public void Dispose()
        {
            foreach (var dialog in Window.OwnedWindows.ToArray()) dialog.Close();
            Pump();
            Edit.MarkSaved();
            Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
