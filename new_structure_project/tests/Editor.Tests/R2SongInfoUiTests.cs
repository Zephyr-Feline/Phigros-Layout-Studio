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

public sealed class R2SongInfoUiTests
{
    [Test]
    public void RowRequiresConfirmationAndCanApplyFieldsAndReplacementArtwork()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var owner = new Window();
        var document = GroupDocument();
        var group = document.Layers[0].GroupId!.Value;
        var suggestion = SongInfoWorkbench.Suggest(document, [group]).Single();
        IReadOnlyList<SongInfoEdit>? applied = null;
        SongTextStyle? appliedStyle = null;
        var dialog = new SongInfoWorkbenchDialog(document, [suggestion],
            _ => Task.FromResult<string?>("replacement.png"),
            _ => Task.FromResult(new LoadedImage(
                new AssetBlob(TestImages.EncodePng(12, 8, TestImages.SyntheticBgra(12, 8))),
                12, 8)),
            (edits, style) => { applied = edits; appliedStyle = style; return null; });
        try
        {
            owner.Show();
            var pending = dialog.ShowDialog<bool?>(owner);
            dialog.UpdateLayout();
            UiTestActions.ClickNamed(dialog, "ApplySongInfo");
            Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "SongInfoError").Text, Does.Contain("尚未确认"));
            Assert.That(pending.IsCompleted, Is.False);

            dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "SongInfoConfirm_0").IsChecked = true;
            dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "SongInfoTitleText_0").Text = "界面编辑标题";
            Dispatcher.UIThread.RunJobs();
            Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "SongInfoRowPreview_0").Text, Does.Contain("界面编辑标题"));
            UiTestActions.ClickNamed(dialog, "SongInfoReplace_0");
            PumpUntil(() => dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "SongInfoReplacement_0").Text?
                .Contains("replacement.png", StringComparison.Ordinal) == true);
            dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "SongInfoApplyFontSize").IsChecked = true;
            dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "SongInfoCommonFontSize").Text = "48";
            UiTestActions.ClickNamed(dialog, "ApplySongInfo");
            PumpUntil(() => pending.IsCompleted);
            Assert.That(pending.GetAwaiter().GetResult(), Is.True);
            Assert.That(applied, Is.Not.Null);
            var editResult = applied!.Single();
            Assert.That(editResult.Values.Title, Is.EqualTo("界面编辑标题"));
            Assert.That(editResult.ReplacementArtwork, Is.Not.Null);
            Assert.That(editResult.ReplacementArtworkPath, Is.EqualTo(Path.GetFullPath("replacement.png")));
            Assert.That(appliedStyle!.FontSize, Is.EqualTo(48));
        }
        finally
        {
            foreach (var window in owner.OwnedWindows.ToArray()) window.Close();
            owner.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void DesktopWorkbenchConfirmsMappingAndAppliesAllRowsOnce()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var document = GroupDocument();
        var group = document.Layers[0].GroupId!.Value;
        var edit = new EditSession(document);
        edit.SetSelection(document.Layers.Where(layer => layer.GroupId == group).Select(layer => layer.Id));
        var selection = edit.SelectedLayerIds.ToArray();
        var window = new MainWindow(edit, recovery: EmptyRecovery(edit));
        try
        {
            window.Show();
            UiTestActions.ClickNamed(window, "OpenSongInfoWorkbench");
            PumpUntil(() => window.OwnedWindows.OfType<SongInfoWorkbenchDialog>().Any());
            var dialog = window.OwnedWindows.OfType<SongInfoWorkbenchDialog>().Single();
            dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "SongInfoConfirm_0").IsChecked = true;
            dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "SongInfoTitleText_0").Text = "主窗口批量标题";
            dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "SongInfoLevel_0").Text = "16";
            UiTestActions.ClickNamed(dialog, "ApplySongInfo");
            PumpUntil(() => edit.Document.Layers.Any(layer =>
                layer.Name.EndsWith("· title", StringComparison.Ordinal) &&
                layer.Text == "主窗口批量标题"));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection));
            Assert.That(edit.Document.Layers.Single(layer => layer.Kind == LayerKind.Badge).Level,
                Is.EqualTo(16));
            Assert.That(edit.Document.Layers.Single(layer =>
                layer.Name.EndsWith("· title", StringComparison.Ordinal)).SongRole,
                Is.EqualTo(SongRole.Title));
            PumpUntil(() => UiTestActions.Status(window)?.Contains("已更新", StringComparison.Ordinal) == true);
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void LockedGroupErrorStaysInTheDialogWithoutChangingTheDocument()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var document = GroupDocument();
        var group = document.Layers[0].GroupId!.Value;
        foreach (var layer in document.Layers.Where(layer => layer.GroupId == group)) layer.Locked = true;
        var edit = new EditSession(document);
        edit.SetSelection(document.Layers.Where(layer => layer.GroupId == group).Select(layer => layer.Id));
        var before = edit.Document.Copy();
        var window = new MainWindow(edit, recovery: EmptyRecovery(edit));
        try
        {
            window.Show();
            UiTestActions.ClickNamed(window, "OpenSongInfoWorkbench");
            PumpUntil(() => window.OwnedWindows.OfType<SongInfoWorkbenchDialog>().Any());
            var dialog = window.OwnedWindows.OfType<SongInfoWorkbenchDialog>().Single();
            dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "SongInfoConfirm_0").IsChecked = true;
            UiTestActions.ClickNamed(dialog, "ApplySongInfo");
            Assert.That(window.OwnedWindows.OfType<SongInfoWorkbenchDialog>().Any(), Is.True);
            Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "SongInfoError").Text, Does.Contain("锁定"));
            Assert.That(edit.Document.StateEquals(before), Is.True);
            Assert.That(edit.UndoCount, Is.Zero);
            UiTestActions.ClickNamed(dialog, "CancelSongInfo");
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static AutoRecoveryService EmptyRecovery(EditSession edit) =>
        new(edit, Path.Combine(Path.GetTempPath(), "r2-info-recovery-" + Guid.NewGuid().ToString("N")[..8]),
            "r2-test", () => DateTimeOffset.Now, (_, _) => false);

    private static EditorDocument GroupDocument()
    {
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        var group = Guid.NewGuid();
        var asset = Guid.NewGuid();
        document.AddAsset(asset, TestImages.EncodePng(20, 10, TestImages.SyntheticBgra(20, 10)));
        var artwork = new Layer { Kind = LayerKind.Image, Name = "歌曲 · artwork",
            GroupId = group, AssetId = asset, Bounds = new RectD(40, 40, 200, 120) };
        var title = new Layer { Kind = LayerKind.Text, Name = "歌曲 · title", Text = "原标题",
            GroupId = group, Bounds = new RectD(40, 180, 240, 40) };
        var artist = new Layer { Kind = LayerKind.Text, Name = "歌曲 · artist", Text = "原曲师",
            GroupId = group, Bounds = new RectD(40, 220, 240, 35) };
        var charter = new Layer { Kind = LayerKind.Text, Name = "歌曲 · charter", Text = "原谱师",
            GroupId = group, Bounds = new RectD(40, 255, 240, 30) };
        var badge = EditorDocument.NewBadge(new PointD(260, 40));
        badge.Name = "歌曲 · difficulty";
        badge.GroupId = group;
        document.Layers.AddRange([artwork, title, artist, charter, badge]);
        document.Validate();
        return document;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(condition(), Is.True, "UI operation did not complete.");
    }
}
