using System.Diagnostics;
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

public sealed class ImportDraftLifecycleTests
{
    [Test]
    public void CancellingKeepsInputsSelectionAndSavePoint(
        [Values(false, true)] bool songs, [Values(false, true)] bool deferred, [Values(false, true)] bool dirty)
    {
        using var host = new Host(songs, dirty);
        var choice = new TaskCompletionSource<IReadOnlyList<string>>();
        if (deferred) host.Choose = () => choice.Task;
        host.SetDrafts();
        var inputs = host.Inputs();
        var before = host.Edit.Document.Copy();
        var selection = host.Edit.SelectedLayerIds.ToArray();
        var history = (host.Edit.UndoCount, host.Edit.RedoCount, host.Edit.IsDirty);
        var title = host.Window.Title;
        var savedBytes = File.ReadAllBytes(host.Native.CurrentPath!);

        host.Import();
        if (deferred)
        {
            Assert.That(host.Calls, Is.EqualTo(1));
            host.AssertInputs(inputs);
            choice.SetResult([]);
        }
        PumpUntil(() => UiTestActions.Status(host.Window)?.StartsWith("已取消", StringComparison.Ordinal) == true);

        Assert.Multiple(() =>
        {
            Assert.That(host.Calls, Is.EqualTo(1));
            Assert.That(UiTestActions.Error(host.Window), Is.Null);
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
            Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(selection));
            Assert.That((host.Edit.UndoCount, host.Edit.RedoCount, host.Edit.IsDirty), Is.EqualTo(history));
            Assert.That(host.Window.Title, Is.EqualTo(title));
            Assert.That(File.ReadAllBytes(host.Native.CurrentPath!), Is.EqualTo(savedBytes));
        });
        host.AssertInputs(inputs);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailedReadKeepsInputsAndAllowsAnotherPicker(bool songs)
    {
        using var host = new Host(songs, dirty: true);
        var corrupt = Path.Combine(host.Folder, songs ? "corrupt.png" : "corrupt.ppgproj");
        File.WriteAllText(corrupt, "invalid image or project");
        host.Choose = () => Task.FromResult<IReadOnlyList<string>>(songs ? [corrupt, corrupt, corrupt] : [corrupt]);
        host.SetDrafts();
        var inputs = host.Inputs();
        var before = host.Edit.Document.Copy();
        var history = (host.Edit.UndoCount, host.Edit.RedoCount, host.Edit.IsDirty);

        host.Import();
        PumpUntil(() => UiTestActions.Error(host.Window) is not null);

        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That((host.Edit.UndoCount, host.Edit.RedoCount, host.Edit.IsDirty), Is.EqualTo(history));
        host.AssertInputs(inputs);
        host.Choose = () => Task.FromResult<IReadOnlyList<string>>([]);
        host.Import();
        PumpUntil(() => UiTestActions.Status(host.Window)?.StartsWith("已取消", StringComparison.Ordinal) == true);
        Assert.That(host.Calls, Is.EqualTo(2));
        host.AssertInputs(inputs);
    }

    [TestCase(0)]
    [TestCase(3)]
    [TestCase(5)]
    public void RetryAfterCancellationImportsRefreshesAndRoundTrips(int songs)
    {
        using var host = new Host(songs > 0, dirty: false);
        host.SetDrafts();
        var before = host.Edit.Document.Copy();
        var undo = host.Edit.UndoCount;
        var currentPath = host.Native.CurrentPath;
        host.Import();
        PumpUntil(() => UiTestActions.Status(host.Window)?.StartsWith("已取消", StringComparison.Ordinal) == true);
        var source = Path.Combine(host.Folder, songs > 0 ? "自制曲绘.png" : "source.ppgproj");
        if (songs > 0) File.WriteAllBytes(source, TestImages.EncodePng(4, 4, TestImages.SyntheticBgra(4, 4)));
        else NativePackageStore.Save(source, new EditorDocument
        {
            Layers = [new Layer { Kind = LayerKind.Text, Name = "导入图层", Text = "可复用卡片" }]
        }, "test");
        host.Choose = () => Task.FromResult<IReadOnlyList<string>>(
            Enumerable.Repeat(source, songs > 0 ? songs : 1).ToArray());

        host.Import();
        PumpUntil(() => host.Edit.UndoCount > undo || UiTestActions.Error(host.Window) is not null);

        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Calls, Is.EqualTo(2));
        Assert.That(host.Native.CurrentPath, Is.EqualTo(currentPath));
        Assert.That(host.Edit.Document.Layers.Count, Is.EqualTo(before.Layers.Count + (songs > 0 ? songs * 6 : 1)));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo + 1));
        Assert.That(host.Edit.RedoCount, Is.Zero);
        Assert.That(host.Edit.IsDirty, Is.True);
        Assert.That(host.Window.Title, Does.Contain("*"));
        Assert.That(host.Edit.SelectedLayerIds, Is.EquivalentTo(host.Edit.Document.Layers.Skip(before.Layers.Count).Select(l => l.Id)));
        Assert.That(host.Field("LayerName").Text, Is.EqualTo(host.Edit.Document.Find(host.Edit.SelectedLayerId!.Value).Name));
        Assert.That(host.Field("CanvasWidth").Text, Is.EqualTo("640"));
        Assert.That(host.Field("CanvasHeight").Text, Is.EqualTo("360"));
        foreach (var layer in before.Layers)
            Assert.That(host.Edit.Document.Find(layer.Id).StateEquals(layer), Is.True);
        var after = host.Edit.Document.Copy();
        host.Edit.Undo();
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.IsDirty, Is.False);
        host.Edit.Redo();
        Assert.That(host.Edit.Document.StateEquals(after), Is.True);
        host.Native.SaveAs(Path.Combine(host.Folder, "saved.ppgproj"));
        var reopened = NativePackageStore.Read(host.Native.CurrentPath!);
        Assert.That(reopened.StateEquals(after), Is.True);
        Assert.That(TestImages.RenderPng(reopened, true), Is.EqualTo(TestImages.RenderPng(after, true)));
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var timer = Stopwatch.StartNew();
        while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(complete(), Is.True, "The import did not finish.");
        Dispatcher.UIThread.RunJobs();
    }

    private sealed record Drafts(Dictionary<string, string?> Fields, int Caret, int Start, int End);

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        private readonly bool _songs;
        public string Folder { get; } = Path.Combine(TestContext.CurrentContext.WorkDirectory, "import-drafts-" + Guid.NewGuid().ToString("N"));
        public EditSession Edit { get; }
        public NativeProjectWorkflow Native { get; }
        public MainWindow Window { get; }
        public int Calls { get; private set; }
        public Func<Task<IReadOnlyList<string>>> Choose { get; set; } = () => Task.FromResult<IReadOnlyList<string>>([]);

        public Host(bool songs, bool dirty)
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            Directory.CreateDirectory(Folder);
            _songs = songs;
            var text = new Layer { Kind = LayerKind.Text, Name = "已应用名称", Text = "已应用文字" };
            Edit = new EditSession(new EditorDocument { CanvasWidth = 640, CanvasHeight = 360,
                Layers = [new Layer { Kind = LayerKind.Shape }, text] });
            Native = new NativeProjectWorkflow(Edit, TestImages.ValidateImages, "test");
            Native.SaveAs(Path.Combine(Folder, "current.ppgproj"));
            Edit.UpdateLayer(text.Id, l => l.Text = "已提交内容");
            Edit.UpdateLayer(text.Id, l => l.ColorArgb = 0xFFCF1313);
            Edit.Undo();
            if (!dirty) Edit.MarkSaved();
            Edit.SetSelection([text.Id, Edit.Document.Layers[0].Id]);
            Window = new MainWindow(Edit, nativeWorkflow: Native,
                projectLayerPathPicker: async () => { Calls++; return (await Choose()).FirstOrDefault(); },
                songImagePathsPicker: () => { Calls++; return Choose(); });
            Window.Show();
        }

        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>().Single(f => f.Name == name);
        public void Import() => UiTestActions.Click(Window, _songs ? "Build 3 / 5 songs" : "Import project layers");

        public void SetDrafts()
        {
            Assert.That(UiTestActions.Focus(Field("LayerName")), Is.True);
            foreach (var name in new[] { "LayerName", "LayerFontSize", "LayerX", "LayerWidth", "LayerHeight",
                "CanvasWidth", "CanvasHeight", "BackgroundColor", "BackgroundBlur", "BackgroundDarkness" })
                Field(name).Text = "尚未应用：" + name;
            var text = Field("TextContent");
            text.Text = "尚未应用的中文第一行\n第二行保留光标与文字选区";
            text.CaretIndex = text.Text.Length;
            text.SelectionStart = 3;
            text.SelectionEnd = 8;
        }

        public Drafts Inputs()
        {
            var text = Field("TextContent");
            return new(Window.GetLogicalDescendants().OfType<TextBox>().Where(f => f.Name is not null)
                .ToDictionary(f => f.Name!, f => f.Text), text.CaretIndex, text.SelectionStart, text.SelectionEnd);
        }

        public void AssertInputs(Drafts inputs)
        {
            Assert.Multiple(() =>
            {
                foreach (var (name, value) in inputs.Fields) Assert.That(Field(name).Text, Is.EqualTo(value), name);
                var text = Field("TextContent");
                Assert.That(text.CaretIndex, Is.EqualTo(inputs.Caret));
                Assert.That(text.SelectionStart, Is.EqualTo(inputs.Start));
                Assert.That(text.SelectionEnd, Is.EqualTo(inputs.End));
            });
        }

        public void Dispose()
        {
            Edit.MarkSaved();
            Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
            Directory.Delete(Folder, recursive: true);
        }
    }
}
