using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class SaveDraftLifecycleTests
{
    private const string TextDraft = "保存后仍待应用的第一行文字\n第二行完整保留";
    private const string SizeDraft = "85.98765432198765";
    private const string WidthDraft = "700.98765432198765";
    private const string HeightDraft = "300.12345678912345";

    [TestCase("in-place", false)]
    [TestCase("save-as", false)]
    [TestCase("untitled", false)]
    [TestCase("temporary", false)]
    [TestCase("in-place", true)]
    [TestCase("save-as", true)]
    [TestCase("untitled", true)]
    [TestCase("temporary", true)]
    public void SuccessfulSaveKeepsInputsAndOnlyPersistsTheAppliedDocument(string mode, bool invalid)
    {
        using var host = new Host(mode);
        var applied = host.Edit.Document.Copy();
        var ratio = host.Layer.Bounds.Width / host.Layer.Bounds.Height;
        host.SetDrafts(invalid);
        var drafts = host.CaptureInputs();

        host.Save();

        Assert.Multiple(() =>
        {
            Assert.That(UiTestActions.Error(host.Window), Is.Null);
            Assert.That(host.ReadSaved().StateEquals(applied), Is.True);
            Assert.That(host.Edit.Document.StateEquals(applied), Is.True);
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
            Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
            Assert.That(host.Edit.IsDirty, Is.False);
            Assert.That(host.Window.Title, Does.Not.Contain("*"));
            Assert.That(host.Native.CurrentPath, Is.EqualTo(mode == "temporary" ? null : host.OutputPath));
        });
        host.AssertInputs(drafts);
        if (mode is "save-as" or "temporary")
            Assert.That(File.ReadAllBytes(host.OriginalPath), Is.EqualTo(host.OriginalBytes));

        // The last edited dimension remains the height driver after saving; no draft was
        // parsed or committed by Save, including an invalid font size or canvas setting.
        if (invalid) host.Field("LayerFontSize").Text = SizeDraft;
        UiTestActions.ClickNamed(host.Window, "ApplyLayerProperties");
        Assert.Multiple(() =>
        {
            Assert.That(host.Layer.Name, Is.EqualTo("尚未应用的图层名称"));
            Assert.That(host.Layer.FontSize, Is.EqualTo(double.Parse(SizeDraft, CultureInfo.InvariantCulture)));
            Assert.That(host.Layer.Bounds.Height, Is.EqualTo(double.Parse(HeightDraft, CultureInfo.InvariantCulture)));
            Assert.That(host.Layer.Bounds.Width, Is.EqualTo(double.Parse(HeightDraft, CultureInfo.InvariantCulture) * ratio));
            Assert.That(host.Layer.KeepAspect, Is.True);
            Assert.That(host.Layer.Text, Is.EqualTo(Host.AppliedText));
            Assert.That(host.Field("TextContent").Text, Is.EqualTo(TextDraft));
            Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
            Assert.That(host.Edit.RedoCount, Is.Zero);
            Assert.That(host.Edit.IsDirty, Is.True);
        });
        UiTestActions.Click(host.Window, "Undo");
        Assert.That(host.Edit.Document.StateEquals(applied), Is.True);
        Assert.That(host.Edit.IsDirty, Is.False, "Undoing the later explicit edit must reach the saved revision.");
        UiTestActions.Click(host.Window, "Redo");
        Assert.That(host.Edit.IsDirty, Is.True);
    }

    [TestCase("save-as")]
    [TestCase("untitled")]
    [TestCase("temporary")]
    public void CancellingTheSavePickerKeepsInputsHistoryAndTheOriginalSavepoint(string mode)
    {
        using var host = new Host(mode);
        var before = host.Edit.Document.Copy();
        var title = host.Window.Title;
        var path = host.Native.CurrentPath;
        host.SetDrafts(invalid: true);
        var drafts = host.CaptureInputs();
        host.PickPath = () => Task.FromResult<string?>(null);

        host.Save();

        host.AssertInputs(drafts);
        Assert.Multiple(() =>
        {
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
            Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
            Assert.That(host.Edit.IsDirty, Is.True);
            Assert.That(host.Native.CurrentPath, Is.EqualTo(path));
            Assert.That(host.Window.Title, Is.EqualTo(title));
            Assert.That(File.Exists(host.OutputPath), Is.False);
        });
        if (mode != "untitled")
            Assert.That(File.ReadAllBytes(host.OriginalPath), Is.EqualTo(host.OriginalBytes));
    }

    [TestCase("in-place")]
    [TestCase("save-as")]
    [TestCase("temporary")]
    public void FailedSaveKeepsInputsAndDoesNotReplaceTheExistingOutput(string mode)
    {
        using var host = new Host(mode);
        var before = host.Edit.Document.Copy();
        var title = host.Window.Title;
        var path = host.Native.CurrentPath;
        host.SetDrafts(invalid: true);
        var drafts = host.CaptureInputs();
        if (mode != "in-place") File.WriteAllBytes(host.OutputPath, [7, 8, 9]);
        var previous = File.ReadAllBytes(host.OutputPath);
        // A real Windows sharing failure exercises the atomic writer without replacing it.
        using (File.Open(host.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            host.Save();

        Assert.That(UiTestActions.Error(host.Window), Is.Not.Null);
        host.AssertInputs(drafts);
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(host.OutputPath), Is.EqualTo(previous));
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
            Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
            Assert.That(host.Edit.IsDirty, Is.True);
            Assert.That(host.Native.CurrentPath, Is.EqualTo(path));
            Assert.That(host.Window.Title, Is.EqualTo(title));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SaveAsRefreshesTheActualSelectionIfItChangedDuringThePicker(bool deselect)
    {
        using var host = new Host("save-as");
        var applied = host.Edit.Document.Copy();
        host.SetDrafts(invalid: false);
        var picked = new TaskCompletionSource<string?>();
        host.PickPath = () => picked.Task;
        host.Save();
        Assert.That(File.Exists(host.OutputPath), Is.False);

        // Session changes can arrive independently of list events. A successful save must
        // reconcile the actual context rather than preserve controls for the old selection.
        host.Edit.SelectedLayerId = deselect ? null : host.SecondId;
        picked.SetResult(host.OutputPath);
        Dispatcher.UIThread.RunJobs();

        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.ReadSaved().StateEquals(applied), Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
        Assert.That(host.Edit.IsDirty, Is.False);
        Assert.That(host.Field("CanvasWidth").Text, Is.EqualTo(applied.CanvasWidth.ToString(CultureInfo.InvariantCulture)));
        var list = host.Window.GetLogicalDescendants().OfType<ListBox>().Single();
        if (deselect)
        {
            Assert.That(list.SelectedItems, Is.Empty);
            var properties = host.Window.GetLogicalDescendants().OfType<Expander>()
                .Single(section => section.Name == "LayerProperties");
            Assert.That(((Control)properties.Content!).IsVisible, Is.False);
            list.SelectedIndex = 1;
            Assert.That(host.Field("TextContent").Text, Is.EqualTo(Host.AppliedText));
        }
        else
        {
            Assert.That(list.SelectedIndex, Is.Zero);
            Assert.That(host.Field("LayerName").Text, Is.EqualTo("第二图层"));
            Assert.That(host.Field("TextContent").Text, Is.EqualTo("另一图层的文字"));
            Assert.That(host.Field("LayerFontSize").Text, Is.EqualTo("54"));
        }
    }

    private sealed record Inputs(Dictionary<string, string?> Fields, int Caret, int Start, int End, bool? KeepAspect);

    private sealed class Host : IDisposable
    {
        public const string AppliedText = "已经应用的文字";
        private readonly FluentTheme _theme = new();
        private readonly string _folder;
        private readonly string _mode;
        private static readonly string[] InputNames =
        [
            "LayerName", "LayerFontSize", "LayerWidth", "LayerHeight", "TextContent",
            "CanvasWidth", "CanvasHeight", "BackgroundColor", "BackgroundBlur", "BackgroundDarkness"
        ];
        public EditSession Edit { get; }
        public NativeProjectWorkflow Native { get; }
        public MainWindow Window { get; }
        public Guid Id { get; }
        public Guid SecondId { get; }
        public string OriginalPath { get; }
        public string OutputPath { get; }
        public byte[]? OriginalBytes { get; }
        public Func<Task<string?>> PickPath { get; set; }
        public Layer Layer => Edit.Document.Find(Id);

        public Host(string mode)
        {
            _mode = mode;
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            _folder = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory,
                "save-drafts-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_folder);
            OriginalPath = Path.Combine(_folder, "original.ppgproj");
            OutputPath = mode == "in-place" ? OriginalPath : Path.Combine(_folder,
                mode == "temporary" ? "temporary.json" : "saved.ppgproj");
            var first = new Layer { Kind = LayerKind.Text, Name = "第一图层", Text = "最初的文字",
                FontSize = 54.12345678912345, Bounds = new(100.12345678912345, 100, 500.12345678912345, 200.98765432198765) };
            var second = new Layer { Kind = LayerKind.Text, Name = "第二图层", Text = "另一图层的文字",
                FontSize = 54, Bounds = new(800, 100, 500, 200) };
            Id = first.Id;
            SecondId = second.Id;
            Edit = new EditSession(new EditorDocument { Layers = [first, second] });
            Native = new NativeProjectWorkflow(Edit, document => document.Validate(), "save-draft-tests");
            if (mode != "untitled")
            {
                Native.SaveAs(OriginalPath);
                OriginalBytes = File.ReadAllBytes(OriginalPath);
            }
            Edit.UpdateLayer(Id, layer => layer.Text = AppliedText);
            Edit.UpdateLayer(Id, layer => layer.Name = "未保留的历史名称");
            Edit.Undo();
            Edit.SelectedLayerId = Id;
            PickPath = () => Task.FromResult<string?>(OutputPath);
            Window = new MainWindow(Edit, nativeWorkflow: Native, nativeSavePathPicker: () => PickPath(),
                filePathPicker: action => action == EditorFileAction.SaveTemporary ? PickPath()
                    : throw new AssertionException("Unexpected file action: " + action));
            Window.Show();
        }

        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>()
            .Single(field => field.Name == name);

        private CheckBox KeepAspect => Window.GetLogicalDescendants().OfType<CheckBox>()
            .Single(box => box.Name == "LayerKeepAspect");

        public void SetDrafts(bool invalid)
        {
            Assert.That(UiTestActions.Focus(Field("LayerFontSize")), Is.True);
            Field("LayerName").Text = "尚未应用的图层名称";
            Field("LayerFontSize").Text = invalid ? "NaN" : SizeDraft;
            Field("LayerWidth").Text = WidthDraft;
            Field("LayerHeight").Text = HeightDraft;
            KeepAspect.IsChecked = true;
            Field("CanvasWidth").Text = "尚未应用的非法宽度";
            Field("CanvasHeight").Text = "1200";
            Field("BackgroundColor").Text = "未应用的颜色";
            Field("BackgroundBlur").Text = "非法模糊值";
            Field("BackgroundDarkness").Text = "23.12345678912345";
            var text = Field("TextContent");
            text.Text = TextDraft;
            text.CaretIndex = TextDraft.Length;
            text.SelectionStart = TextDraft.Length - 5;
            text.SelectionEnd = TextDraft.Length - 1;
        }

        public Inputs CaptureInputs()
        {
            var text = Field("TextContent");
            return new(InputNames.ToDictionary(name => name, name => Field(name).Text),
                text.CaretIndex, text.SelectionStart, text.SelectionEnd, KeepAspect.IsChecked);
        }

        public void AssertInputs(Inputs inputs)
        {
            Assert.Multiple(() =>
            {
                foreach (var (name, value) in inputs.Fields)
                    Assert.That(Field(name).Text, Is.EqualTo(value), "Pending field " + name);
                var text = Field("TextContent");
                Assert.That(text.CaretIndex, Is.EqualTo(inputs.Caret));
                Assert.That(text.SelectionStart, Is.EqualTo(inputs.Start));
                Assert.That(text.SelectionEnd, Is.EqualTo(inputs.End));
                Assert.That(KeepAspect.IsChecked, Is.EqualTo(inputs.KeepAspect));
            });
        }

        public void Save()
        {
            if (_mode == "temporary") UiTestActions.Click(Window, "Save Temporary JSON");
            else Window.KeyPress(Key.S, _mode == "save-as"
                ? RawInputModifiers.Control | RawInputModifiers.Shift : RawInputModifiers.Control,
                PhysicalKey.None, null);
        }

        public EditorDocument ReadSaved() => _mode == "temporary"
            ? NativeDocumentStore.Read(OutputPath) : NativePackageStore.Read(OutputPath);

        public void Dispose()
        {
            Edit.MarkSaved();
            Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
            Directory.Delete(_folder, recursive: true);
        }
    }
}
