using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R2SongGridTests
{
    [TestCase(1, 1)]
    [TestCase(1, 4)]
    [TestCase(2, 2)]
    [TestCase(2, 3)]
    [TestCase(4, 5)]
    public void CreatesConfiguredSixLayerSongCardsInCellOrder(int rows, int columns)
    {
        var settings = new SongGridSettings(rows, columns, 40, 24, 18, 12);
        var artworks = Enumerable.Range(0, rows * columns).Select(index =>
            new SongArtwork($"歌曲 {index}", new AssetBlob([(byte)(index + 1)]))).ToArray();
        var document = SongGrid.Create(2740, 1080, artworks, settings);
        document.Validate();
        Assert.That(document.Layers, Has.Count.EqualTo(rows * columns * 6));
        Assert.That(document.Layers.GroupBy(layer => layer.GroupId).Count(), Is.EqualTo(rows * columns));
        var template = SongGrid.CreateTemplate(2740, 1080, settings);
        var groups = document.Layers.GroupBy(layer => layer.GroupId)
            .OrderBy(group => group.Min(layer => layer.Bounds.X))
            .ThenBy(group => group.Min(layer => layer.Bounds.Y)).ToArray();
        foreach (var group in groups)
        {
            Assert.That(group.Select(layer => layer.Kind), Is.EqualTo(new[]
            {
                LayerKind.Shape, LayerKind.Image, LayerKind.Text,
                LayerKind.Text, LayerKind.Text, LayerKind.Badge
            }));
            var union = MoveSnapping.BoundsOf(group.Select(layer => layer.Bounds));
            Assert.That(template.Slots.Any(slot =>
                union.X >= slot.X * 2740 - 1e-7 && union.Y >= slot.Y * 1080 - 1e-7 &&
                union.X + union.Width <= (slot.X + slot.Width) * 2740 + 1e-7 &&
                union.Y + union.Height <= (slot.Y + slot.Height) * 1080 + 1e-7), Is.True);
        }
        Assert.That(document.Layers.Where(layer => layer.Kind == LayerKind.Text)
            .Select(layer => layer.Text), Does.Contain("歌曲 0"));
    }

    [TestCase(0, 1)]
    [TestCase(1, 0)]
    [TestCase(21, 1)]
    [TestCase(5, 5)]
    public void RejectsInvalidGridDimensionsAndMargins(int rows, int columns)
    {
        var settings = new SongGridSettings(rows, columns, -1, 0, 0, 0);
        Assert.That(() => settings.Validate(1000, 600), Throws.Exception);
        Assert.That(() => new SongGridSettings(rows, Math.Max(1, columns), 600, 20, 30, 30)
            .Validate(1000, 600), Throws.Exception);
    }

    [Test]
    public void WrongArtworkCountIsRejectedBeforeCreatingCards()
    {
        var settings = new SongGridSettings(2, 2, 20, 20, 10, 10);
        Assert.That(() => SongGrid.Create(1000, 600,
            [new SongArtwork("one", new AssetBlob([1]))], settings),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public async Task WorkflowAddsTheWholeGridAsOneUndoableImportAndProtectsSources()
    {
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [new Layer { Kind = LayerKind.Shape, Name = "existing" }]
        });
        var before = edit.Document.Copy();
        var sources = new ProtectedSourcePaths();
        var reads = 0;
        var workflow = new SongGridWorkflow(edit, path =>
        {
            reads++;
            return Task.FromResult(new LoadedImage(new AssetBlob([(byte)(reads + 2)]), 30, 20));
        }, _ => { }, sources, () => null);
        var paths = Enumerable.Range(0, 4).Select(index => $"song-{index}.png").ToArray();
        Assert.That(await workflow.ImportAsync(paths, new SongGridSettings(2, 2, 20, 20, 10, 10)),
            Is.EqualTo(4));
        var after = edit.Document.Copy();
        Assert.That(edit.Document.Layers, Has.Count.EqualTo(25));
        Assert.That(edit.SelectedLayerIds, Has.Count.EqualTo(24));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(after.Layers[0].StateEquals(before.Layers[0]), Is.True);
        foreach (var path in paths) Assert.That(sources.Contains(path), Is.True);
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
        edit.Redo();
        Assert.That(edit.Document.StateEquals(after), Is.True);
    }

    [TestCase("count")]
    [TestCase("limit")]
    [TestCase("last")]
    [TestCase("cancel")]
    public void WrongCountLimitFailureAndCancellationNeverPartiallyCommit(string failure)
    {
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 });
        if (failure == "limit")
            edit.Document.Layers.AddRange(Enumerable.Range(0, 190).Select(_ =>
                new Layer { Kind = LayerKind.Shape }));
        var before = edit.Document.Copy();
        var sources = new ProtectedSourcePaths();
        var reads = 0;
        var paths = failure == "count"
            ? new[] { "a.png", "b.png", "c.png" }
            : Enumerable.Range(0, 4).Select(index => $"song-{index}.png").ToArray();
        var workflow = new SongGridWorkflow(edit, path =>
        {
            reads++;
            if (failure == "last" && reads == 4) throw new IOException("locked");
            return Task.FromResult(new LoadedImage(new AssetBlob([(byte)(reads + 1)]), 20, 10));
        }, _ => { }, sources, () => null);
        using var cancel = new CancellationTokenSource();
        if (failure == "cancel") cancel.Cancel();
        Assert.That(async () => await workflow.ImportAsync(paths,
            new SongGridSettings(2, 2, 20, 20, 10, 10), cancellationToken: cancel.Token),
            Throws.Exception);
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(sources.Count, Is.Zero);
        if (failure is "count" or "limit" or "cancel") Assert.That(reads, Is.Zero);
    }

    [Test]
    public async Task GridSurvivesSaveReopenAndProducesTheSamePng()
    {
        var folder = Path.Combine(Path.GetTempPath(), "r2-grid-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var paths = Enumerable.Range(0, 4).Select(index =>
                Path.Combine(folder, $"曲绘 {index}.png")).ToArray();
            for (var index = 0; index < paths.Length; index++)
            {
                var width = 40 + index * 8;
                File.WriteAllBytes(paths[index], TestImages.EncodePng(width, 30,
                    TestImages.SyntheticBgra(width, 30)));
            }
            var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 });
            var workflow = new SongGridWorkflow(edit, path =>
            {
                var bytes = File.ReadAllBytes(path);
                var size = ImageAssetValidator.ReadEncodedDimensions(new AssetBlob(bytes));
                return Task.FromResult(new LoadedImage(new AssetBlob(bytes), size.Width, size.Height));
            }, TestImages.ValidateImages, new(), () => null);
            Assert.That(await workflow.ImportAsync(paths, new SongGridSettings(2, 2, 24, 24, 12, 12)),
                Is.EqualTo(4));
            var pixels = TestImages.RenderPng(edit.Document);
            var project = Path.Combine(folder, "grid.ppgproj");
            NativePackageStore.Save(project, edit.Document, "r2-grid");
            Assert.That(TestImages.RenderPng(NativePackageStore.Read(project)), Is.EqualTo(pixels));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
