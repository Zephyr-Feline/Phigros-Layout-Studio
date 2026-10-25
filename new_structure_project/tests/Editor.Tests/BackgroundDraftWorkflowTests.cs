using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class BackgroundDraftWorkflowTests
{
    private const double PreciseDarkness = 0.12345678912345678;
    private const string TextDraft = "背景操作后仍待应用的第一行文字\n第二行完整保留";

    [TestCase(PreciseDarkness)]
    [TestCase(0.41176470588235287)]
    public void UnchangedBlendApplyPreservesExactDarknessAndTheRedoBranch(double darkness)
    {
        using var host = new Host(darkness: darkness);
        host.SetDrafts(includeBlend: false);
        var inputs = host.CaptureInputs(includeBlend: false);
        var before = host.Edit.Document.Copy();
        var undo = host.Edit.UndoCount;
        var redo = host.Edit.RedoCount;

        host.Action("Apply blend settings");

        Assert.Multiple(() =>
        {
            Assert.That(UiTestActions.Error(host.Window), Is.Null);
            Assert.That(host.Edit.Document.BackgroundBlend.Darkness, Is.EqualTo(darkness));
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
            Assert.That(host.Edit.UndoCount, Is.EqualTo(undo));
            Assert.That(host.Edit.RedoCount, Is.EqualTo(redo));
            Assert.That(host.Edit.IsDirty, Is.False);
            Assert.That(host.Window.Title, Does.Not.Contain("*"));
        });
        host.AssertInputs(inputs);
    }

    [TestCase(PreciseDarkness)]
    [TestCase(0.41176470588235287)]
    public void ChangingOnlyBlurPreservesTheUneditedDarknessAndOtherDrafts(double darkness)
    {
        using var host = new Host(darkness: darkness);
        host.SetDrafts(includeBlend: false);
        var inputs = host.CaptureInputs(includeBlend: false);
        var before = host.Edit.Document.Copy();
        var undo = host.Edit.UndoCount;
        host.Field("BackgroundBlur").Text = "12";

        host.Action("Apply blend settings");

        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.BackgroundBlend.Darkness, Is.EqualTo(darkness));
        Assert.That(host.Edit.Document.BackgroundBlend.BlurRadius, Is.EqualTo(12));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo + 1));
        Assert.That(host.Edit.RedoCount, Is.Zero);
        Assert.That(host.Edit.IsDirty, Is.True);
        Assert.That(host.Window.Title, Does.Contain("*"));
        host.AssertInputs(inputs);
        host.Action("Undo");
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.IsDirty, Is.False);
    }

    [Test]
    public void AnExplicitPercentageEditAppliesOnlyTheBlendSettings()
    {
        using var host = new Host();
        host.SetDrafts(includeBlend: false);
        var inputs = host.CaptureInputs(includeBlend: false);
        var before = host.Edit.Document.Copy();
        var undo = host.Edit.UndoCount;
        host.Field("BackgroundBlur").Text = "12";
        host.Field("BackgroundDarkness").Text = "65";

        host.Action("Apply blend settings");

        var expected = before.Copy();
        expected.BackgroundBlend = expected.BackgroundBlend with { BlurRadius = 12, Darkness = 0.65 };
        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.StateEquals(expected), Is.True);
        Assert.That(host.Field("BackgroundBlur").Text, Is.EqualTo("12"));
        Assert.That(host.Field("BackgroundDarkness").Text, Is.EqualTo("65"));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo + 1));
        Assert.That(host.Edit.RedoCount, Is.Zero);
        host.AssertInputs(inputs);
    }

    [TestCase("12", "NaN")]
    [TestCase("21", "65")]
    [TestCase("6", "101")]
    public void InvalidBlendInputsPreserveEveryInputAndTheCommittedState(string blur, string darkness)
    {
        using var host = new Host();
        host.SetDrafts(includeBlend: true);
        host.Field("BackgroundBlur").Text = blur;
        host.Field("BackgroundDarkness").Text = darkness;
        var inputs = host.CaptureInputs(includeBlend: true);
        var before = host.Edit.Document.Copy();
        var undo = host.Edit.UndoCount;
        var redo = host.Edit.RedoCount;

        host.Action("Apply blend settings");

        Assert.That(UiTestActions.Error(host.Window), Is.Not.Null);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo));
        Assert.That(host.Edit.RedoCount, Is.EqualTo(redo));
        Assert.That(host.Edit.IsDirty, Is.False);
        host.AssertInputs(inputs);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TogglingAutomaticBackgroundKeepsPendingBlendAndUnrelatedInputs(bool enabled)
    {
        using var host = new Host(enabled: enabled);
        host.SetDrafts(includeBlend: true);
        var inputs = host.CaptureInputs(includeBlend: true);
        var before = host.Edit.Document.Copy();
        var undo = host.Edit.UndoCount;

        host.Automatic.IsChecked = !enabled;

        var expected = before.Copy();
        expected.BackgroundBlend = expected.BackgroundBlend with { Enabled = !enabled };
        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.StateEquals(expected), Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo + 1));
        Assert.That(host.Edit.RedoCount, Is.Zero);
        Assert.That(host.Edit.IsDirty, Is.True);
        Assert.That(host.Window.Title, Does.Contain("*"));
        Assert.That(host.Source, Is.EqualTo(enabled ? "背景：画布底色" :
            "背景：自动 1 张曲绘（范围 1，过渡 0.5）"));
        host.AssertInputs(inputs);
    }

    [Test]
    public void ClearingAnAbsentFixedBackgroundKeepsInputsAndHistory()
    {
        using var host = new Host();
        host.SetDrafts(includeBlend: true);
        var inputs = host.CaptureInputs(includeBlend: true);
        var before = host.Edit.Document.Copy();
        var undo = host.Edit.UndoCount;
        var redo = host.Edit.RedoCount;

        host.Action("Clear background image");

        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo));
        Assert.That(host.Edit.RedoCount, Is.EqualTo(redo));
        Assert.That(host.Edit.IsDirty, Is.False);
        Assert.That(host.Source, Is.EqualTo("背景：画布底色"));
        host.AssertInputs(inputs);
    }

    [Test]
    public void RegeneratingIdenticalSnapshotPixelsKeepsInputsAndHistory()
    {
        using var host = new Host(snapshot: true);
        host.SetDrafts(includeBlend: true);
        var inputs = host.CaptureInputs(includeBlend: true);
        var before = host.Edit.Document.Copy();
        var undo = host.Edit.UndoCount;
        var redo = host.Edit.RedoCount;

        host.Action("Generate blended snapshot");

        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo));
        Assert.That(host.Edit.RedoCount, Is.EqualTo(redo));
        Assert.That(host.Edit.IsDirty, Is.False);
        Assert.That(host.Source, Is.EqualTo("背景：固定图片（优先使用）"));
        host.AssertInputs(inputs);
    }

    [Test]
    public void ClearingAnExistingSnapshotUpdatesTheBackgroundWithoutApplyingDrafts()
    {
        using var host = new Host(snapshot: true);
        host.SetDrafts(includeBlend: true);
        var inputs = host.CaptureInputs(includeBlend: true);
        var before = host.Edit.Document.Copy();
        var opaque = TestImages.RenderPng(before);
        var transparent = TestImages.RenderPng(before, true);
        var undo = host.Edit.UndoCount;

        host.Action("Clear background image");

        var expected = before.Copy();
        expected.FixedBackgroundAssetId = null;
        expected.RemoveUnreferencedAssets();
        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.StateEquals(expected), Is.True);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo + 1));
        Assert.That(host.Edit.RedoCount, Is.Zero);
        Assert.That(host.Edit.IsDirty, Is.True);
        Assert.That(host.Window.Title, Does.Contain("*"));
        Assert.That(host.Source, Is.EqualTo("背景：画布底色"));
        Assert.That(TestImages.RenderPng(host.Edit.Document), Is.Not.EqualTo(opaque));
        Assert.That(TestImages.RenderPng(host.Edit.Document, true), Is.EqualTo(transparent));
        host.AssertInputs(inputs);
    }

    [Test]
    public void GeneratingASnapshotUsesAppliedSettingsAndKeepsPendingInputs()
    {
        using var host = new Host();
        host.SetDrafts(includeBlend: true);
        var inputs = host.CaptureInputs(includeBlend: true);
        var before = host.Edit.Document.Copy();
        var expectedBytes = BackgroundComposer.Generate(before, TestImages.Decoder);
        var opaque = TestImages.RenderPng(before);
        var transparent = TestImages.RenderPng(before, true);
        var undo = host.Edit.UndoCount;

        host.Action("Generate blended snapshot");

        Assert.That(UiTestActions.Error(host.Window), Is.Null);
        Assert.That(host.Edit.Document.FixedBackgroundAssetId, Is.Not.Null);
        Assert.That(host.Edit.Document.Assets[host.Edit.Document.FixedBackgroundAssetId!.Value].ToArray(),
            Is.EqualTo(expectedBytes), "Pending blur, darkness and canvas inputs must not affect snapshot pixels.");
        Assert.That(host.Edit.Document.BackgroundBlend, Is.EqualTo(before.BackgroundBlend));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(undo + 1));
        Assert.That(host.Edit.RedoCount, Is.Zero);
        Assert.That(host.Edit.IsDirty, Is.True);
        Assert.That(host.Window.Title, Does.Contain("*"));
        Assert.That(host.Source, Is.EqualTo("背景：固定图片（优先使用）"));
        Assert.That(TestImages.RenderPng(host.Edit.Document), Is.Not.EqualTo(opaque));
        Assert.That(TestImages.RenderPng(host.Edit.Document, true), Is.EqualTo(transparent));
        host.AssertInputs(inputs);
    }

    [Test]
    public void ImportingAFixedBackgroundKeepsInputsInTheSameDocumentAndSelection()
    {
        var image = CreateImageFile();
        try
        {
            using var host = new Host(pathPicker: action =>
            {
                Assert.That(action, Is.EqualTo(EditorFileAction.BackgroundImage));
                return Task.FromResult<string?>(image.Path);
            });
            host.SetDrafts(includeBlend: true);
            var inputs = host.CaptureInputs(includeBlend: true);
            var before = host.Edit.Document.Copy();
            var undo = host.Edit.UndoCount;

            host.Action("Set background image");
            PumpUntil(() => host.Edit.Document.FixedBackgroundAssetId is not null || UiTestActions.Error(host.Window) is not null);

            Assert.That(UiTestActions.Error(host.Window), Is.Null);
            var id = host.Edit.Document.FixedBackgroundAssetId!.Value;
            var expected = before.Copy();
            expected.AddAsset(id, image.Bytes);
            expected.FixedBackgroundAssetId = id;
            Assert.That(host.Edit.Document.StateEquals(expected), Is.True);
            Assert.That(host.Edit.UndoCount, Is.EqualTo(undo + 1));
            Assert.That(host.Edit.RedoCount, Is.Zero);
            Assert.That(host.Edit.IsDirty, Is.True);
            Assert.That(host.Window.Title, Does.Contain("*"));
            Assert.That(host.Source, Is.EqualTo("背景：固定图片（优先使用）"));
            host.AssertInputs(inputs);
        }
        finally { Directory.Delete(image.Folder, recursive: true); }
    }

    [Test]
    public void ImportingABackgroundAfterASelectionChangeRefreshesTheActualContext()
    {
        var image = CreateImageFile();
        try
        {
            var picked = new TaskCompletionSource<string?>();
            using var host = new Host(pathPicker: action =>
            {
                Assert.That(action, Is.EqualTo(EditorFileAction.BackgroundImage));
                return picked.Task;
            });
            host.SetDrafts(includeBlend: true);
            var undo = host.Edit.UndoCount;
            var art = host.Edit.Document.Layers.Single(layer => layer.Kind == LayerKind.Image);

            host.Action("Set background image");
            Assert.That(host.Edit.Document.FixedBackgroundAssetId, Is.Null);
            // Change the session directly, without a list event that would refresh controls.
            host.Edit.SelectedLayerId = art.Id;
            picked.SetResult(image.Path);
            PumpUntil(() => host.Edit.Document.FixedBackgroundAssetId is not null || UiTestActions.Error(host.Window) is not null);

            Assert.Multiple(() =>
            {
                Assert.That(UiTestActions.Error(host.Window), Is.Null);
                Assert.That(host.Edit.Document.FixedBackgroundAssetId, Is.Not.Null);
                Assert.That(host.Edit.Document.Assets[host.Edit.Document.FixedBackgroundAssetId!.Value].ToArray(),
                    Is.EqualTo(image.Bytes));
                Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(new[] { art.Id }));
                Assert.That(host.Field("LayerName").Text, Is.EqualTo(art.Name));
                Assert.That(host.Field("LayerFontSize").Text, Is.EqualTo("36"));
                Assert.That(host.Field("CanvasWidth").Text, Is.EqualTo("640"));
                Assert.That(host.Field("CanvasHeight").Text, Is.EqualTo("360"));
                Assert.That(host.Field("BackgroundColor").Text, Is.EqualTo("FF233B47"));
                Assert.That(host.Field("BackgroundBlur").Text, Is.EqualTo("6"));
                Assert.That(host.Source, Is.EqualTo("背景：固定图片（优先使用）"));
                Assert.That(host.Edit.UndoCount, Is.EqualTo(undo + 1));
                Assert.That(host.Edit.RedoCount, Is.Zero);
                Assert.That(host.Edit.IsDirty, Is.True);
                Assert.That(host.Window.Title, Does.Contain("*"));
            });
            var list = host.Window.GetLogicalDescendants().OfType<ListBox>().Single();
            Assert.That(list.SelectedIndex, Is.EqualTo(1));
            list.SelectedIndex = 0;
            Assert.That(host.Field("LayerName").Text, Is.EqualTo("已应用名称"));
            Assert.That(host.Field("TextContent").Text, Is.EqualTo("已应用文字"));
            Assert.That(host.Field("LayerFontSize").Text, Is.EqualTo("54"));
        }
        finally { Directory.Delete(image.Folder, recursive: true); }
    }

    private static (string Folder, string Path, byte[] Bytes) CreateImageFile()
    {
        var folder = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "background-import-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "自制背景.png");
        var bytes = TestImages.EncodePng(6, 3, TestImages.SyntheticBgra(6, 3));
        File.WriteAllBytes(path, bytes);
        return (folder, path, bytes);
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var timer = Stopwatch.StartNew();
        while (!complete() && timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(complete(), Is.True, "The background image import did not complete.");
        Dispatcher.UIThread.RunJobs();
    }

    private sealed record Inputs(Dictionary<string, string?> Fields, int Caret, int Start, int End, Guid[] Selection);

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public CheckBox Automatic => Window.GetLogicalDescendants().OfType<CheckBox>()
            .Single(box => box.Name == "AutomaticBackground");
        public string? Source => Window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(block => block.Text?.StartsWith("背景：", StringComparison.Ordinal) == true).Text;

        public Host(double darkness = PreciseDarkness, bool enabled = false, bool snapshot = false,
            Func<EditorFileAction, Task<string?>>? pathPicker = null)
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            var text = new Layer { Kind = LayerKind.Text, Name = "最初名称", Text = "已应用文字",
                FontSize = 54, Bounds = new(300, 200, 240, 120) };
            var art = new Layer { Kind = LayerKind.Image, Name = "自制曲绘", AssetId = Guid.NewGuid(),
                Slanted = true, Bounds = new(40, 40, 240, 180) };
            var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360,
                BackgroundBlend = new(enabled, 6, darkness), Layers = [art, text] };
            document.AddAsset(art.AssetId!.Value, TestImages.EncodePng(4, 4, TestImages.SyntheticBgra(4, 4)));
            Edit = new EditSession(document);
            if (snapshot) Edit.SetFixedBackground(BackgroundComposer.Generate(Edit.Document, TestImages.Decoder));
            Edit.UpdateLayer(text.Id, layer => layer.Name = "已应用名称");
            Edit.UpdateLayer(text.Id, layer => layer.ColorArgb = 0xFFCF1313);
            Edit.Undo();
            Edit.MarkSaved();
            Edit.SelectedLayerId = text.Id;
            Window = new MainWindow(Edit, filePathPicker: pathPicker);
            Window.Show();
        }

        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>()
            .Single(field => field.Name == name);

        public void Action(string label) => UiTestActions.Click(Window, label);

        public void SetDrafts(bool includeBlend)
        {
            Assert.That(UiTestActions.Focus(Field("LayerName")), Is.True);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Field("LayerName").Text = "尚未应用的图层名称";
            Field("LayerFontSize").Text = "NaN";
            Field("LayerX").Text = "123.45678912345678";
            Field("CanvasWidth").Text = "未应用的非法宽度";
            Field("CanvasHeight").Text = "700";
            Field("BackgroundColor").Text = "未应用的颜色";
            if (includeBlend)
            {
                Field("BackgroundBlur").Text = "12";
                Field("BackgroundDarkness").Text = "65.12345678912345";
            }
            var text = Field("TextContent");
            text.Text = TextDraft;
            text.CaretIndex = TextDraft.Length;
            text.SelectionStart = TextDraft.Length - 5;
            text.SelectionEnd = TextDraft.Length - 1;
        }

        public Inputs CaptureInputs(bool includeBlend)
        {
            string[] fields = ["LayerName", "LayerFontSize", "LayerX", "TextContent",
                "CanvasWidth", "CanvasHeight", "BackgroundColor"];
            if (includeBlend) fields = [.. fields, "BackgroundBlur", "BackgroundDarkness"];
            var text = Field("TextContent");
            return new(fields.ToDictionary(name => name, name => Field(name).Text),
                text.CaretIndex, text.SelectionStart, text.SelectionEnd, Edit.SelectedLayerIds.ToArray());
        }

        public void AssertInputs(Inputs inputs)
        {
            Assert.Multiple(() =>
            {
                foreach (var (name, value) in inputs.Fields)
                    Assert.That(Field(name).Text, Is.EqualTo(value), "Unapplied field " + name);
                var text = Field("TextContent");
                Assert.That(text.CaretIndex, Is.EqualTo(inputs.Caret));
                Assert.That(text.SelectionStart, Is.EqualTo(inputs.Start));
                Assert.That(text.SelectionEnd, Is.EqualTo(inputs.End));
                Assert.That(Edit.SelectedLayerIds, Is.EqualTo(inputs.Selection));
            });
        }

        public void Dispose()
        {
            Edit.MarkSaved();
            Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
