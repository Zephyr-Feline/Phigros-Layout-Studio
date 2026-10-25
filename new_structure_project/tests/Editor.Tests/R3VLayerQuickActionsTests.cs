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

public sealed class R3VLayerQuickActionsTests
{
    [Test]
    public void RenameMenuCommitIsOneUndoableEditAndRefreshesTheList()
    {
        using var host = new Host();
        host.ClickRow();
        Assert.That(((MenuItem)UiTestActions.FindNamed(host.Window, "RenameLayer")).Header,
            Is.EqualTo("重命名图层…"));
        UiTestActions.ClickNamed(host.Window, "RenameLayer");
        var dialog = host.AwaitDialog();
        var input = dialog.GetLogicalDescendants().OfType<TextBox>()
            .Single(box => box.Name == "NamePromptValue");
        input.Text = "Renamed layer";
        UiTestActions.ClickNamed(dialog, "ConfirmNamePrompt");
        host.Settle();

        Assert.Multiple(() =>
        {
            Assert.That(host.Edit.Document.Layers[0].Name, Is.EqualTo("Renamed layer"));
            Assert.That(host.List.Items.Cast<object>().Single().ToString(),
                Is.EqualTo("形状：Renamed layer"));
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
            Assert.That(host.Edit.IsDirty, Is.True);
        });
        host.Key(Key.Z, RawInputModifiers.Control);
        Assert.That(host.Edit.Document.Layers[0].Name, Is.EqualTo("Layer 00"));
        host.Key(Key.Y, RawInputModifiers.Control);
        Assert.That(host.Edit.Document.Layers[0].Name, Is.EqualTo("Renamed layer"));
    }

    [Test]
    public void F2CancelUnchangedEmptyAndLockedNamesKeepHistoryClean()
    {
        using var host = new Host();
        host.ClickRow();
        host.Key(Key.F2);
        var dialog = host.AwaitDialog();
        UiTestActions.ClickNamed(dialog, "CancelNamePrompt");
        host.Settle();
        Assert.That(host.Edit.UndoCount, Is.Zero);

        host.Key(Key.F2);
        dialog = host.AwaitDialog();
        var input = dialog.GetLogicalDescendants().OfType<TextBox>()
            .Single(box => box.Name == "NamePromptValue");
        input.Text = "Layer 00";
        UiTestActions.ClickNamed(dialog, "ConfirmNamePrompt");
        host.Settle();
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(UiTestActions.Status(host.Window), Is.EqualTo("图层名称未更改。"));

        host.Key(Key.F2);
        dialog = host.AwaitDialog();
        input = dialog.GetLogicalDescendants().OfType<TextBox>()
            .Single(box => box.Name == "NamePromptValue");
        input.Text = "   ";
        UiTestActions.ClickNamed(dialog, "ConfirmNamePrompt");
        host.Settle();
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(UiTestActions.Error(host.Window), Is.EqualTo("错误：图层名称不能为空。"));

        host.Edit.Document.Layers[0].Locked = true;
        host.Refresh();
        host.Key(Key.F2);
        host.Settle();
        Assert.That(host.Window.OwnedWindows, Is.Empty);
        Assert.That(UiTestActions.Error(host.Window),
            Is.EqualTo("错误：图层已锁定，请先解锁再重命名。"));
    }

    [Test]
    public void LongExistingLayerNameIsNotTruncatedByTheRenameDialog()
    {
        var longName = new string('长', 130);
        using var host = new Host(longName);
        host.ClickRow();
        host.Key(Key.F2);
        var dialog = host.AwaitDialog();
        var input = dialog.GetLogicalDescendants().OfType<TextBox>()
            .Single(box => box.Name == "NamePromptValue");
        Assert.That(input.Text, Is.EqualTo(longName));
        UiTestActions.ClickNamed(dialog, "ConfirmNamePrompt");
        host.Settle();
        Assert.That(host.Edit.Document.Layers[0].Name, Is.EqualTo(longName));
        Assert.That(host.Edit.UndoCount, Is.Zero);
    }

    [Test]
    public void RowContextMenuDuplicatesDeletesHidesAndLocksTheClickedLayer()
    {
        using var host = new Host();
        var originalId = host.Edit.Document.Layers[0].Id;

        host.ContextAction("LayerContextDuplicate");
        Assert.Multiple(() =>
        {
            Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(2));
            Assert.That(host.Edit.SelectedLayerId, Is.Not.EqualTo(originalId));
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        });
        host.Key(Key.Z, RawInputModifiers.Control);
        Assert.That(host.Edit.Document.Layers.Single().Id, Is.EqualTo(originalId));

        host.ContextAction("LayerContextVisibility");
        Assert.That(host.Edit.Document.Layers.Single().Visible, Is.False);
        host.ContextAction("LayerContextLock");
        Assert.That(host.Edit.Document.Layers.Single().Locked, Is.True);
        host.ContextAction("LayerContextDelete");
        Assert.That(host.Edit.Document.Layers, Is.Empty);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(3));
    }

    [Test]
    public void DoubleClickingARowOpensRenameWithoutChangingTheDocument()
    {
        using var host = new Host();
        var point = host.RowPoint();
        host.Window.MouseMove(point);
        host.Window.MouseDown(point, MouseButton.Left);
        host.Window.MouseUp(point, MouseButton.Left);
        host.Settle();
        host.Window.MouseDown(point, MouseButton.Left);
        host.Window.MouseUp(point, MouseButton.Left);
        var dialog = host.AwaitDialog();
        Assert.That(dialog.Title, Is.EqualTo("重命名图层"));
        Assert.That(host.Edit.UndoCount, Is.Zero);
        UiTestActions.ClickNamed(dialog, "CancelNamePrompt");
        host.Settle();
    }

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public ListBox List => Window.GetVisualDescendants().OfType<ListBox>()
            .Single(list => list.Name == "LayerList");

        public Host(string layerName = "Layer 00")
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            Edit = new(new EditorDocument
            {
                CanvasWidth = 1000,
                CanvasHeight = 600,
                Layers = [new Layer { Kind = LayerKind.Shape, Name = layerName,
                    Bounds = new RectD(100, 100, 200, 160) }]
            });
            Window = new MainWindow(Edit);
            Window.Show();
            Settle();
        }

        public Point RowPoint()
        {
            var row = List.GetVisualDescendants().OfType<ListBoxItem>().Single();
            return row.TranslatePoint(new Point(18, row.Bounds.Height / 2), Window)!.Value;
        }

        public void ClickRow()
        {
            var point = RowPoint();
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
            Settle();
        }

        public void ContextAction(string name)
        {
            var row = List.GetVisualDescendants().OfType<ListBoxItem>().Single();
            var menu = row.GetVisualDescendants().OfType<Grid>()
                .Select(grid => grid.ContextMenu)
                .First(menu => menu?.Name == "LayerRowContextMenu")!;
            var action = menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
            UiTestActions.Click(action);
            Settle();
        }

        public NamePromptDialog AwaitDialog()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                var dialog = Window.OwnedWindows.OfType<NamePromptDialog>().SingleOrDefault();
                if (dialog is not null) return dialog;
                Thread.Sleep(10);
            }
            Assert.Fail("Rename dialog did not open.");
            throw new InvalidOperationException();
        }

        public void Key(Key key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            Window.KeyPress(key, modifiers, PhysicalKey.None, null);
            Settle();
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
