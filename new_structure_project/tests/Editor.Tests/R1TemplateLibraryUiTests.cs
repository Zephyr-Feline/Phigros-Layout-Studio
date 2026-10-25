using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R1TemplateLibraryUiTests
{
    private string _root = null!;
    private string _libraryPath = null!;
    private string _external = null!;

    [SetUp]
    public void SetUp()
    {
        AvaloniaTestHost.EnsureStarted();
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "r1-template-ui-" + Guid.NewGuid().ToString("N"));
        _libraryPath = Path.Combine(_root, "library");
        _external = Path.Combine(_root, "external");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_external);
    }

    [TearDown]
    public void TearDown()
    {
        var expected = Path.GetFullPath(TestContext.CurrentContext.WorkDirectory) + Path.DirectorySeparatorChar;
        Assert.That(Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase), Is.True);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void LibraryReloadFilterPreviewCaptureAddAndRestartUseTheChineseEntries()
    {
        var library = new TemplateLibrary(_libraryPath);
        library.Add(Template("已有布局", 2));
        var edit = Session();
        using var host = new Host(edit, library);
        var list = host.List;
        var search = host.Search;
        var preview = host.Preview;
        Assert.That(list.Items.Cast<TemplateLibraryEntry>().Select(entry => entry.Name),
            Is.EqualTo(new[] { "已有布局" }));
        Assert.That(preview.Template, Is.Not.Null);
        Assert.That(preview.Template!.Slots.Count, Is.EqualTo(2));

        search.Text = "不存在";
        Dispatcher.UIThread.RunJobs();
        Assert.That(list.Items, Is.Empty);
        search.Text = "已有";
        Dispatcher.UIThread.RunJobs();
        Assert.That(list.Items, Has.Count.EqualTo(1));

        var draft = host.Window.GetLogicalDescendants().OfType<TextBox>()
            .Single(box => box.Name == "LayerName");
        draft.Text = "尚未应用的单层名称";
        UiTestActions.ClickNamed(host.Window, "CaptureLayoutTemplate");
        Assert.That(edit.UndoCount, Is.Zero);
        UiTestActions.ClickNamed(host.Window, "AddLayoutToLibrary");
        Assert.That(library.Load().Entries, Has.Count.EqualTo(2));
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(host.Status, Does.Contain("重启后仍可使用"));
        Assert.That(draft.Text, Is.EqualTo("尚未应用的单层名称"));

        var restarted = new TemplateLibrary(_libraryPath);
        Assert.That(restarted.Load().Entries.Select(entry => entry.Name),
            Is.EquivalentTo(new[] { "已有布局", "我的布局" }));
        var builtins = host.Window.GetLogicalDescendants().OfType<ComboBox>()
            .Single(box => box.Name == "LayoutTemplate").ItemsSource!
            .Cast<LayoutTemplate>().Select(template => template.Id).ToArray();
        Assert.That(builtins, Does.Contain("three-columns").And.Contain("five-point"));
        using var reopened = new Host(edit, restarted);
        Assert.That(reopened.List.Items, Has.Count.EqualTo(2));
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void ApplyLibraryTemplateIsOneUndoAndRedoAndKeepsTheLibraryCopy()
    {
        var library = new TemplateLibrary(_libraryPath);
        var entry = library.Add(Template("两槽布局", 2));
        var edit = Session();
        using var host = new Host(edit, library);
        host.Select(entry.Entry.Id);
        var before = edit.Document.Copy();
        var pixels = TestImages.RenderPng(edit.Document);

        UiTestActions.ClickNamed(host.Window, "ApplyLibraryTemplate");

        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(TestImages.RenderPng(edit.Document), Is.Not.EqualTo(pixels));
        Assert.That(library.Load().Entries.Single().Slots, Is.EqualTo(entry.Entry.Slots));
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
        edit.Redo();
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void ImportRenameExportAndRemoveThroughTheUiKeepTheOriginalFile()
    {
        var sourcePath = Path.Combine(_external, "外部模板.ppglayout");
        LayoutTemplateStore.Save(sourcePath, Template("外部模板", 2) with { Id = "outside-id" });
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var exportPath = Path.Combine(_external, "导出副本.ppglayout");
        var library = new TemplateLibrary(_libraryPath);
        var edit = Session();
        using var host = new Host(edit, library,
            action => Task.FromResult<string?>(action == EditorFileAction.SaveLayout ? exportPath : sourcePath));
        UiTestActions.Click(host.Window, "从文件导入到库");
        host.PumpUntil(() => library.Load().Entries.Count == 1);
        Assert.That(library.Load().Entries.Single().Name, Is.EqualTo("外部模板"));
        Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(sourceBytes));

        UiTestActions.Click(host.Window, "重命名库模板");
        host.PumpUntil(() => host.Window.OwnedWindows.OfType<NamePromptDialog>().Any());
        var rename = host.Window.OwnedWindows.OfType<NamePromptDialog>().Single();
        rename.GetLogicalDescendants().OfType<TextBox>().Single().Text = "改名的库模板";
        UiTestActions.ClickNamed(rename, "ConfirmNamePrompt");
        host.PumpUntil(() => library.Load().Entries.Single().Name == "改名的库模板");

        UiTestActions.Click(host.Window, "导出库模板");
        host.PumpUntil(() => File.Exists(exportPath));
        Assert.That(LayoutTemplateStore.Read(exportPath).Slots,
            Is.EqualTo(LayoutTemplateStore.Read(sourcePath).Slots));
        Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(sourceBytes));

        UiTestActions.Click(host.Window, "移除库模板");
        host.PumpUntil(() => host.Window.OwnedWindows.OfType<ConfirmDialog>().Any());
        var confirm = host.Window.OwnedWindows.OfType<ConfirmDialog>().Single();
        UiTestActions.ClickNamed(confirm, "AcceptConfirm");
        host.PumpUntil(() => library.Load().Entries.Count == 0);
        Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(sourceBytes));
        Assert.That(File.Exists(exportPath), Is.True);
    }

    private static EditSession Session()
    {
        var group = Guid.NewGuid();
        var art = new Layer { Kind = LayerKind.Shape, GroupId = group, Name = "art",
            Bounds = new RectD(50, 60, 300, 220) };
        var badge = EditorDocument.NewBadge(new PointD(250, 120));
        badge.GroupId = group; badge.Visible = false;
        var title = new Layer { Kind = LayerKind.Text, Name = "title", Text = "标题",
            Bounds = new RectD(420, 300, 260, 90) };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 960, CanvasHeight = 540, Layers = [art, badge, title]
        });
        edit.SetSelection([art.Id, title.Id]);
        return edit;
    }

    private static LayoutTemplate Template(string name, int slots) => new(
        Guid.NewGuid().ToString("N"), name,
        slots == 2
            ? [new RectD(.05, .1, .4, .7), new RectD(.55, .1, .4, .7)]
            : [new RectD(.1, .1, .5, .5)]);

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public ListBox List => Window.GetLogicalDescendants().OfType<ListBox>()
            .Single(list => list.Name == "TemplateLibraryList");
        public TextBox Search => Window.GetLogicalDescendants().OfType<TextBox>()
            .Single(box => box.Name == "TemplateLibrarySearch");
        public TemplatePreview Preview => Window.GetLogicalDescendants().OfType<TemplatePreview>().Single();
        public string? Status => UiTestActions.Status(Window);

        public Host(EditSession edit, TemplateLibrary library,
            Func<EditorFileAction, Task<string?>>? picker = null)
        {
            Avalonia.Application.Current!.Styles.Add(_theme);
            Edit = edit;
            Window = new MainWindow(edit, templateLibrary: library, filePathPicker: picker);
            Window.Show();
            Window.GetLogicalDescendants().OfType<Expander>()
                .Single(expander => expander.Name == "LayoutTemplates").IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public void Select(string id)
        {
            List.SelectedItem = List.Items.Cast<TemplateLibraryEntry>().Single(entry => entry.Id == id);
            Dispatcher.UIThread.RunJobs();
        }

        public void PumpUntil(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (!condition() && DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Assert.That(condition(), Is.True, "The UI operation did not complete.");
        }

        public void Dispose()
        {
            Edit.MarkSaved();
            foreach (var dialog in Window.OwnedWindows.ToArray()) dialog.Close();
            Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
