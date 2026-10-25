using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R2SongGridUiTests
{
    [Test]
    public void DialogPreviewsSlotsAndRequiresTheConfiguredFileCount()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var owner = new Window();
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        var paths = Enumerable.Range(0, 4).Select(index => $"图片 {index}.png").ToArray();
        var dialog = new SongGridDialog(document, _ => Task.FromResult<IReadOnlyList<string>>(paths));
        try
        {
            owner.Show();
            var pending = dialog.ShowDialog<SongGridRequest?>(owner);
            dialog.UpdateLayout();
            var preview = dialog.GetLogicalDescendants().OfType<TemplatePreview>().Single();
            Assert.That(preview.Template, Is.Not.Null);
            Assert.That(preview.Template!.Slots, Has.Count.EqualTo(4));
            Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "SongGridCount").Text, Does.Contain("4 首"));
            UiTestActions.ClickNamed(dialog, "ChooseSongGridImages");
            PumpUntil(() => dialog.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "SongGridFiles").Items.Count == 4);
            Assert.That(dialog.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "GenerateSongGrid").IsEnabled, Is.True);
            UiTestActions.ClickNamed(dialog, "GenerateSongGrid");
            PumpUntil(() => pending.IsCompleted);
            var request = pending.GetAwaiter().GetResult();
            Assert.That(request, Is.Not.Null);
            Assert.That(request!.Settings.Count, Is.EqualTo(4));
            Assert.That(request.Paths, Is.EqualTo(paths.Select(Path.GetFullPath)));
        }
        finally
        {
            foreach (var window in owner.OwnedWindows.ToArray()) window.Close();
            owner.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void DesktopGridEntryBuildsTheWholeBatchAsOneUndo()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var folder = Path.Combine(Path.GetTempPath(), "r2-grid-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 });
        var paths = Enumerable.Range(0, 4).Select(index =>
            Path.Combine(folder, $"曲绘 {index}.png")).ToArray();
        for (var index = 0; index < paths.Length; index++)
        {
            var width = 32 + index * 4;
            File.WriteAllBytes(paths[index], TestImages.EncodePng(width, 20,
                TestImages.SyntheticBgra(width, 20)));
        }
        var window = new MainWindow(edit,
            recovery: EmptyRecovery(edit),
            songGridPathsPicker: () => Task.FromResult<IReadOnlyList<string>>(paths));
        try
        {
            window.Show();
            UiTestActions.ClickNamed(window, "BuildSongGrid");
            PumpUntil(() => window.OwnedWindows.OfType<SongGridDialog>().Any());
            var dialog = window.OwnedWindows.OfType<SongGridDialog>().Single();
            UiTestActions.ClickNamed(dialog, "ChooseSongGridImages");
            PumpUntil(() => dialog.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "SongGridFiles").Items.Count == 4);
            UiTestActions.ClickNamed(dialog, "GenerateSongGrid");
            PumpUntil(() => edit.Document.Layers.Count == 24);
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.SelectedLayerIds, Has.Count.EqualTo(24));
            Assert.That(edit.Document.Layers.GroupBy(layer => layer.GroupId).Count(), Is.EqualTo(4));
            Assert.That(UiTestActions.Status(window), Does.Contain("4 首歌曲"));
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static AutoRecoveryService EmptyRecovery(EditSession edit) =>
        new(edit, Path.Combine(Path.GetTempPath(), "r2-grid-recovery-" + Guid.NewGuid().ToString("N")[..8]),
            "r2-test", () => DateTimeOffset.Now, (_, _) => false);

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
