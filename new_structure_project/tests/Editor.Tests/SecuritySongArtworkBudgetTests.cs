using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class SecuritySongArtworkBudgetTests
{
    [Test]
    public void DuplicatePendingRowsShareTheirBlobAndOneByteSnapshot()
    {
        var pending = new PendingSongArtwork(3);
        var first = pending.Replace(0, new AssetBlob([1, 2, 3]));
        for (var row = 1; row < 20; row++)
        {
            var repeated = pending.Replace(row, new AssetBlob([1, 2, 3]));
            Assert.That(repeated, Is.SameAs(first));
            Assert.That(repeated.Bytes, Is.SameAs(first.Bytes));
        }
        Assert.That(first.Bytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void ReplacingOneRowDropsItsOldPayloadBeforeAdmission()
    {
        var pending = new PendingSongArtwork(5);
        var original = pending.Replace(0, new AssetBlob([1, 2, 3]));
        var other = pending.Replace(1, new AssetBlob([4, 5]));
        var replacement = pending.Replace(0, new AssetBlob([6, 7, 8]));
        Assert.That(replacement, Is.Not.SameAs(original));
        Assert.That(replacement.Bytes, Is.EqualTo(new byte[] { 6, 7, 8 }));
        Assert.That(pending.Replace(1, new AssetBlob([4, 5])), Is.SameAs(other));
    }

    [Test]
    public void RejectedGrowthKeepsBothExistingPendingRows()
    {
        var pending = new PendingSongArtwork(5);
        var first = pending.Replace(0, new AssetBlob([1, 2, 3]));
        var second = pending.Replace(1, new AssetBlob([4, 5]));
        Assert.Throws<InvalidDataException>(() => pending.Replace(0, new AssetBlob([6, 7, 8, 9])));
        Assert.Throws<InvalidDataException>(() => pending.Replace(2, new AssetBlob([9])));
        Assert.That(pending.Replace(0, new AssetBlob([1, 2, 3])), Is.SameAs(first));
        Assert.That(pending.Replace(1, new AssetBlob([4, 5])), Is.SameAs(second));
    }

    [Test]
    public void PayloadStillOwnedByAnotherRowRemainsInTheBudget()
    {
        var pending = new PendingSongArtwork(5);
        var shared = pending.Replace(0, new AssetBlob([1, 2, 3]));
        pending.Replace(1, new AssetBlob([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => pending.Replace(0, new AssetBlob([4, 5, 6])));
        pending.Replace(0, new AssetBlob([4, 5]));
        Assert.That(pending.Replace(1, new AssetBlob([1, 2, 3])), Is.SameAs(shared));
    }

    [Test]
    public void PendingLoaderGateIsSharedAndClosedDialogsCannotAdmitData()
    {
        var pending = new PendingSongArtwork(5);
        Assert.That(pending.TryBeginLoad(), Is.True);
        Assert.That(pending.TryBeginLoad(), Is.False);
        pending.EndLoad();
        Assert.That(pending.TryBeginLoad(), Is.True);
        pending.Close();
        pending.EndLoad();
        Assert.That(pending.TryBeginLoad(), Is.False);
        Assert.Throws<InvalidOperationException>(() => pending.Replace(0, new AssetBlob([1])));
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    [TestCase(268_435_457L)]
    public void InvalidPendingBudgetsAreRejected(long maximum) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PendingSongArtwork(maximum));

    [Test]
    public void SongTransactionBudgetFailureRestoresTextRolesAssetsSelectionAndRedo()
    {
        var document = GroupDocument(1);
        var group = document.Layers[0].GroupId!.Value;
        var artwork = document.Layers.Single(layer => layer.SongRole == SongRole.Artwork);
        var title = document.Layers.Single(layer => layer.SongRole == SongRole.Title);
        var artist = document.Layers.Single(layer => layer.SongRole == SongRole.Artist);
        var charter = document.Layers.Single(layer => layer.SongRole == SongRole.Charter);
        var badge = document.Layers.Single(layer => layer.SongRole == SongRole.Badge);
        var mapping = new SongInfoMapping(group, artwork.Id, title.Id, artist.Id, charter.Id, badge.Id);
        foreach (var layer in document.Layers) layer.SongRole = SongRole.None;
        var edit = new EditSession(document, 8, 4);
        edit.UpdateLayer(title.Id, layer => layer.Text = "redo title");
        Assert.That(edit.Undo(), Is.True);
        edit.SetSelection([title.Id, badge.Id]);
        edit.MarkSaved();
        var before = edit.Document.Copy();
        var selected = edit.SelectedLayerIds.ToArray();
        var primary = edit.SelectedLayerId;
        var undo = edit.UndoCount;
        var redo = edit.RedoCount;
        var undoLabel = edit.UndoLabel;
        var redoLabel = edit.RedoLabel;
        var dirty = edit.IsDirty;
        var validations = 0;
        Assert.Throws<InvalidDataException>(() => edit.ApplySongInfoBatch([
            new SongInfoEdit(mapping, new SongInfoValues("new title", "new artist", "new charter", "AT", 99),
                [10, 11, 12, 13, 14])
        ], new SongTextStyle(FontSize: 48), _ => validations++));
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.StateEquals(before), Is.True);
            Assert.That(edit.Document.Layers.All(layer => layer.SongRole == SongRole.None), Is.True);
            Assert.That(edit.Document.Assets.All(pair => ReferenceEquals(pair.Value, before.Assets[pair.Key])), Is.True);
            Assert.That(edit.Document.Find(title.Id).Text, Is.EqualTo(before.Find(title.Id).Text));
            Assert.That(edit.Document.Find(artist.Id).Text, Is.EqualTo(before.Find(artist.Id).Text));
            Assert.That(edit.Document.Find(charter.Id).Text, Is.EqualTo(before.Find(charter.Id).Text));
            Assert.That(edit.Document.Find(badge.Id).Difficulty, Is.EqualTo(before.Find(badge.Id).Difficulty));
            Assert.That(edit.Document.Find(artwork.Id).AssetId, Is.EqualTo(before.Find(artwork.Id).AssetId));
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(selected));
            Assert.That(edit.SelectedLayerId, Is.EqualTo(primary));
            Assert.That(edit.UndoCount, Is.EqualTo(undo));
            Assert.That(edit.RedoCount, Is.EqualTo(redo));
            Assert.That(edit.UndoLabel, Is.EqualTo(undoLabel));
            Assert.That(edit.RedoLabel, Is.EqualTo(redoLabel));
            Assert.That(edit.IsDirty, Is.EqualTo(dirty));
            Assert.That(validations, Is.Zero, "admission fails before native image validation");
        });
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(title.Id).Text, Is.EqualTo("redo title"));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.StateEquals(before), Is.True);
    }

    [Test]
    public void WorkbenchBudgetFailureKeepsThePreviousReplacementPathAndDocument()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var document = GroupDocument(1);
        var before = document.Copy();
        var suggestions = SongInfoWorkbench.Suggest(document, document.Layers.Select(layer => layer.GroupId!.Value));
        var paths = new Queue<string>(["kept.png", "rejected.png"]);
        IReadOnlyList<SongInfoEdit>? applied = null;
        var dialog = new SongInfoWorkbenchDialog(document, suggestions,
            _ => Task.FromResult<string?>(paths.Dequeue()),
            path => Task.FromResult(new LoadedImage(new AssetBlob(path == "kept.png"
                ? [1, 2, 3] : [4, 5, 6, 7, 8, 9]), 1, 1)),
            (edits, _) => { applied = edits; return null; }, 5);
        var owner = new Window();
        try
        {
            owner.Show();
            var result = dialog.ShowDialog<bool?>(owner);
            dialog.UpdateLayout();
            UiTestActions.ClickNamed(dialog, "SongInfoReplace_0");
            PumpUntil(() => Block(dialog, "SongInfoReplacement_0").Text?.Contains("kept.png") == true);
            UiTestActions.ClickNamed(dialog, "SongInfoReplace_0");
            PumpUntil(() => Block(dialog, "SongInfoRowError_0").Text?.StartsWith("替换曲绘失败") == true);
            Assert.That(Block(dialog, "SongInfoReplacement_0").Text, Does.Contain("kept.png"));
            Assert.That(document.StateEquals(before), Is.True);
            dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "SongInfoConfirm_0").IsChecked = true;
            UiTestActions.ClickNamed(dialog, "ApplySongInfo");
            PumpUntil(() => result.IsCompleted);
            Assert.That(result.GetAwaiter().GetResult(), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(applied, Is.Not.Null);
                Assert.That(applied!.Single().ReplacementArtwork, Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(applied!.Single().ReplacementArtworkPath, Is.EqualTo(Path.GetFullPath("kept.png")));
                Assert.That(document.StateEquals(before), Is.True);
            });
        }
        finally
        {
            foreach (var window in owner.OwnedWindows.ToArray()) window.Close();
            owner.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void WorkbenchRunsOnlyOneLoaderAndDiscardsItsResultAfterCancel()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var document = GroupDocument(2);
        var before = document.Copy();
        var suggestions = SongInfoWorkbench.Suggest(document, document.Layers.Select(layer => layer.GroupId!.Value));
        var completion = new TaskCompletionSource<LoadedImage>();
        var choices = 0;
        var loads = 0;
        var dialog = new SongInfoWorkbenchDialog(document, suggestions,
            _ => { choices++; return Task.FromResult<string?>("pending.png"); },
            _ => { loads++; return completion.Task; },
            (_, _) => throw new AssertionException("Cancel must not apply song edits."), 5);
        var owner = new Window();
        try
        {
            owner.Show();
            var result = dialog.ShowDialog<bool?>(owner);
            dialog.UpdateLayout();
            UiTestActions.ClickNamed(dialog, "SongInfoReplace_0");
            UiTestActions.ClickNamed(dialog, "SongInfoReplace_1");
            Assert.That(choices, Is.EqualTo(1));
            Assert.That(loads, Is.EqualTo(1));
            UiTestActions.ClickNamed(dialog, "CancelSongInfo");
            PumpUntil(() => result.IsCompleted);
            completion.SetResult(new LoadedImage(new AssetBlob([1, 2, 3]), 1, 1));
            Dispatcher.UIThread.RunJobs();
            Assert.That(result.GetAwaiter().GetResult(), Is.False);
            Assert.That(document.StateEquals(before), Is.True);
            Assert.That(loads, Is.EqualTo(1));
        }
        finally
        {
            completion.TrySetResult(new LoadedImage(new AssetBlob([1, 2, 3]), 1, 1));
            Dispatcher.UIThread.RunJobs();
            foreach (var window in owner.OwnedWindows.ToArray()) window.Close();
            owner.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static TextBlock Block(Control dialog, string name) =>
        dialog.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Name == name);

    private static EditorDocument GroupDocument(int groups)
    {
        var document = new EditorDocument();
        for (var index = 0; index < groups; index++)
        {
            var group = Guid.NewGuid();
            var asset = Guid.NewGuid();
            document.AddAsset(asset, [1, 2, 3]);
            document.Layers.Add(new Layer { Kind = LayerKind.Image, GroupId = group, AssetId = asset,
                SongRole = SongRole.Artwork, Name = "曲绘" });
            foreach (var role in new[] { SongRole.Title, SongRole.Artist, SongRole.Charter })
                document.Layers.Add(new Layer { Kind = LayerKind.Text, GroupId = group,
                    SongRole = role, Name = role.ToString() });
            var badge = EditorDocument.NewBadge(new PointD(100, 100));
            badge.GroupId = group;
            badge.SongRole = SongRole.Badge;
            document.Layers.Add(badge);
        }
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
