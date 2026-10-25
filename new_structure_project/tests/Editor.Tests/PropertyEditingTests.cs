using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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

public sealed class PropertyEditingTests
{
    [TestCase(LayerKind.Shape)]
    [TestCase(LayerKind.Text)]
    [TestCase(LayerKind.Badge)]
    public void ShortDisplayAndUnchangedApplyPreserveExactDocumentAndRedo(LayerKind kind)
    {
        using var host = new Host(kind);
        host.Edit.UpdateLayer(host.Id, layer => layer.Name = "暂时修改");
        host.Edit.Undo();
        // Refresh through the existing UI, including a selection refresh after Undo.
        UiTestActions.Click(host.Window, "Redo");
        UiTestActions.Click(host.Window, "Undo");
        var before = host.Edit.Document.Copy();
        var png = TestImages.RenderPng(before);
        Assert.That(host.Field("LayerX").Text, Is.EqualTo("102.71"));
        Assert.That(host.Field("LayerRotation").Text, Is.EqualTo("33.123"));
        host.Apply();
        Assert.Multiple(() =>
        {
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
            Assert.That(host.Edit.UndoCount, Is.Zero);
            Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
            Assert.That(TestImages.RenderPng(host.Edit.Document), Is.EqualTo(png));
        });
    }

    [Test]
    public void RenamingAndChangingColorPreserveAllUneditedDoubleValues()
    {
        using var host = new Host();
        var before = host.Edit.Document.Copy();
        host.Field("LayerName").Text = "只改名称与颜色";
        host.Field("LayerColor").Text = "FFCF1313";
        host.Apply();
        var expected = before.Copy();
        expected.Find(host.Id).Name = "只改名称与颜色";
        expected.Find(host.Id).ColorArgb = 0xFFCF1313;
        Assert.That(host.Edit.Document.StateEquals(expected), Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        host.Edit.Undo();
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        host.Edit.Redo();
        Assert.That(host.Edit.Document.StateEquals(expected), Is.True);
    }

    [TestCase("LayerX", "1e-12")]
    [TestCase("LayerY", "-0.0000000000123456789")]
    [TestCase("LayerWidth", "400.98765432112345")]
    [TestCase("LayerHeight", "150.98765432112345")]
    [TestCase("LayerRotation", "-179.99999999912345")]
    [TestCase("LayerFontSize", "64.12345678912345")]
    [TestCase("LayerLineSpacing", "1.98765432112345")]
    public void ExplicitPrecisionIsStoredAndRepeatedApplyDoesNotRoundIt(string field, string input)
    {
        using var host = new Host();
        host.Field(field).Text = input;
        host.Apply();
        var layer = host.Edit.Document.Find(host.Id);
        var actual = field switch
        {
            "LayerX" => layer.Bounds.X, "LayerY" => layer.Bounds.Y,
            "LayerWidth" => layer.Bounds.Width, "LayerHeight" => layer.Bounds.Height,
            "LayerRotation" => layer.RotationDegrees, "LayerFontSize" => layer.FontSize,
            _ => layer.LineSpacing
        };
        Assert.That(actual, Is.EqualTo(double.Parse(input, CultureInfo.InvariantCulture)));
        Assert.That(host.Field(field).Text!.Length, Is.LessThanOrEqualTo(12));
        var before = host.Edit.Document.Copy();
        host.Apply();
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
    }

    [TestCase("LayerWidth")]
    [TestCase("LayerHeight")]
    public void LockedRatioUsesOnlyTheEditedDimensionDespiteShortDisplay(string field)
    {
        using var host = new Host(LayerKind.Shape, keepAspect: true);
        var source = host.Edit.Document.Find(host.Id).Copy();
        var ratio = source.Bounds.Width / source.Bounds.Height;
        host.Field(field).Text = "333.3333333333333";
        host.Apply();
        var changed = host.Edit.Document.Find(host.Id);
        Assert.Multiple(() =>
        {
            Assert.That(changed.Bounds.X, Is.EqualTo(source.Bounds.X));
            Assert.That(changed.Bounds.Y, Is.EqualTo(source.Bounds.Y));
            Assert.That(changed.RotationDegrees, Is.EqualTo(source.RotationDegrees));
            Assert.That(changed.Bounds.Width / changed.Bounds.Height, Is.EqualTo(ratio).Within(1e-14));
            Assert.That(field == "LayerWidth" ? changed.Bounds.Width : changed.Bounds.Height,
                Is.EqualTo(333.3333333333333));
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        });
    }

    [TestCase("LayerWidth", "0")]
    [TestCase("LayerRotation", "180.01")]
    [TestCase("LayerFontSize", "NaN")]
    [TestCase("LayerLineSpacing", "")]
    public void InvalidMixedEditIsAtomicAndCanBeCorrected(string field, string input)
    {
        using var host = new Host();
        var before = host.Edit.Document.Copy();
        host.Field("LayerName").Text = "不应半次提交";
        host.Field(field).Text = input;
        host.Apply();
        Assert.That(UiTestActions.Error(host.Window), Is.Not.Null);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(host.Field("LayerName").Text, Is.EqualTo("不应半次提交"));
        host.Field(field).Text = field switch
        {
            "LayerWidth" => "500", "LayerRotation" => "45",
            "LayerFontSize" => "64", _ => "1.5"
        };
        host.Apply();
        Assert.That(host.Edit.Document.Find(host.Id).Name, Is.EqualTo("不应半次提交"));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
    }

    [TestCase("LayerName")]
    [TestCase("LayerX")]
    [TestCase("LayerY")]
    [TestCase("LayerWidth")]
    [TestCase("LayerHeight")]
    [TestCase("LayerRotation")]
    [TestCase("LayerColor")]
    [TestCase("LayerFontFamily")]
    [TestCase("LayerFontSize")]
    [TestCase("LayerLineSpacing")]
    public void EnterInSingleLinePropertyAppliesOnce(string field)
    {
        using var host = new Host();
        host.Field("LayerName").Text = "Enter 确认";
        var input = host.Field(field);
        Assert.That(UiTestActions.Focus(input), Is.True);
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.That(host.Edit.Document.Find(host.Id).Name, Is.EqualTo("Enter 确认"));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void EnterInBadgeLevelAppliesAndInvalidEnterKeepsFocusAndDocument()
    {
        using var host = new Host(LayerKind.Badge);
        var before = host.Edit.Document.Copy();
        var input = host.Field("LayerLevel");
        input.Text = "0";
        Assert.That(UiTestActions.Focus(input), Is.True);
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(input.IsFocused, Is.True);
        input.Text = "16";
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.That(host.Edit.Document.Find(host.Id).Level, Is.EqualTo(16));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void MultilineEnterInsertsNewlineAndDoesNotCommitProperties()
    {
        using var host = new Host();
        var input = host.Field("TextContent");
        input.Text = "第一行";
        host.Field("LayerName").Text = "尚未提交";
        Assert.That(UiTestActions.Focus(input), Is.True);
        input.CaretIndex = input.Text.Length;
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.That(input.Text, Does.Contain("\n"));
        Assert.That(host.Edit.Document.Find(host.Id).Name, Is.Not.EqualTo("尚未提交"));
        Assert.That(host.Edit.UndoCount, Is.Zero);
    }

    [TestCase(RawInputModifiers.Control)]
    [TestCase(RawInputModifiers.Shift)]
    [TestCase(RawInputModifiers.Alt)]
    public void ModifiedEnterDoesNotApplyLayerProperties(RawInputModifiers modifiers)
    {
        using var host = new Host();
        host.Field("LayerName").Text = "尚未提交";
        Assert.That(UiTestActions.Focus(host.Field("LayerX")), Is.True);
        host.Window.KeyPress(Key.Enter, modifiers, PhysicalKey.None, null);
        Assert.That(host.Edit.Document.Find(host.Id).Name, Is.Not.EqualTo("尚未提交"));
        Assert.That(host.Edit.UndoCount, Is.Zero);
    }

    [Test]
    public void EnterInCanvasSizeDoesNotApplyPendingLayerProperties()
    {
        using var host = new Host();
        host.Field("LayerName").Text = "尚未提交";
        Assert.That(UiTestActions.Focus(host.Field("CanvasWidth")), Is.True);
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.That(host.Edit.Document.Find(host.Id).Name, Is.Not.EqualTo("尚未提交"));
        Assert.That(host.Edit.UndoCount, Is.Zero);
    }

    [TestCase("LayerName")]
    [TestCase("LayerFontFamily")]
    public void EnterDuringImePreeditDoesNotApplyOrClearTheComposition(string field)
    {
        using var host = new Host();
        host.Field("LayerName").Text = "尚未提交";
        var input = host.Field(field);
        Assert.That(UiTestActions.Focus(input), Is.True);
        // Headless KeyPress flushes queued focus/render work before dispatching input.
        // Finish that work before installing synthetic IME state on the current presenter.
        Dispatcher.UIThread.RunJobs();
        host.Window.UpdateLayout();
        Assert.That(input.IsFocused, Is.True);
        var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
        presenter.PreeditText = "ni";
        Assert.That(presenter.PreeditText, Is.EqualTo("ni"));
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.That(host.Edit.Document.Find(host.Id).Name, Is.Not.EqualTo("尚未提交"));
        Assert.That(host.Edit.UndoCount, Is.Zero);
        Assert.That(presenter.PreeditText, Is.EqualTo("ni"));
        Assert.That(input.IsFocused, Is.True);
        presenter.PreeditText = null;
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.That(host.Edit.Document.Find(host.Id).Name, Is.EqualTo("尚未提交"));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
    }

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public Guid Id { get; }
        public Host(LayerKind kind = LayerKind.Text, bool keepAspect = false)
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            var layer = kind == LayerKind.Badge ? EditorDocument.NewBadge(new(102.71014492753623, -0.0000123456789), 256.123456789) :
                new Layer { Kind = kind, Name = "测试层", Bounds = new(102.71014492753623, -0.0000123456789,
                    650.1234567891234, 160.9876543219876), Text = "精度测试" };
            layer.RotationDegrees = 33.12345678912345;
            layer.FontSize = 54.12345678912345;
            layer.LineSpacing = 1.2345678912345;
            layer.KeepAspect = kind == LayerKind.Badge || keepAspect;
            Id = layer.Id;
            Edit = new EditSession(new EditorDocument { Layers = [layer] }) { SelectedLayerId = Id };
            Window = new MainWindow(Edit);
            Window.Show();
        }
        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Name == name);
        public void Apply() => UiTestActions.ClickNamed(Window, "ApplyLayerProperties");
        public void Dispose()
        {
            Edit.MarkSaved(); Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
