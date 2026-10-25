using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R1BatchStyleTests
{
    [Test]
    public void TextPatchChangesOnlyExplicitFieldsAndIsOneUndoStep()
    {
        var edit = TextSession();
        var ids = edit.Document.Layers.Select(layer => layer.Id).ToArray();
        var before = edit.Document.Copy();
        var colors = ids.Select(id => edit.Document.Find(id).ColorArgb).ToArray();
        var families = ids.Select(id => edit.Document.Find(id).FontFamily).ToArray();
        edit.ApplyLayerStyleBatch(ids, new LayerStylePatch(FontSize: 48, Bold: true));
        var after = edit.Document.Copy();
        Assert.Multiple(() =>
        {
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(ids.Select(id => edit.Document.Find(id).FontSize), Is.All.EqualTo(48));
            Assert.That(ids.Select(id => edit.Document.Find(id).Bold), Is.All.True);
            Assert.That(ids.Select(id => edit.Document.Find(id).ColorArgb), Is.EqualTo(colors));
            Assert.That(ids.Select(id => edit.Document.Find(id).FontFamily), Is.EqualTo(families));
            Assert.That(ids.Select(id => edit.Document.Find(id).Text), Is.EqualTo(before.Layers.Select(layer => layer.Text)));
            Assert.That(ids.Select(id => edit.Document.Find(id).Bounds), Is.EqualTo(before.Layers.Select(layer => layer.Bounds)));
        });
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
        edit.Redo();
        Assert.That(edit.Document.StateEquals(after), Is.True);
    }

    [Test]
    public void BadgePatchChangesDifficultyAndPositiveLevelWithoutBreakingAspectOrColors()
    {
        var badges = Enumerable.Range(0, 3).Select(index =>
        {
            var badge = EditorDocument.NewBadge(new PointD(100 + index * 180, 100));
            badge.Level = index + 1;
            badge.Difficulty = new[] { "EZ", "HD", "AT" }[index];
            return badge;
        }).ToArray();
        var edit = new EditSession(new EditorDocument { Layers = badges.ToList() });
        var ids = badges.Select(badge => badge.Id).ToArray();
        var before = edit.Document.Copy();
        edit.ApplyLayerStyleBatch(ids, new LayerStylePatch(Difficulty: "IN", Level: 16));
        foreach (var id in ids)
        {
            var badge = edit.Document.Find(id);
            Assert.That(badge.Difficulty, Is.EqualTo("IN"));
            Assert.That(badge.Level, Is.EqualTo(16));
            Assert.That(badge.Bounds.Width / badge.Bounds.Height,
                Is.EqualTo(EditorDocument.BadgeRatio).Within(1e-12));
            Assert.That(badge.KeepAspect, Is.True);
        }
        Assert.That(TestImages.RenderPng(edit.Document, true, 480), Is.Not.EqualTo(TestImages.RenderPng(before, true, 480)));
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
    }

    [Test]
    public void HiddenExplicitTargetsAreStyledWithoutBecomingVisible()
    {
        var hidden = new Layer { Kind = LayerKind.Text, Name = "hidden", Text = "隐藏文字",
            Visible = false, FontSize = 20, Bounds = new RectD(10, 10, 200, 60) };
        var visible = new Layer { Kind = LayerKind.Text, Name = "visible", Text = "可见文字",
            FontSize = 30, Bounds = new RectD(10, 100, 200, 60) };
        var edit = new EditSession(new EditorDocument { Layers = [hidden, visible] });
        edit.SetSelection([hidden.Id, visible.Id]);
        edit.ApplyLayerStyleBatch([hidden.Id, visible.Id], new LayerStylePatch(FontSize: 45, Italic: true));
        Assert.That(edit.Document.Find(hidden.Id).FontSize, Is.EqualTo(45));
        Assert.That(edit.Document.Find(hidden.Id).Italic, Is.True);
        Assert.That(edit.Document.Find(hidden.Id).Visible, Is.False);
        Assert.That(edit.Document.Find(visible.Id).FontSize, Is.EqualTo(45));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [TestCase("locked")]
    [TestCase("mixed")]
    [TestCase("invalid-size")]
    [TestCase("invalid-spacing")]
    [TestCase("invalid-font")]
    [TestCase("invalid-level")]
    [TestCase("invalid-difficulty")]
    public void RejectedBatchLeavesDocumentSelectionHistoryAndRedoUntouched(string failure)
    {
        var edit = TextSession();
        edit.UpdateLayer(edit.Document.Layers[0].Id, layer => layer.Name = "redo");
        edit.Undo();
        var ids = edit.Document.Layers.Select(layer => layer.Id).ToArray();
        if (failure == "locked") edit.UpdateLayer(ids[1], layer => layer.Locked = true);
        if (failure == "mixed")
        {
            var shape = new Layer { Kind = LayerKind.Shape, Name = "shape" };
            edit.UpdateDocument(document => document.Layers.Add(shape));
            ids = [.. ids, shape.Id];
        }
        var before = edit.Document.Copy();
        var selection = edit.SelectedLayerIds.ToArray();
        var history = (edit.UndoCount, edit.RedoCount, edit.IsDirty);
        var patch = failure switch
        {
            "invalid-size" => new LayerStylePatch(FontSize: 0),
            "invalid-spacing" => new LayerStylePatch(LineSpacing: 5),
            "invalid-font" => new LayerStylePatch(FontFamily: " "),
            "invalid-level" => new LayerStylePatch(Level: 0),
            "invalid-difficulty" => new LayerStylePatch(Difficulty: "XX"),
            _ => new LayerStylePatch(FontSize: 40)
        };
        Assert.That(() => edit.ApplyLayerStyleBatch(ids, patch), Throws.Exception);
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That((edit.UndoCount, edit.RedoCount, edit.IsDirty), Is.EqualTo(history));
    }

    [Test]
    public void NoOpPatchDoesNotCreateHistoryAndSaveReopenKeepsBatchPixels()
    {
        var edit = TextSession();
        var ids = edit.Document.Layers.Select(layer => layer.Id).ToArray();
        var first = edit.Document.Find(ids[0]);
        edit.ApplyLayerStyleBatch([ids[0]], new LayerStylePatch(FontSize: first.FontSize));
        Assert.That(edit.UndoCount, Is.Zero);
        edit.ApplyLayerStyleBatch(ids, new LayerStylePatch(FontSize: 58,
            Alignment: HorizontalAlignment.Center));
        var pixels = TestImages.RenderPng(edit.Document);
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "r1-batch-" + Guid.NewGuid() + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "r1-batch");
            var reopened = NativePackageStore.Read(path);
            Assert.That(TestImages.RenderPng(reopened), Is.EqualTo(pixels));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void DesktopBatchPanelUsesMixedPlaceholdersAndOnlyAppliesCheckedFields()
    {
        using var host = new Host();
        var fontSizes = host.Layers.Select(layer => layer.FontSize).ToArray();
        var colors = host.Layers.Select(layer => layer.ColorArgb).ToArray();
        Assert.That(host.Field("BatchFontSize").Text, Is.Empty);
        Assert.That(host.Field("BatchFontSize").PlaceholderText, Is.EqualTo("多个值"));
        var draft = host.Field("LayerName");
        draft.Text = "未应用的单层草稿";
        host.Check("BatchApplyFontSize");
        host.Field("BatchFontSize").Text = "52";
        UiTestActions.ClickNamed(host.Window, "ApplyBatchStyle");

        Assert.That(host.Edit.Document.Layers.Select(layer => layer.FontSize), Is.All.EqualTo(52));
        Assert.That(host.Edit.Document.Layers.Select(layer => layer.ColorArgb), Is.EqualTo(colors));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        Assert.That(draft.Text, Is.EqualTo("未应用的单层草稿"));
        host.Edit.Undo();
        Assert.That(host.Edit.Document.Layers.Select(layer => layer.FontSize), Is.EqualTo(fontSizes));
    }

    [Test]
    public void DesktopLockedOrMixedSelectionRejectsWithoutChangingDraftsOrHistory()
    {
        using var host = new Host();
        var before = host.Edit.Document.Copy();
        host.Edit.UpdateLayer(host.Layers[0].Id, layer => layer.Locked = true);
        host.WindowRefresh();
        var draft = host.Field("LayerName");
        draft.Text = "保留草稿";
        host.Check("BatchApplyFontSize");
        host.Field("BatchFontSize").Text = "44";
        UiTestActions.ClickNamed(host.Window, "ApplyBatchStyle");
        Assert.That(UiTestActions.Error(host.Window), Is.Not.Null);
        Assert.That(draft.Text, Is.EqualTo("保留草稿"));
        Assert.That(host.Edit.Document.Layers.All(layer => layer.FontSize != 44), Is.True);

        host.Edit.UpdateDocument(document => document.Layers.Add(
            new Layer { Kind = LayerKind.Shape, Name = "Shape" }));
        host.Edit.SetSelection([host.Layers[0].Id, host.Layers[1].Id, host.Edit.Document.Layers[^1].Id]);
        host.WindowRefresh();
        var apply = UiTestActions.FindNamed(host.Window, "ApplyBatchStyle");
        Assert.That(apply.IsEnabled, Is.False);
        Assert.That(host.Window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(block => block.Name == "BatchTargetInfo").Text, Does.Contain("仅支持同一类型"));
        Assert.That(host.Edit.Document.StateEquals(before), Is.False);
    }

    [Test]
    public void DesktopBatchContextChangeIsRejectedBeforeApplyingOldValues()
    {
        using var host = new Host();
        var beforeSizes = host.Edit.Document.Layers.Select(layer => layer.FontSize).ToArray();
        host.Check("BatchApplyFontSize");
        host.Field("BatchFontSize").Text = "57";
        host.Edit.UpdateDocument(document => document.BackgroundArgb = 0xFF123456);
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
        UiTestActions.ClickNamed(host.Window, "ApplyBatchStyle");
        Assert.That(UiTestActions.Error(host.Window), Does.Contain("目标已变化"));
        Assert.That(host.Edit.Document.Layers.Select(layer => layer.FontSize), Is.EqualTo(beforeSizes));
        Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
    }

    private static EditSession TextSession()
    {
        var layers = Enumerable.Range(0, 3).Select(index => new Layer
        {
            Kind = LayerKind.Text, Name = "Text " + index, Text = "正文 " + index,
            FontFamily = new[] { "Microsoft YaHei UI", "SimSun", "Arial" }[index],
            FontSize = 30 + index * 4,
            ColorArgb = new uint[] { 0xFFFFFFFF, 0xFFCF1313, 0xFF00FF00 }[index],
            Bounds = new RectD(50, 50 + index * 90, 500, 80)
        }).ToArray();
        return new EditSession(new EditorDocument { CanvasWidth = 960, CanvasHeight = 540, Layers = layers.ToList() });
    }

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public Layer[] Layers { get; }
        public Host()
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            Layers = Enumerable.Range(0, 2).Select(index => new Layer
            {
                Kind = LayerKind.Text, Name = "Text " + index, Text = "正文 " + index,
                FontSize = 30 + index * 4, ColorArgb = index == 0 ? 0xFFFFFFFF : 0xFFCF1313,
                Bounds = new RectD(50, 50 + index * 120, 500, 80)
            }).ToArray();
            Edit = new EditSession(new EditorDocument
            {
                CanvasWidth = 960, CanvasHeight = 540, Layers = Layers.ToList()
            });
            Edit.SetSelection(Layers.Select(layer => layer.Id));
            Window = new MainWindow(Edit);
            Window.Show();
        }
        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>()
            .Single(field => field.Name == name);
        public void Check(string name)
        {
            var check = Window.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == name);
            UiTestActions.Reveal(check);
            check.IsChecked = true;
        }
        public void WindowRefresh() => UiTestActions.ClickNamed(Window, "ApplyCanvasSettings");
        public void Dispose()
        {
            Edit.MarkSaved();
            Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
