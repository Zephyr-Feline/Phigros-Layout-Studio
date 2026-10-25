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

public sealed class R3SongStyleUiTests
{
    [Test]
    public void DesktopStyleLibraryAppliesInstantiatesCapturesRenamesAndRemoves()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3-style-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var library = new SongStyleLibrary(Path.Combine(root, "SongStyles"));
        var document = Document();
        var groupA = document.Layers.Single(layer => layer.SongRole == SongRole.Title &&
            layer.Name.StartsWith("A ", StringComparison.Ordinal)).GroupId!.Value;
        var groupB = document.Layers.Single(layer => layer.SongRole == SongRole.Title &&
            layer.Name.StartsWith("B ", StringComparison.Ordinal)).GroupId!.Value;
        library.Add(SongStyles.Capture(document, groupA, "已有样式"));
        var edit = new EditSession(document);
        edit.SetSelection(document.Layers.Where(layer => layer.GroupId == groupB)
            .Select(layer => layer.Id));
        var window = new MainWindow(edit, recovery: EmptyRecovery(edit),
            songStyleLibrary: library);
        try
        {
            window.Show();
            window.UpdateLayout();
            var side = window.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "OpenSongStyleLibrarySide");
            Assert.That(side.Content?.ToString(), Is.EqualTo("歌曲样式库…"));
            UiTestActions.ClickNamed(window, "OpenSongStyleLibrarySide");
            PumpUntil(() => window.OwnedWindows.OfType<SongStyleLibraryDialog>().Any());
            var dialog = window.OwnedWindows.OfType<SongStyleLibraryDialog>().Single();
            var list = dialog.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "SongStyleList");
            var preview = dialog.GetLogicalDescendants().OfType<SongStylePreview>().Single();
            Assert.That(list.Items, Has.Count.EqualTo(1));
            Assert.That(preview.Style, Is.Not.Null);
            Assert.That(preview.Style!.Name, Is.EqualTo("已有样式"));

            UiTestActions.ClickNamed(dialog, "ApplySongStyle");
            PumpUntil(() => edit.UndoCount == 1);
            Assert.That(edit.Document.Layers.Single(layer =>
                layer.GroupId == groupB && layer.SongRole == SongRole.Title).FontSize,
                Is.EqualTo(38.4).Within(1e-9));
            Assert.That(Status(dialog), Does.Contain("Ctrl+Z"));

            UiTestActions.ClickNamed(dialog, "InstantiateSongStyle");
            PumpUntil(() => edit.UndoCount == 2);
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(document.Layers.Count + 6));
            Assert.That(Status(dialog), Does.Contain("新 ID"));

            UiTestActions.ClickNamed(dialog, "CaptureSongStyle");
            PumpUntil(() => dialog.OwnedWindows.OfType<NamePromptDialog>().Any());
            var capturePrompt = dialog.OwnedWindows.OfType<NamePromptDialog>().Single();
            capturePrompt.GetLogicalDescendants().OfType<TextBox>().Single().Text = "界面捕获样式";
            UiTestActions.ClickNamed(capturePrompt, "ConfirmNamePrompt");
            PumpUntil(() => library.Load().Entries.Count == 2);
            Assert.That(list.Items.Cast<SongStyleLibraryEntry>()
                .Select(entry => entry.Name), Does.Contain("界面捕获样式"));

            UiTestActions.ClickNamed(dialog, "RenameSongStyle");
            PumpUntil(() => dialog.OwnedWindows.OfType<NamePromptDialog>().Any());
            var renamePrompt = dialog.OwnedWindows.OfType<NamePromptDialog>().Single();
            renamePrompt.GetLogicalDescendants().OfType<TextBox>().Single().Text = "界面改名样式";
            UiTestActions.ClickNamed(renamePrompt, "ConfirmNamePrompt");
            PumpUntil(() => library.Load().Entries.Any(entry => entry.Name == "界面改名样式"));

            UiTestActions.ClickNamed(dialog, "RemoveSongStyle");
            PumpUntil(() => dialog.OwnedWindows.OfType<ConfirmDialog>().Any());
            var confirm = dialog.OwnedWindows.OfType<ConfirmDialog>().Single();
            UiTestActions.ClickNamed(confirm, "AcceptConfirm");
            PumpUntil(() => library.Load().Entries.Count == 1);

            UiTestActions.ClickNamed(dialog, "CloseSongStyleLibrary");
            PumpUntil(() => !window.OwnedWindows.OfType<SongStyleLibraryDialog>().Any());
            PumpUntil(() => UiTestActions.Status(window)?.Contains("歌曲样式已应用",
                StringComparison.Ordinal) == true);
            Assert.That(new SongStyleLibrary(library.DirectoryPath).Load().Entries.Single().Name,
                Is.EqualTo("已有样式"));
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            var expected = Path.GetFullPath(TestContext.CurrentContext.WorkDirectory)
                + Path.DirectorySeparatorChar;
            Assert.That(Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase),
                Is.True);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void UnmarkedLegacyGroupKeepsTheDialogOpenAndMakesNoPartialChange()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3-style-ui-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var library = new SongStyleLibrary(Path.Combine(root, "SongStyles"));
        var source = Document();
        var groupA = source.Layers.Single(layer => layer.SongRole == SongRole.Title &&
            layer.Name.StartsWith("A ", StringComparison.Ordinal)).GroupId!.Value;
        library.Add(SongStyles.Capture(source, groupA, "有效样式"));

        var legacy = new EditorDocument { CanvasWidth = 800, CanvasHeight = 450 };
        var asset = Guid.NewGuid();
        legacy.AddAsset(asset, TestImages.EncodePng(20, 10, TestImages.SyntheticBgra(20, 10)));
        var legacyGroup = Guid.NewGuid();
        var legacyImage = new Layer
        {
            Kind = LayerKind.Image, Name = "旧工程 · artwork", AssetId = asset,
            GroupId = legacyGroup, Bounds = new RectD(20, 20, 200, 100)
        };
        legacy.Layers.Add(legacyImage);
        legacy.Validate();
        var edit = new EditSession(legacy);
        edit.SetSelection([legacyImage.Id]);
        var before = edit.Document.Copy();
        var window = new MainWindow(edit, recovery: EmptyRecovery(edit),
            songStyleLibrary: library);
        try
        {
            window.Show();
            UiTestActions.ClickNamed(window, "OpenSongStyleLibrarySide");
            PumpUntil(() => window.OwnedWindows.OfType<SongStyleLibraryDialog>().Any());
            var dialog = window.OwnedWindows.OfType<SongStyleLibraryDialog>().Single();
            UiTestActions.ClickNamed(dialog, "ApplySongStyle");
            Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "SongStyleLibraryError").Text,
                Does.Contain("持久角色"));
            Assert.That(edit.Document.StateEquals(before), Is.True);
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(window.OwnedWindows.OfType<SongStyleLibraryDialog>().Any(), Is.True);
            UiTestActions.ClickNamed(dialog, "CloseSongStyleLibrary");
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            var expected = Path.GetFullPath(TestContext.CurrentContext.WorkDirectory)
                + Path.DirectorySeparatorChar;
            Assert.That(Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase),
                Is.True);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string? Status(Window window) => window.GetLogicalDescendants()
        .OfType<TextBlock>().Single(block => block.Name == "SongStyleLibraryStatus").Text;

    private static EditorDocument Document()
    {
        var document = new EditorDocument { CanvasWidth = 1600, CanvasHeight = 900 };
        AddGroup(document, "A", 40, variant: 0);
        AddGroup(document, "B", 700, variant: 1);
        document.Validate();
        return document;
    }

    private static void AddGroup(EditorDocument document, string name, double x, int variant)
    {
        var group = Guid.NewGuid();
        var asset = Guid.NewGuid();
        document.AddAsset(asset, TestImages.EncodePng(40, 20,
            TestImages.SyntheticBgra(40, 20)));
        document.Layers.AddRange(
        [
            new Layer
            {
                Kind = LayerKind.Shape, Name = name + " · plate", GroupId = group,
                SongRole = SongRole.Plate, Bounds = new RectD(x - 5, 250, 410, 100),
                Slanted = true, ColorArgb = 0xCC102030
            },
            new Layer
            {
                Kind = LayerKind.Image, Name = name + " · artwork", GroupId = group,
                SongRole = SongRole.Artwork, AssetId = asset,
                Bounds = new RectD(x, 50, variant == 0 ? 400 : 320,
                    variant == 0 ? 225 : 180), Slanted = true, KeepAspect = true
            },
            new Layer
            {
                Kind = LayerKind.Text, Name = name + " · title", GroupId = group,
                SongRole = SongRole.Title, Text = "Title " + name,
                Bounds = new RectD(x, 280, 320, 40), FontSize = variant == 0 ? 48 : 30,
                FontFamily = variant == 0 ? "Arial" : "Microsoft YaHei UI"
            },
            new Layer
            {
                Kind = LayerKind.Text, Name = name + " · artist", GroupId = group,
                SongRole = SongRole.Artist, Text = "Artist " + name,
                Bounds = new RectD(x, 320, 320, 30), FontSize = 22,
                FontFamily = variant == 0 ? "Arial" : "Microsoft YaHei UI"
            },
            new Layer
            {
                Kind = LayerKind.Text, Name = name + " · charter", GroupId = group,
                SongRole = SongRole.Charter, Text = "Charter " + name,
                Bounds = new RectD(x, 350, 320, 28), FontSize = 20,
                FontFamily = variant == 0 ? "Arial" : "Microsoft YaHei UI"
            },
            NewBadge(x + 280, 40, group, name, variant)
        ]);
    }

    private static Layer NewBadge(double x, double y, Guid group, string name, int variant)
    {
        var badge = EditorDocument.NewBadge(new PointD(x, y));
        badge.Name = name + " · difficulty";
        badge.GroupId = group;
        badge.SongRole = SongRole.Badge;
        badge.Difficulty = variant == 0 ? "IN" : "HD";
        badge.Level = variant == 0 ? 15 : 12;
        return badge;
    }

    private static AutoRecoveryService EmptyRecovery(EditSession edit) =>
        new(edit, Path.Combine(Path.GetTempPath(),
            "r3-style-recovery-" + Guid.NewGuid().ToString("N")[..8]),
            "r3-test", () => DateTimeOffset.Now, (_, _) => false);

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(condition(), Is.True, "The UI operation did not complete.");
    }
}
