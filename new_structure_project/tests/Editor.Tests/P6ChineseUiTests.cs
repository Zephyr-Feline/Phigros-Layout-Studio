using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P6ChineseUiTests
{
    [TestCase(2)]
    [TestCase(4)]
    public void InvalidSongCountShowsChineseModalAndLeavesDocumentAndHistoryUntouched(int count)
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(new EditorDocument());
        var before = edit.Document.Copy(); var reads = 0;
        var window = new MainWindow(edit,
            songImagePathsPicker: () => Task.FromResult<IReadOnlyList<string>>(
                Enumerable.Range(0, count).Select(index => $"{index}.png").ToArray()),
            imageLoader: _ => { reads++; throw new AssertionException("Invalid count must not read images."); });
        try
        {
            window.Show(); UiTestActions.ClickNamed(window, "BuildSongBatch");
            var prompt = window.OwnedWindows.Single();
            Assert.That(prompt.Title, Is.EqualTo("无法生成歌曲"));
            Assert.That(prompt.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Text?.Contains("恰好") == true).Text,
                Does.Contain("3 张或 5 张"));
            Assert.That(UiTestActions.Status(window), Does.StartWith("错误："));
            Assert.That(edit.Document.StateEquals(before), Is.True);
            Assert.That(edit.UndoCount, Is.Zero); Assert.That(reads, Is.Zero);
            UiTestActions.ClickNamed(prompt, "ConfirmMessage"); Dispatcher.UIThread.RunJobs();
            Assert.That(window.OwnedWindows, Is.Empty);
        }
        finally { edit.MarkSaved(); foreach (var prompt in window.OwnedWindows.ToArray()) prompt.Close(); window.Close(); }
    }

    [TestCase("confirm")]
    [TestCase("cancel")]
    [TestCase("unchanged")]
    public void MultilineTextDialogOnlyRecordsOneConfirmedChange(string choice)
    {
        AvaloniaTestHost.EnsureStarted();
        var layer = new Layer { Kind = LayerKind.Text, Text = "原文字", Bounds = new(100, 100, 400, 160) };
        var edit = new EditSession(new EditorDocument { Layers = [layer] }) { SelectedLayerId = layer.Id };
        var before = edit.Document.Copy(); var window = new MainWindow(edit);
        try
        {
            window.Show(); UiTestActions.ClickNamed(window, "EditSelectedText");
            var dialog = window.OwnedWindows.OfType<TextEditDialog>().Single();
            var input = dialog.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Name == "TextEditValue");
            Assert.That(input.AcceptsReturn, Is.True); Assert.That(input.MaxLength, Is.EqualTo(4000));
            if (choice != "unchanged") input.Text = "第一行\n第二行，中文输入";
            UiTestActions.ClickNamed(dialog, choice == "cancel" ? "CancelTextEdit" : "ConfirmTextEdit");
            Dispatcher.UIThread.RunJobs();
            if (choice == "confirm")
            {
                Assert.That(edit.Document.Find(layer.Id).Text, Is.EqualTo(input.Text));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
                edit.Undo(); Assert.That(edit.Document.StateEquals(before), Is.True);
                edit.Redo(); Assert.That(edit.Document.Find(layer.Id).Text, Is.EqualTo(input.Text));
            }
            else { Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(edit.UndoCount, Is.Zero); }
        }
        finally { edit.MarkSaved(); foreach (var prompt in window.OwnedWindows.ToArray()) prompt.Close(); window.Close(); }
    }

    [Test]
    public void LockedTextCannotOpenTheEditDialog()
    {
        AvaloniaTestHost.EnsureStarted();
        var layer = new Layer { Kind = LayerKind.Text, Text = "锁定文字", Locked = true };
        var edit = new EditSession(new EditorDocument { Layers = [layer] }) { SelectedLayerId = layer.Id };
        var window = new MainWindow(edit);
        try
        {
            window.Show(); UiTestActions.ClickNamed(window, "EditSelectedText");
            Assert.That(window.OwnedWindows, Is.Empty);
            Assert.That(UiTestActions.Status(window), Does.Contain("先解锁"));
            Assert.That(edit.Document.Find(layer.Id).Text, Is.EqualTo("锁定文字")); Assert.That(edit.UndoCount, Is.Zero);
        }
        finally { window.Close(); }
    }

    [Test]
    public void AddTextCreatesDiscoverableContentAndChineseAlignmentKeepsEnumValues()
    {
        AvaloniaTestHost.EnsureStarted(); var edit = new EditSession(new EditorDocument()); var window = new MainWindow(edit);
        try
        {
            window.Show(); UiTestActions.Click(window, "Add Text");
            Assert.That(edit.Document.Layers.Single().Text, Is.EqualTo("双击编辑文字"));
            var horizontal = window.GetLogicalDescendants().OfType<ComboBox>().Single(box => box.Name == "LayerAlignment");
            Assert.That(horizontal.SelectedItem, Is.EqualTo(HorizontalAlignment.Left));
            Assert.That(((TextBlock)horizontal.ItemTemplate!.Build(horizontal.SelectedItem)!).Text, Is.EqualTo("左对齐"));
            var vertical = window.GetLogicalDescendants().OfType<ComboBox>().Single(box => box.Name == "LayerVerticalAlignment");
            Assert.That(vertical.SelectedItem, Is.EqualTo(VerticalAlignment.Top));
            Assert.That(((TextBlock)vertical.ItemTemplate!.Build(vertical.SelectedItem)!).Text, Is.EqualTo("顶部"));
            edit.Undo(); Assert.That(edit.Document.Layers, Is.Empty);
        }
        finally { edit.MarkSaved(); window.Close(); }
    }
}
