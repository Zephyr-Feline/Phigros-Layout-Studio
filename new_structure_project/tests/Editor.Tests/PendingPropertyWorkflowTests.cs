using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class PendingPropertyWorkflowTests
{
    private const string Draft = "尚未应用的第一行\n第二行";

    [TestCase(false)]
    [TestCase(true)]
    public void ApplyingStyleKeepsTheMultilineDraftWithoutCommittingIt(bool enter)
    {
        using var host = new Host();
        host.Field("TextContent").Text = Draft;
        host.Field("LayerFontSize").Text = "72";

        host.ApplyProperties(enter);

        Assert.Multiple(() =>
        {
            Assert.That(UiTestActions.Error(host.Window), Is.Null);
            Assert.That(host.Field("TextContent").Text, Is.EqualTo(Draft));
            Assert.That(host.Layer.Text, Is.EqualTo(Host.OriginalText));
            Assert.That(host.Layer.FontSize, Is.EqualTo(72));
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ApplyingStyleKeepsTheMultilineDraftCaretAndSelection()
    {
        using var host = new Host();
        var input = host.Field("TextContent");
        input.Text = Draft;
        host.Field("LayerFontSize").Text = "72";
        // Keep the selection beyond the old model text's length: a temporary assignment of
        // that old text would clamp these positions even if the draft were restored later.
        input.CaretIndex = Draft.Length;
        input.SelectionStart = Draft.Length - 4;
        input.SelectionEnd = Draft.Length - 1;
        var caret = input.CaretIndex;
        var start = input.SelectionStart;
        var end = input.SelectionEnd;

        host.ApplyProperties(false);

        Assert.Multiple(() =>
        {
            Assert.That(input.Text, Is.EqualTo(Draft));
            Assert.That(input.CaretIndex, Is.EqualTo(caret));
            Assert.That(input.SelectionStart, Is.EqualTo(start));
            Assert.That(input.SelectionEnd, Is.EqualTo(end));
            Assert.That(host.Layer.Text, Is.EqualTo(Host.OriginalText));
            Assert.That(host.Layer.FontSize, Is.EqualTo(72));
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnchangedPropertyApplyPreservesTheDraftAndTheRedoBranch(bool enter)
    {
        using var host = new Host();
        host.Field("LayerName").Text = "临时名称";
        host.ApplyProperties(false);
        UiTestActions.Click(host.Window, "Undo");
        var before = host.Edit.Document.Copy();
        host.Field("TextContent").Text = Draft;

        host.ApplyProperties(enter);

        Assert.Multiple(() =>
        {
            Assert.That(host.Field("TextContent").Text, Is.EqualTo(Draft));
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
            Assert.That(host.Edit.UndoCount, Is.Zero);
            Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
            Assert.That(host.Edit.IsDirty, Is.False);
        });
    }

    [TestCase("NaN")]
    [TestCase("0")]
    public void InvalidStyleCanBeCorrectedWithoutLosingTheMultilineDraft(string invalid)
    {
        using var host = new Host();
        var before = host.Edit.Document.Copy();
        host.Field("TextContent").Text = Draft;
        host.Field("LayerFontSize").Text = invalid;

        host.ApplyProperties(true);

        Assert.Multiple(() =>
        {
            Assert.That(UiTestActions.Error(host.Window), Is.Not.Null);
            Assert.That(host.Field("TextContent").Text, Is.EqualTo(Draft));
            Assert.That(host.Field("LayerFontSize").Text, Is.EqualTo(invalid));
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
            Assert.That(host.Edit.UndoCount, Is.Zero);
        });

        host.Field("LayerFontSize").Text = "72";
        host.ApplyProperties(true);
        Assert.That(host.Field("TextContent").Text, Is.EqualTo(Draft));
        Assert.That(host.Layer.Text, Is.EqualTo(Host.OriginalText));
        Assert.That(host.Layer.FontSize, Is.EqualTo(72));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void ExplicitTextApplyIsASeparateUndoableEditAfterTheStyleCommit()
    {
        using var host = new Host();
        host.Field("TextContent").Text = Draft;
        host.Field("LayerFontSize").Text = "72";
        host.ApplyProperties(true);
        Assert.That(host.Field("TextContent").Text, Is.EqualTo(Draft));

        UiTestActions.Click(host.Window, "Apply Text");
        Assert.That(host.Layer.Text, Is.EqualTo(Draft));
        Assert.That(host.Layer.FontSize, Is.EqualTo(72));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
        UiTestActions.Click(host.Window, "Apply Text");
        Assert.That(host.Edit.UndoCount, Is.EqualTo(2), "An unchanged text apply must not add history.");

        UiTestActions.Click(host.Window, "Undo");
        Assert.That(host.Layer.Text, Is.EqualTo(Host.OriginalText));
        Assert.That(host.Layer.FontSize, Is.EqualTo(72));
        Assert.That(host.Field("TextContent").Text, Is.EqualTo(Host.OriginalText));
        UiTestActions.Click(host.Window, "Undo");
        Assert.That(host.Layer.FontSize, Is.EqualTo(54));
        UiTestActions.Click(host.Window, "Redo");
        UiTestActions.Click(host.Window, "Redo");
        Assert.That(host.Layer.Text, Is.EqualTo(Draft));
        Assert.That(host.Field("TextContent").Text, Is.EqualTo(Draft));
        Assert.That(host.Layer.FontSize, Is.EqualTo(72));
    }

    [Test]
    public void ApplyingTextKeepsPendingPropertiesForASeparateUndoableCommit()
    {
        using var host = new Host();
        host.Field("TextContent").Text = Draft;
        host.Field("LayerName").Text = "尚未应用的名称";
        host.Field("LayerFontSize").Text = "72.12345678912345";
        host.Field("LayerColor").Text = "FFCF1313";

        UiTestActions.Click(host.Window, "Apply Text");

        Assert.Multiple(() =>
        {
            Assert.That(host.Layer.Text, Is.EqualTo(Draft));
            Assert.That(host.Layer.Name, Is.EqualTo("第一图层"));
            Assert.That(host.Layer.FontSize, Is.EqualTo(54));
            Assert.That(host.Field("LayerName").Text, Is.EqualTo("尚未应用的名称"));
            Assert.That(host.Field("LayerFontSize").Text, Is.EqualTo("72.12345678912345"));
            Assert.That(host.Field("LayerColor").Text, Is.EqualTo("FFCF1313"));
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        });

        host.ApplyProperties(true);
        Assert.That(host.Layer.Name, Is.EqualTo("尚未应用的名称"));
        Assert.That(host.Layer.FontSize, Is.EqualTo(72.12345678912345));
        Assert.That(host.Layer.ColorArgb, Is.EqualTo(0xFFCF1313));
        Assert.That(host.Layer.Text, Is.EqualTo(Draft));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
        UiTestActions.Click(host.Window, "Undo");
        Assert.That(host.Layer.Text, Is.EqualTo(Draft));
        Assert.That(host.Layer.FontSize, Is.EqualTo(54));
        UiTestActions.Click(host.Window, "Undo");
        Assert.That(host.Layer.Text, Is.EqualTo(Host.OriginalText));
        UiTestActions.Click(host.Window, "Redo");
        UiTestActions.Click(host.Window, "Redo");
        Assert.That(host.Layer.Text, Is.EqualTo(Draft));
        Assert.That(host.Layer.FontSize, Is.EqualTo(72.12345678912345));
    }

    [Test]
    public void UnchangedTextApplyPreservesPendingPropertiesAndTheRedoBranch()
    {
        using var host = new Host();
        host.Field("LayerName").Text = "临时名称";
        host.ApplyProperties(false);
        UiTestActions.Click(host.Window, "Undo");
        var before = host.Edit.Document.Copy();
        host.Field("LayerFontSize").Text = "NaN";

        UiTestActions.Click(host.Window, "Apply Text");

        Assert.Multiple(() =>
        {
            Assert.That(UiTestActions.Error(host.Window), Is.Null);
            Assert.That(host.Field("LayerFontSize").Text, Is.EqualTo("NaN"));
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
            Assert.That(host.Edit.UndoCount, Is.Zero);
            Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ATextDraftDoesNotLeakAcrossSelectionChanges(bool deselect)
    {
        using var host = new Host();
        host.Field("TextContent").Text = Draft;
        var list = host.Window.GetLogicalDescendants().OfType<ListBox>().Single();
        if (deselect)
        {
            list.SelectedIndex = -1;
            Assert.That(host.Edit.SelectedLayerIds, Is.Empty);
        }
        else
        {
            list.SelectedIndex = 0; // The second layer is the frontmost list item.
            Assert.That(host.Edit.SelectedLayerId, Is.EqualTo(host.SecondId));
            Assert.That(host.Field("TextContent").Text, Is.EqualTo("另一图层的内容"));
        }

        list.SelectedIndex = 1;

        Assert.That(host.Edit.SelectedLayerId, Is.EqualTo(host.Id));
        Assert.That(host.Field("TextContent").Text, Is.EqualTo(Host.OriginalText));
        Assert.That(host.Layer.Text, Is.EqualTo(Host.OriginalText));
        Assert.That(host.Edit.UndoCount, Is.Zero);
    }

    private sealed class Host : IDisposable
    {
        public const string OriginalText = "已经应用的文字";
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public Guid Id { get; }
        public Guid SecondId { get; }
        public Layer Layer => Edit.Document.Find(Id);

        public Host()
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            var first = new Layer { Kind = LayerKind.Text, Name = "第一图层", Text = OriginalText,
                FontSize = 54, Bounds = new(100, 100, 500, 150) };
            var second = new Layer { Kind = LayerKind.Text, Name = "第二图层", Text = "另一图层的内容",
                Bounds = new(700, 100, 500, 150) };
            Id = first.Id;
            SecondId = second.Id;
            Edit = new EditSession(new EditorDocument { Layers = [first, second] }) { SelectedLayerId = Id };
            Window = new MainWindow(Edit);
            Window.Show();
        }

        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>()
            .Single(field => field.Name == name);

        public void ApplyProperties(bool enter)
        {
            if (!enter) UiTestActions.ClickNamed(Window, "ApplyLayerProperties");
            else
            {
                Assert.That(UiTestActions.Focus(Field("LayerFontSize")), Is.True);
                Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            }
        }

        public void Dispose()
        {
            Edit.MarkSaved();
            Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
