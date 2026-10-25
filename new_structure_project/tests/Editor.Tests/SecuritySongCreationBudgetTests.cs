using Editor.Application;
using Editor.Core;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class SecuritySongCreationBudgetTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Directory.CreateTempSubdirectory("ppg-song-admission-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [TestCase("preset-3")]
    [TestCase("preset-5")]
    [TestCase("grid-3")]
    [TestCase("grid-5")]
    [TestCase("grid-20")]
    public void UniqueArtworkOverBudgetStopsBeforeTheThirdReadAndPreservesEverything(string kind)
    {
        var edit = Session(5);
        PrepareRedo(edit);
        var before = Capture(edit);
        var paths = Paths(Count(kind));
        var prior = Path.Combine(_directory, "prior.png");
        File.WriteAllBytes(prior, [100]);
        var sources = new ProtectedSourcePaths();
        sources.Add(prior);
        var reads = new List<string>();
        var validations = 0;
        Assert.That(async () => await Import(kind, edit, paths, path =>
        {
            reads.Add(path);
            return Task.FromResult(new LoadedImage(new AssetBlob([(byte)reads.Count, 90, 91]), 1, 1));
        }, _ => validations++, sources), Throws.TypeOf<InvalidDataException>());
        AssertState(edit, before);
        Assert.Multiple(() =>
        {
            Assert.That(reads, Is.EqualTo(paths.Take(2)));
            Assert.That(validations, Is.Zero, "admission rejects before candidate construction/validation");
            Assert.That(sources.Count, Is.EqualTo(1));
            Assert.That(sources.Contains(prior), Is.True);
            Assert.That(paths.All(path => !sources.Contains(path)), Is.True);
            Assert.That(paths.All(path => File.ReadAllBytes(path).SequenceEqual(new byte[] { 42 })), Is.True);
        });
    }

    [TestCase("preset-3")]
    [TestCase("preset-5")]
    [TestCase("grid-3")]
    [TestCase("grid-5")]
    [TestCase("grid-20")]
    public async Task IndependentlyLoadedIdenticalArtworkCostsOnceAndCommitsOneUndo(string kind)
    {
        var edit = Session(3);
        var before = edit.Document.Copy();
        var paths = Paths(Count(kind));
        var supplied = new List<AssetBlob>();
        var sources = new ProtectedSourcePaths();
        var validated = 0;
        Assert.That(await Import(kind, edit, paths, _ =>
        {
            var blob = new AssetBlob([1, 2, 3]);
            supplied.Add(blob);
            return Task.FromResult(new LoadedImage(blob, 1, 1));
        }, candidate =>
        {
            validated++;
            Assert.That(candidate.Assets.Values.Single(), Is.SameAs(supplied[0]));
        }, sources), Is.EqualTo(Count(kind)));
        var after = edit.Document.Copy();
        Assert.Multiple(() =>
        {
            Assert.That(supplied.Distinct().Count(), Is.EqualTo(Count(kind)), "the loader did not deduplicate inputs");
            Assert.That(validated, Is.EqualTo(1));
            Assert.That(edit.Document.Assets.Values.Single(), Is.SameAs(supplied[0]));
            Assert.That(AssetResourceBudget.UniqueBytes(edit.Document.Assets.Values), Is.EqualTo(3));
            Assert.That(edit.Document.Layers.Count(layer => layer.Kind == LayerKind.Image), Is.EqualTo(Count(kind)));
            Assert.That(edit.SelectedLayerIds, Has.Count.EqualTo(Count(kind) * 6));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.RedoCount, Is.Zero);
            Assert.That(sources.Count, Is.EqualTo(Count(kind)));
        });
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.Undo(), Is.False);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.StateEquals(after), Is.True);
        Assert.That(edit.Document.Assets.Values.Single(), Is.SameAs(supplied[0]));
        Assert.That(sources.Count, Is.EqualTo(Count(kind)), "source protection survives successful undo");
    }

    [TestCase("preset-3")]
    [TestCase("preset-5")]
    [TestCase("grid-3")]
    [TestCase("grid-5")]
    [TestCase("grid-20")]
    public async Task ExistingOverBudgetPayloadMayBeReusedWithoutFurtherGrowth(string kind)
    {
        var existing = new AssetBlob([1, 2, 3, 4, 5, 6]);
        var document = new EditorDocument();
        var assetId = Guid.NewGuid();
        document.AddAsset(assetId, existing);
        document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = assetId });
        var edit = new EditSession(document, 8, 3);
        Assert.That(await Import(kind, edit, Paths(Count(kind)), _ =>
            Task.FromResult(new LoadedImage(new AssetBlob(existing.ToArray()), 1, 1)),
            _ => { }, new()), Is.EqualTo(Count(kind)));
        Assert.Multiple(() =>
        {
            Assert.That(AssetResourceBudget.UniqueBytes(edit.Document.Assets.Values), Is.EqualTo(6));
            Assert.That(edit.Document.Assets.Values.Single(), Is.SameAs(existing));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
    }

    [TestCase("preset-3")]
    [TestCase("preset-5")]
    [TestCase("grid-3")]
    [TestCase("grid-5")]
    [TestCase("grid-20")]
    public async Task DistinctArtworkCanFillTheExactBudgetWithoutLosingOrder(string kind)
    {
        var count = Count(kind);
        var edit = Session(count * 2);
        var paths = Paths(count);
        var reads = 0;
        Assert.That(await Import(kind, edit, paths, _ =>
            Task.FromResult(new LoadedImage(new AssetBlob([(byte)++reads, 200]), 1, 1)),
            _ => { }, new()), Is.EqualTo(count));
        Assert.Multiple(() =>
        {
            Assert.That(reads, Is.EqualTo(count));
            Assert.That(AssetResourceBudget.UniqueBytes(edit.Document.Assets.Values), Is.EqualTo(count * 2));
            Assert.That(edit.Document.Layers.Where(layer => layer.Kind == LayerKind.Image)
                .Select(layer => edit.Document.Assets[layer.AssetId!.Value].ToArray()[0]),
                Is.EqualTo(Enumerable.Range(1, count).Select(value => (byte)value)));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
    }

    [TestCase("preset-3")]
    [TestCase("grid-20")]
    public void CandidateValidationFailureDoesNotCommitResourcesOrProtectAnyNewSource(string kind)
    {
        var edit = Session(3);
        PrepareRedo(edit);
        var before = Capture(edit);
        var sources = new ProtectedSourcePaths();
        Assert.That(async () => await Import(kind, edit, Paths(Count(kind)), _ =>
            Task.FromResult(new LoadedImage(new AssetBlob([1, 2, 3]), 1, 1)),
            _ => throw new InvalidDataException("bounded validator failure"), sources),
            Throws.TypeOf<InvalidDataException>().With.Message.EqualTo("bounded validator failure"));
        AssertState(edit, before);
        Assert.That(sources.Count, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GridCancellationBeforeOrAfterFirstReadPreservesAllSessionState(bool cancelDuringRead)
    {
        var edit = Session(3);
        PrepareRedo(edit);
        var before = Capture(edit);
        var sources = new ProtectedSourcePaths();
        var reads = 0;
        using var cancellation = new CancellationTokenSource();
        if (!cancelDuringRead) cancellation.Cancel();
        Assert.That(async () => await Import("grid-3", edit, Paths(3), _ =>
        {
            reads++;
            cancellation.Cancel();
            return Task.FromResult(new LoadedImage(new AssetBlob([1, 2, 3]), 1, 1));
        }, _ => throw new AssertionException("cancelled work cannot validate"), sources, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        AssertState(edit, before);
        Assert.That(reads, Is.EqualTo(cancelDuringRead ? 1 : 0));
        Assert.That(sources.Count, Is.Zero);
    }

    private static Task<int> Import(string kind, EditSession edit, IReadOnlyList<string> paths,
        Func<string, Task<LoadedImage>> load, Action<EditorDocument> validate,
        ProtectedSourcePaths sources, CancellationToken cancellation = default)
    {
        if (kind.StartsWith("preset-", StringComparison.Ordinal))
            return new SongBatchWorkflow(edit, async path => (await load(path)).Image, validate, sources)
                .ImportAsync(() => Task.FromResult(paths));
        var count = Count(kind);
        var settings = count == 20 ? new SongGridSettings(5, 4) : new SongGridSettings(1, count);
        return new SongGridWorkflow(edit, load, validate, sources, () => null)
            .ImportAsync(paths, settings, cancellationToken: cancellation);
    }

    private static int Count(string kind) => int.Parse(kind[(kind.IndexOf('-') + 1)..]);

    private static EditSession Session(long budget)
    {
        var existing = new Layer { Kind = LayerKind.Text, Name = "existing", Text = "existing" };
        return new EditSession(new EditorDocument { Layers = [existing] }, 8, budget)
            { SelectedLayerId = existing.Id };
    }

    private string[] Paths(int count) => Enumerable.Range(0, count).Select(index =>
    {
        var path = Path.Combine(_directory, $"artwork-{index + 1}.png");
        File.WriteAllBytes(path, [42]);
        return path;
    }).ToArray();

    private static void PrepareRedo(EditSession edit)
    {
        var selected = edit.Document.Layers.Single().Id;
        edit.UpdateLayer(selected, layer => layer.Name = "temporary name");
        Assert.That(edit.Undo(), Is.True);
        edit.SelectedLayerId = selected;
        edit.MarkSaved();
    }

    private sealed record SessionState(EditorDocument Document, Guid? Primary, Guid[] Selection,
        int Undo, int Redo, string? UndoLabel, string? RedoLabel, bool Dirty);

    private static SessionState Capture(EditSession edit) => new(edit.Document.Copy(), edit.SelectedLayerId,
        edit.SelectedLayerIds.ToArray(), edit.UndoCount, edit.RedoCount, edit.UndoLabel, edit.RedoLabel, edit.IsDirty);

    private static void AssertState(EditSession edit, SessionState before)
    {
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.StateEquals(before.Document), Is.True);
            Assert.That(edit.Document.Assets.All(pair => ReferenceEquals(pair.Value, before.Document.Assets[pair.Key])), Is.True);
            Assert.That(edit.SelectedLayerId, Is.EqualTo(before.Primary));
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(before.Selection));
            Assert.That(edit.UndoCount, Is.EqualTo(before.Undo));
            Assert.That(edit.RedoCount, Is.EqualTo(before.Redo));
            Assert.That(edit.UndoLabel, Is.EqualTo(before.UndoLabel));
            Assert.That(edit.RedoLabel, Is.EqualTo(before.RedoLabel));
            Assert.That(edit.IsDirty, Is.EqualTo(before.Dirty));
        });
    }
}
