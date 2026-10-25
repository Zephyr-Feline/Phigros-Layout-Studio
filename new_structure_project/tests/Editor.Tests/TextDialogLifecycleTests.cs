using System.Diagnostics;
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

public sealed class TextDialogLifecycleTests
{
    private const string SideDraft = "侧栏尚未应用的第一行文字\n第二行需要完整保留";
    private const string ConfirmedText = "对话框明确确认的新文字\n第二行";

    [TestCase("unchanged")]
    [TestCase("cancel")]
    [TestCase("close")]
    public void AnUnchangedOrCancelledDialogKeepsAllPendingInputsAndHistory(string choice)
    {
        using var host = new Host();
        host.SetDrafts();
        var inputs = host.CaptureInputs();
        var before = host.Edit.Document.Copy();
        host.OpenDialog("menu");
        Assert.That(host.DialogInput.Text, Is.EqualTo(Host.AppliedText));
        if (choice != "unchanged") host.DialogInput.Text = ConfirmedText;

        host.CloseDialog(choice);

        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
        Assert.That(host.Edit.IsDirty, Is.False);
        host.AssertInputs(inputs);
    }

    [Test]
    public void ConfirmingChangedTextRefreshesOnlyTheConfirmedTextAndPreservesOtherInputs()
    {
        using var host = new Host();
        host.SetDrafts();
        var inputs = host.CaptureInputs();
        var before = host.Edit.Document.Copy();
        host.OpenDialog("menu");
        host.DialogInput.Text = ConfirmedText;

        host.CloseDialog("confirm");

        var expected = before.Copy();
        expected.Find(host.Id).Text = ConfirmedText;
        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.StateEquals(expected), Is.True);
        Assert.That(host.Field("TextContent").Text, Is.EqualTo(ConfirmedText));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
        Assert.That(host.Edit.RedoCount, Is.Zero);
        Assert.That(host.Edit.IsDirty, Is.True);
        Assert.That(host.Window.Title, Does.Contain("*"));
        host.AssertInputs(inputs, includeText: false);
        host.Action("Undo");
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.IsDirty, Is.False);
        host.Action("Redo");
        Assert.That(host.Edit.Document.StateEquals(expected), Is.True);
    }

    [Test]
    public void LockedTextRejectsTheSidePanelContentCommitWithoutLosingInputs()
    {
        using var host = new Host(locked: true);
        host.SetDrafts();
        var inputs = host.CaptureInputs();
        var before = host.Edit.Document.Copy();

        host.Action("Apply Text");

        Assert.That(UiTestActions.Error(host.Window), Does.Contain("先解锁"));
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
        Assert.That(host.Edit.IsDirty, Is.False);
        host.AssertInputs(inputs);
    }

    [Test]
    public void LockingTextDoesNotDisableItsExplicitNamePropertyEdit()
    {
        using var host = new Host(locked: true);
        host.Field("LayerName").Text = "锁定层仍可明确改名";

        host.Action("Apply properties");

        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.Find(host.Id).Name, Is.EqualTo("锁定层仍可明确改名"));
        Assert.That(host.Edit.Document.Find(host.Id).Locked, Is.True);
        Assert.That(host.Edit.Document.Find(host.Id).Text, Is.EqualTo(Host.AppliedText));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
    }

    [Test]
    public void CanvasDoubleClickCannotOpenATextDialogDuringAnImageReadAndCanRetryAfterward()
    {
        var loading = new TaskCompletionSource<LoadedImage>();
        var reads = 0;
        using var host = new Host(filePicker: _ => Task.FromResult<string?>("pending-image.png"),
            imageLoader: _ => { reads++; return loading.Task; });
        try
        {
            host.Action("Import Image");
            Assert.That(reads, Is.EqualTo(1));

            host.DoubleClickText();

            Assert.That(host.Window.OwnedWindows.OfType<TextEditDialog>(), Is.Empty);
            Assert.That(UiTestActions.Status(host.Window), Does.Contain("请先完成当前文件操作"));
            loading.SetResult(Picture());
            PumpUntil(() => host.Edit.Document.Layers.Count == 3 || UiTestActions.Error(host.Window) is not null);
            Assert.That(UiTestActions.Error(host.Window), Is.Null);
            Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(3));
            var list = host.Window.GetLogicalDescendants().OfType<ListBox>().Single();
            list.SelectedIndex = list.Items.Count - 1;

            host.OpenDialog("canvas");
            Assert.That(host.DialogInput.Text, Is.EqualTo(Host.AppliedText));
            host.CloseDialog("cancel");
        }
        finally
        {
            loading.TrySetResult(Picture());
            Dispatcher.UIThread.RunJobs();
        }
    }

    [TestCase("menu")]
    [TestCase("canvas")]
    public void BothDialogEntrypointsBlockFileRequestsUntilTheDialogCloses(string entry)
    {
        var picks = 0;
        using var host = new Host(filePicker: _ => { picks++; return Task.FromResult<string?>(null); });
        host.OpenDialog(entry);
        var before = host.Edit.Document.Copy();

        host.Action("Import Image");

        Assert.That(picks, Is.Zero);
        Assert.That(UiTestActions.Status(host.Window), Does.Contain("请先完成当前文件操作"));
        Assert.That(host.Window.OwnedWindows.OfType<TextEditDialog>().Count(), Is.EqualTo(1));
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        host.CloseDialog("cancel");
        host.Action("Import Image");
        Assert.That(picks, Is.EqualTo(1), "Closing either dialog entrypoint must release the shared file gate.");
        Assert.That(host.Window.OwnedWindows, Is.Empty);
    }

    [Test]
    public void ASelectionChangeEditsTheOriginalTargetAndRefreshesTheCurrentSelection()
    {
        using var host = new Host();
        host.SetDrafts();
        host.OpenDialog("menu");
        host.DialogInput.Text = ConfirmedText;
        host.Edit.SelectedLayerId = host.SecondId;

        host.CloseDialog("confirm");

        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.Find(host.Id).Text, Is.EqualTo(ConfirmedText));
        Assert.That(host.Edit.Document.Find(host.SecondId).Text, Is.EqualTo("另一图层的文字"));
        Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(new[] { host.SecondId }));
        Assert.That(host.Field("LayerName").Text, Is.EqualTo("第二图层"));
        Assert.That(host.Field("TextContent").Text, Is.EqualTo("另一图层的文字"));
        Assert.That(host.Field("LayerFontSize").Text, Is.EqualTo("54"));
        Assert.That(host.Field("CanvasWidth").Text, Is.EqualTo("1000"));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
        var list = host.Window.GetLogicalDescendants().OfType<ListBox>().Single();
        Assert.That(list.SelectedIndex, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AChangedOrReplacedDocumentRejectsTheObsoleteDialogResult(bool replace)
    {
        using var host = new Host();
        host.OpenDialog("menu");
        host.DialogInput.Text = ConfirmedText;
        if (replace)
        {
            var document = host.Edit.Document.Copy();
            document.Find(host.Id).Text = "替换工程中的内容";
            host.Edit.ReplaceDocument(document);
        }
        else host.Edit.UpdateLayer(host.SecondId, layer => layer.Name = "对话框期间的其他修改");
        var current = host.Edit.Document;
        var before = current.Copy();
        var undo = host.Edit.UndoCount;
        var redo = host.Edit.RedoCount;
        var dirty = host.Edit.IsDirty;

        host.CloseDialog("confirm");

        Assert.That(UiTestActions.Error(host.Window), Does.Contain("工程已发生变化"));
        Assert.That(host.Edit.Document, Is.SameAs(current));
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo));
        Assert.That(host.Edit.RedoCount, Is.EqualTo(redo));
        Assert.That(host.Edit.IsDirty, Is.EqualTo(dirty));
        Assert.That(host.Window.OwnedWindows, Is.Empty);
    }

    private static LoadedImage Picture() => new(new AssetBlob(TestImages.EncodePng(4, 4,
        TestImages.SyntheticBgra(4, 4))), 4, 4);

    private static void PumpUntil(Func<bool> complete)
    {
        var timer = Stopwatch.StartNew();
        while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(complete(), Is.True, "The pending image operation did not finish.");
        Dispatcher.UIThread.RunJobs();
    }

    private sealed record Inputs(Dictionary<string, string?> Fields, int Caret, int Start, int End,
        bool? KeepAspect, bool? Bold, bool? Italic, object? Alignment, object? VerticalAlignment);

    private sealed class Host : IDisposable
    {
        public const string AppliedText = "已应用文字";
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public Guid Id { get; }
        public Guid SecondId { get; }
        public List<Guid> TextEditRequests { get; } = [];
        private SceneCanvas Canvas => Window.GetVisualDescendants().OfType<SceneCanvas>().Single();
        private TextEditDialog Dialog => Window.OwnedWindows.OfType<TextEditDialog>().Single();
        public TextBox DialogInput => Dialog.GetLogicalDescendants().OfType<TextBox>()
            .Single(input => input.Name == "TextEditValue");

        public Host(bool locked = false, Func<EditorFileAction, Task<string?>>? filePicker = null,
            Func<string, Task<LoadedImage>>? imageLoader = null)
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            var first = new Layer { Kind = LayerKind.Text, Name = "初始名称", Text = AppliedText,
                Locked = locked, FontSize = 54, Bounds = new(140, 180, 420, 180) };
            var second = new Layer { Kind = LayerKind.Text, Name = "第二图层", Text = "另一图层的文字",
                FontSize = 54, Bounds = new(700, 200, 220, 160) };
            Id = first.Id;
            SecondId = second.Id;
            Edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
                Layers = [first, second] });
            Edit.UpdateLayer(Id, layer => layer.Name = "已应用名称");
            Edit.UpdateLayer(Id, layer => layer.ColorArgb = 0xFFCF1313);
            Edit.Undo();
            Edit.MarkSaved();
            Edit.SelectedLayerId = Id;
            Window = new MainWindow(Edit, filePathPicker: filePicker, imageLoader: imageLoader);
            Window.Show();
            Canvas.TextEditRequested += TextEditRequests.Add;
        }

        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>()
            .Single(field => field.Name == name);
        private CheckBox Check(string name) => Window.GetLogicalDescendants().OfType<CheckBox>()
            .Single(field => field.Name == name);
        private ComboBox Choice(string name) => Window.GetLogicalDescendants().OfType<ComboBox>()
            .Single(field => field.Name == name);
        public void Action(string label) => UiTestActions.Click(Window, label);

        public void SetDrafts()
        {
            Assert.That(UiTestActions.Focus(Field("LayerName")), Is.True);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Field("LayerName").Text = "尚未应用的名称";
            Field("LayerFontSize").Text = "NaN";
            Field("LayerFontFamily").Text = "尚未应用的字体";
            Field("LayerX").Text = "123.45678912345678";
            Field("LayerWidth").Text = "600.12345678912345";
            Field("LayerHeight").Text = "240.12345678912345";
            Field("LayerColor").Text = "FFCF1313";
            Field("CanvasWidth").Text = "未应用的画布宽度";
            Field("CanvasHeight").Text = "700";
            Field("BackgroundColor").Text = "未应用的背景颜色";
            Field("BackgroundBlur").Text = "12";
            Field("BackgroundDarkness").Text = "65.12345678912345";
            Check("LayerKeepAspect").IsChecked = true;
            Check("LayerBold").IsChecked = true;
            Check("LayerItalic").IsChecked = true;
            Choice("LayerAlignment").SelectedItem = Editor.Core.HorizontalAlignment.Center;
            Choice("LayerVerticalAlignment").SelectedItem = Editor.Core.VerticalAlignment.Bottom;
            var text = Field("TextContent");
            text.Text = SideDraft;
            text.CaretIndex = SideDraft.Length;
            text.SelectionStart = SideDraft.Length - 5;
            text.SelectionEnd = SideDraft.Length - 1;
        }

        public Inputs CaptureInputs()
        {
            var fields = Window.GetLogicalDescendants().OfType<TextBox>()
                .Where(field => field.Name is not null).ToDictionary(field => field.Name!, field => field.Text);
            var text = Field("TextContent");
            return new(fields, text.CaretIndex, text.SelectionStart, text.SelectionEnd,
                Check("LayerKeepAspect").IsChecked, Check("LayerBold").IsChecked, Check("LayerItalic").IsChecked,
                Choice("LayerAlignment").SelectedItem, Choice("LayerVerticalAlignment").SelectedItem);
        }

        public void AssertInputs(Inputs inputs, bool includeText = true)
        {
            Assert.Multiple(() =>
            {
                foreach (var (name, value) in inputs.Fields)
                    if (includeText || name != "TextContent")
                        Assert.That(Field(name).Text, Is.EqualTo(value), "Pending input " + name);
                if (includeText)
                {
                    var text = Field("TextContent");
                    Assert.That(text.CaretIndex, Is.EqualTo(inputs.Caret));
                    Assert.That(text.SelectionStart, Is.EqualTo(inputs.Start));
                    Assert.That(text.SelectionEnd, Is.EqualTo(inputs.End));
                }
                Assert.That(Check("LayerKeepAspect").IsChecked, Is.EqualTo(inputs.KeepAspect));
                Assert.That(Check("LayerBold").IsChecked, Is.EqualTo(inputs.Bold));
                Assert.That(Check("LayerItalic").IsChecked, Is.EqualTo(inputs.Italic));
                Assert.That(Choice("LayerAlignment").SelectedItem, Is.EqualTo(inputs.Alignment));
                Assert.That(Choice("LayerVerticalAlignment").SelectedItem, Is.EqualTo(inputs.VerticalAlignment));
            });
        }

        public void OpenDialog(string entry)
        {
            if (entry == "canvas")
            {
                DoubleClickText();
                PumpUntil(() => Window.OwnedWindows.OfType<TextEditDialog>().Any());
            }
            else UiTestActions.ClickNamed(Window, "EditSelectedText");
            Assert.That(Window.OwnedWindows.OfType<TextEditDialog>().Count(), Is.EqualTo(1));
        }

        public void CloseDialog(string choice)
        {
            if (choice == "close") Dialog.Close();
            else UiTestActions.ClickNamed(Dialog, choice == "cancel" ? "CancelTextEdit" : "ConfirmTextEdit");
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Assert.That(Window.OwnedWindows.OfType<TextEditDialog>(), Is.Empty);
        }

        public void DoubleClickText()
        {
            Window.UpdateLayout();
            var centre = LayerGeometry.Centre(Edit.Document.Find(Id).Bounds);
            // The shared headless mouse keeps a process-wide click streak, so invoke the handler
            // with a deterministic double-click event. This covers hit testing and TextEditRequested,
            // not routed-event delivery or mouse double-click recognition.
            var windowPoint = View(centre);
            var canvasPoint = Window.TranslatePoint(windowPoint, Canvas)!.Value;
            var logicalPoint = Canvas.CurrentViewTransform.ToLogical(
                new PointD(canvasPoint.X, canvasPoint.Y));
            Assert.That(LayerGeometry.Contains(Edit.Document.Find(Id), logicalPoint), Is.True,
                "the injected double-click must hit the target text layer");
            var properties = new PointerPointProperties(
                RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
            var pointerEvent = new PointerPressedEventArgs(
                Canvas, new Pointer(1, PointerType.Mouse, true), Window, windowPoint,
                timestamp: 0, properties, KeyModifiers.None, clickCount: 2);
            Assert.That(pointerEvent.ClickCount, Is.EqualTo(2));
            Assert.That(pointerEvent.GetCurrentPoint(Canvas).Properties.IsLeftButtonPressed, Is.True);
            Assert.That(pointerEvent.GetPosition(Canvas), Is.EqualTo(canvasPoint));
            var handler = typeof(SceneCanvas).GetMethod("OnPointerPressed",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("SceneCanvas input handler is missing.");
            Assert.That(handler.DeclaringType, Is.EqualTo(typeof(SceneCanvas)));
            var requestsBefore = TextEditRequests.Count;
            handler.Invoke(Canvas, [pointerEvent]);
            Dispatcher.UIThread.RunJobs();
            Assert.That(TextEditRequests.Count, Is.GreaterThan(requestsBefore),
                "canvas double-click did not request text editing");
        }

        private Point View(PointD logical)
        {
            var transform = new ViewportTransform(Edit.Document.CanvasWidth, Edit.Document.CanvasHeight,
                Canvas.Bounds.Width, Canvas.Bounds.Height);
            var point = transform.ToView(logical);
            return Canvas.TranslatePoint(new(point.X, point.Y), Window)!.Value;
        }

        public void Dispose()
        {
            foreach (var dialog in Window.OwnedWindows.ToArray()) dialog.Close();
            Dispatcher.UIThread.RunJobs();
            Edit.CancelGesture();
            Canvas.Refresh();
            Edit.MarkSaved();
            Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
