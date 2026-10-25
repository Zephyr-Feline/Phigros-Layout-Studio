using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class LayerActionDraftWorkflowTests
{
    [TestCase("MoveForward")]
    [TestCase("MoveBackward")]
    [TestCase("BringToFront")]
    [TestCase("SendToBack")]
    [TestCase("Ungroup")]
    [TestCase("HideGroup")]
    [TestCase("ShowGroup")]
    public void AnUnchangedLayerMenuActionKeepsAllInputsSelectionAndRedo(string action)
    {
        using var host = new Host(action, unchanged: true);
        host.SetDrafts();
        var inputs = host.CaptureInputs();
        var before = host.Edit.Document.Copy();
        var selection = host.Edit.SelectedLayerIds.ToArray();
        var history = (host.Edit.UndoCount, host.Edit.RedoCount, host.Edit.IsDirty);
        var title = host.Window.Title;
        var png = TestImages.RenderPng(before, true, 320);

        host.Action(action);

        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That((host.Edit.UndoCount, host.Edit.RedoCount, host.Edit.IsDirty), Is.EqualTo(history));
        Assert.That(host.Window.Title, Is.EqualTo(title));
        Assert.That(TestImages.RenderPng(host.Edit.Document, true, 320), Is.EqualTo(png));
        host.AssertInputs(inputs);
    }

    [TestCase("MoveForward")]
    [TestCase("MoveBackward")]
    [TestCase("BringToFront")]
    [TestCase("SendToBack")]
    [TestCase("Ungroup")]
    [TestCase("HideGroup")]
    [TestCase("ShowGroup")]
    public void AnEffectiveLayerMenuActionRefreshesWithoutApplyingDraftsAndRemainsUndoable(string action)
    {
        using var host = new Host(action, unchanged: false);
        host.SetDrafts();
        var before = host.Edit.Document.Copy();
        var expected = before.Copy();
        if (action == "Ungroup")
        {
            expected.Find(host.Id).GroupId = null;
            expected.Find(host.TopId).GroupId = null;
        }
        else if (action is "HideGroup" or "ShowGroup")
        {
            expected.Find(host.Id).Visible = action == "ShowGroup";
            expected.Find(host.TopId).Visible = action == "ShowGroup";
        }
        else
        {
            var ids = action is "MoveForward" or "BringToFront"
                ? new[] { host.BottomId, host.TopId, host.Id }
                : new[] { host.Id, host.BottomId, host.TopId };
            expected.Layers = ids.Select(expected.Find).ToList();
        }
        var selection = host.Edit.SelectedLayerIds.ToArray();
        var undo = host.Edit.UndoCount;

        host.Action(action);

        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.StateEquals(expected), Is.True);
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo + 1));
        Assert.That(host.Edit.RedoCount, Is.Zero);
        Assert.That(host.Edit.IsDirty, Is.True);
        Assert.That(host.Window.Title, Does.Contain("*"));
        host.AssertAppliedFields();
        host.AssertListOrder();
        host.Action("Undo");
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.IsDirty, Is.False);
        host.Action("Redo");
        Assert.That(host.Edit.Document.StateEquals(expected), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AFullDirtyHistoryStillDistinguishesAnEffectiveOrderChangeFromANoOp(bool unchanged)
    {
        using var host = new Host("BringToFront", unchanged);
        host.Field("LayerName").Text = "历史达到上限后的名称";
        host.Action("ApplyLayerProperties");
        Assert.That(host.Edit.UndoCount, Is.EqualTo(host.Edit.MaxHistoryEntries));
        Assert.That(host.Edit.IsDirty, Is.True);
        host.SetDrafts();
        var inputs = host.CaptureInputs();
        var before = host.Edit.Document.Copy();
        var undo = host.Edit.UndoCount;

        host.Action("BringToFront");

        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo), "Both no-op and effective changes retain the full history count.");
        Assert.That(host.Edit.IsDirty, Is.True);
        if (unchanged)
        {
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
            host.AssertInputs(inputs);
        }
        else
        {
            Assert.That(host.Edit.Document.Layers.Select(layer => layer.Id),
                Is.EqualTo(new[] { host.BottomId, host.TopId, host.Id }));
            host.AssertAppliedFields();
            host.AssertListOrder();
            host.Action("Undo");
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        }
    }

    private sealed record Inputs(Dictionary<string, string?> Fields, int Caret, int Start, int End,
        Dictionary<string, bool?> Checks, Dictionary<string, object?> Choices);

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public Guid BottomId { get; }
        public Guid Id { get; }
        public Guid TopId { get; }

        public Host(string action, bool unchanged)
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            var bottom = new Layer { Kind = LayerKind.Text, Name = "底层", Text = "底层文字", Bounds = new(30, 30, 220, 70) };
            var primary = new Layer { Kind = LayerKind.Text, Name = "主层", Text = "主层已应用文字", Bounds = new(100, 100, 240, 80) };
            var top = new Layer { Kind = LayerKind.Text, Name = "顶层", Text = "锁定组成员", Locked = true, Bounds = new(250, 200, 220, 70) };
            var group = Guid.NewGuid();
            if (action != "Ungroup" || !unchanged) primary.GroupId = top.GroupId = group;
            if (action == "HideGroup") { primary.Visible = !unchanged; top.Visible = false; }
            if (action == "ShowGroup") primary.Visible = unchanged;
            BottomId = bottom.Id; Id = primary.Id; TopId = top.Id;
            Edit = new EditSession(new EditorDocument { CanvasWidth = 640, CanvasHeight = 360,
                Layers = [bottom, primary, top] }, maxHistoryEntries: 2);
            Edit.UpdateLayer(Id, layer => layer.Name = "已应用的主层名称");
            Edit.UpdateLayer(Id, layer => layer.ColorArgb = 0xFFCF1313);
            Edit.Undo(); Edit.MarkSaved();
            Edit.SetSelection(unchanged && action is "MoveForward" or "BringToFront" ? [Id, TopId]
                : unchanged && action is "MoveBackward" or "SendToBack" ? [Id, BottomId] : [Id]);
            Window = new MainWindow(Edit); Window.Show();
        }

        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>().Single(f => f.Name == name);
        public void Action(string name)
        {
            UiTestActions.ClickNamed(Window, name);
            Dispatcher.UIThread.RunJobs();
        }

        public void SetDrafts()
        {
            Assert.That(UiTestActions.Focus(Field("LayerName")), Is.True);
            Field("LayerName").Text = "尚未应用的名称";
            Field("LayerFontSize").Text = "NaN";
            Field("LayerWidth").Text = "321.123456789";
            Field("CanvasWidth").Text = "尚未应用的非法画布宽度";
            Field("CanvasHeight").Text = "700";
            Field("BackgroundColor").Text = "尚未应用的颜色";
            Field("BackgroundBlur").Text = "12";
            Field("BackgroundDarkness").Text = "65.123456789";
            Window.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "LayerBold").IsChecked = true;
            Window.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "LayerAlignment")
                .SelectedItem = Editor.Core.HorizontalAlignment.Center;
            var text = Field("TextContent");
            text.Text = "尚未应用的第一行文字\n第二行保留光标选区";
            text.CaretIndex = text.Text.Length; text.SelectionStart = 3; text.SelectionEnd = 8;
        }

        public Inputs CaptureInputs()
        {
            var controls = Window.GetLogicalDescendants().ToArray();
            var text = Field("TextContent");
            return new(controls.OfType<TextBox>().Where(c => c.Name is not null).ToDictionary(c => c.Name!, c => c.Text),
                text.CaretIndex, text.SelectionStart, text.SelectionEnd,
                controls.OfType<CheckBox>().Where(c => c.Name is not null).ToDictionary(c => c.Name!, c => c.IsChecked),
                controls.OfType<ComboBox>().Where(c => c.Name is not null).ToDictionary(c => c.Name!, c => c.SelectedItem));
        }

        public void AssertInputs(Inputs inputs)
        {
            Assert.Multiple(() =>
            {
                foreach (var (name, value) in inputs.Fields) Assert.That(Field(name).Text, Is.EqualTo(value), name);
                var text = Field("TextContent");
                Assert.That((text.CaretIndex, text.SelectionStart, text.SelectionEnd), Is.EqualTo((inputs.Caret, inputs.Start, inputs.End)));
                foreach (var (name, value) in inputs.Checks)
                    Assert.That(Window.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == name).IsChecked, Is.EqualTo(value), name);
                foreach (var (name, value) in inputs.Choices)
                    Assert.That(Window.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == name).SelectedItem, Is.EqualTo(value), name);
            });
        }

        public void AssertAppliedFields()
        {
            Assert.That(Field("LayerName").Text, Is.EqualTo(Edit.Document.Find(Id).Name));
            Assert.That(Field("TextContent").Text, Is.EqualTo(Edit.Document.Find(Id).Text));
            Assert.That(Field("LayerFontSize").Text, Is.EqualTo("36"));
            Assert.That(Field("CanvasWidth").Text, Is.EqualTo("640"));
            Assert.That(Field("BackgroundBlur").Text, Is.EqualTo("6"));
        }

        public void AssertListOrder()
        {
            var list = Window.GetLogicalDescendants().OfType<ListBox>().Single();
            Assert.That(list.Items.Cast<object>().Select(item => item.ToString()), Is.EqualTo(
                Edit.Document.Layers.AsEnumerable().Reverse().Select(layer =>
                    $"{(layer.Visible ? "" : "[隐藏] ")}{(layer.Locked ? "[锁定] " : "")}文字：{layer.Name}")));
        }

        public void Dispose()
        {
            Edit.MarkSaved(); Window.Close(); Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
