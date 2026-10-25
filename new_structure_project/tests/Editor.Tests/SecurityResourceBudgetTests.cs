using System.IO.Compression;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class SecurityResourceBudgetTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Directory.CreateTempSubdirectory("ppg-resource-budget-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void PolicyCountsUniquePayloadsAndChecksLimitsWithoutLargeAllocations()
    {
        var one = new AssetBlob([1, 2, 3, 4]);
        var duplicate = new AssetBlob([1, 2, 3, 4]);
        var two = new AssetBlob([5, 6, 7]);
        Assert.Multiple(() =>
        {
            Assert.That(AssetResourceBudget.MaxDocumentBytes, Is.EqualTo(256L * 1024 * 1024));
            Assert.That(AssetResourceBudget.UniqueBytes([one, duplicate, two]), Is.EqualTo(7));
            Assert.That(AssetResourceBudget.AddUniqueBytes(4, 4, 8), Is.EqualTo(8));
            Assert.Throws<InvalidDataException>(() => AssetResourceBudget.AddUniqueBytes(4, 5, 8));
            Assert.Throws<InvalidDataException>(() => AssetResourceBudget.AddUniqueBytes(0,
                AssetResourceBudget.MaxAssetBytes + 1, AssetResourceBudget.MaxDocumentBytes));
        });
    }

    [Test]
    public void OverBudgetBatchStopsBeforeReadingTheNextFileAndPreservesAllSessionState()
    {
        var edit = SmallSession(10, [1, 2]);
        PrepareRedo(edit);
        var before = Capture(edit);
        var paths = Paths("first.png", "second.png", "never-read.png");
        var prior = Paths("already-protected.png")[0];
        var protectedPaths = new ProtectedSourcePaths();
        protectedPaths.Add(prior);
        var reads = new List<string>();
        var validations = 0;
        var workflow = new BatchImageImportWorkflow(edit, path =>
        {
            reads.Add(path);
            var bytes = reads.Count == 1 ? new byte[] { 3, 4, 5, 6 } : new byte[] { 7, 8, 9, 10, 11 };
            return Task.FromResult(new LoadedImage(new AssetBlob(bytes), 2, 2));
        }, _ => validations++, protectedPaths, () => null);

        Assert.That(async () => await workflow.ImportAsync(() =>
            Task.FromResult<IReadOnlyList<string>>(paths)), Throws.TypeOf<InvalidDataException>());
        AssertState(edit, before);
        Assert.Multiple(() =>
        {
            Assert.That(reads, Is.EqualTo(paths.Take(2)));
            Assert.That(validations, Is.Zero, "the budget fails during reading, before final validation or commit");
            Assert.That(protectedPaths.Count, Is.EqualTo(1));
            Assert.That(protectedPaths.Contains(prior), Is.True);
            Assert.That(paths.All(path => !protectedPaths.Contains(path)), Is.True);
            Assert.That(paths.All(path => File.ReadAllBytes(path).SequenceEqual(new byte[] { 42 })), Is.True);
        });
    }

    [Test]
    public async Task IndependentDuplicateBlobsCostOnceShareOneInstanceAndCommitOneUndo()
    {
        var edit = new EditSession(new EditorDocument(), 8, 4);
        var before = edit.Document.Copy();
        var paths = Paths("one.png", "two.png", "three.png");
        var protectedPaths = new ProtectedSourcePaths();
        var supplied = new List<AssetBlob>();
        var workflow = new BatchImageImportWorkflow(edit, _ =>
        {
            var blob = new AssetBlob([1, 2, 3, 4]);
            supplied.Add(blob);
            return Task.FromResult(new LoadedImage(blob, 2, 2));
        }, _ => { }, protectedPaths, () => null);

        Assert.That(await workflow.ImportAsync(() => Task.FromResult<IReadOnlyList<string>>(paths)), Is.EqualTo(3));
        var after = edit.Document.Copy();
        Assert.Multiple(() =>
        {
            Assert.That(supplied.Distinct().Count(), Is.EqualTo(3), "the loader did not pre-deduplicate its results");
            Assert.That(edit.Document.Assets, Has.Count.EqualTo(3));
            Assert.That(edit.Document.Assets.Values.All(blob => ReferenceEquals(blob, supplied[0])), Is.True);
            Assert.That(AssetResourceBudget.UniqueBytes(edit.Document.Assets.Values), Is.EqualTo(4));
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(edit.Document.Layers.Select(layer => layer.Id)));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.RedoCount, Is.Zero);
            Assert.That(protectedPaths.Count, Is.EqualTo(3));
        });
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.Undo(), Is.False);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.StateEquals(after), Is.True);
        Assert.That(edit.Document.Assets.Values.All(blob => ReferenceEquals(blob, supplied[0])), Is.True);
        Assert.That(protectedPaths.Count, Is.EqualTo(3), "successful source protection outlives undo");
    }

    [Test]
    public async Task DuplicateOfAnExistingPayloadDoesNotConsumeRemainingBudget()
    {
        var edit = SmallSession(4, [1, 2, 3, 4]);
        var existing = edit.Document.Assets.Values.Single();
        var workflow = new BatchImageImportWorkflow(edit, _ =>
            Task.FromResult(new LoadedImage(new AssetBlob([1, 2, 3, 4]), 2, 2)), _ => { }, new(), () => null);
        Assert.That(await workflow.ImportAsync(() => Task.FromResult<IReadOnlyList<string>>(Paths("duplicate.png"))), Is.EqualTo(1));
        Assert.That(edit.Document.Assets.Values.All(blob => ReferenceEquals(blob, existing)), Is.True);
        Assert.That(AssetResourceBudget.UniqueBytes(edit.Document.Assets.Values), Is.EqualTo(4));
    }

    [TestCase("single-add")]
    [TestCase("payload-batch")]
    [TestCase("single-replace")]
    [TestCase("fixed-background")]
    [TestCase("replace-asset")]
    [TestCase("update-document")]
    [TestCase("import-project")]
    public void EveryResourceAdmissionEntryRollsBackAnOverBudgetEdit(string entry)
    {
        var edit = SmallSession(8, [1, 2, 3, 4]);
        PrepareRedo(edit);
        var before = Capture(edit);
        var image = edit.Document.Layers.Single();
        var five = new AssetBlob([10, 11, 12, 13, 14]);
        var nine = new AssetBlob([20, 21, 22, 23, 24, 25, 26, 27, 28]);
        var replacementValidations = 0;
        Assert.Throws<InvalidDataException>(() =>
        {
            switch (entry)
            {
                case "single-add": edit.AddImageLayer(ImageLayer(), five); break;
                case "payload-batch":
                    edit.AddImagePayloadLayers([(ImageLayer(), new AssetBlob([40, 41])),
                        (ImageLayer(), new AssetBlob([50, 51, 52]))]);
                    break;
                case "single-replace": edit.ReplaceImageLayer(image.Id, nine); break;
                case "fixed-background": edit.SetFixedBackground(five); break;
                case "replace-asset":
                    edit.ReplaceAsset(image.AssetId!.Value, nine.ToArray(), _ => replacementValidations++);
                    break;
                case "update-document":
                    edit.UpdateDocument(document =>
                    {
                        var added = ImageLayer();
                        document.AddAsset(added.AssetId!.Value, five);
                        document.Layers.Add(added);
                    });
                    break;
                case "import-project": edit.ImportProjectLayers(ImageDocument(five)); break;
                default: throw new AssertionException("Unknown admission entry");
            }
        });
        AssertState(edit, before);
        Assert.That(replacementValidations, Is.Zero);
    }

    [TestCase(ImageImportAction.Add)]
    [TestCase(ImageImportAction.Replace)]
    [TestCase(ImageImportAction.Background)]
    public void SingleImportBudgetFailureKeepsSourceProtectionAndRedoUnchanged(ImageImportAction action)
    {
        var edit = SmallSession(8, [1, 2, 3, 4]);
        PrepareRedo(edit);
        var before = Capture(edit);
        var path = Paths("single.png")[0];
        var bytes = action == ImageImportAction.Replace
            ? new byte[] { 10, 11, 12, 13, 14, 15, 16, 17, 18 }
            : new byte[] { 10, 11, 12, 13, 14 };
        var protectedPaths = new ProtectedSourcePaths();
        var workflow = new ImageImportWorkflow(edit, _ =>
            Task.FromResult(new LoadedImage(new AssetBlob(bytes), 3, 3)), protectedPaths, () => null);
        Assert.That(async () => await workflow.ImportAsync(action, () => Task.FromResult<string?>(path)),
            Throws.TypeOf<InvalidDataException>());
        AssertState(edit, before);
        Assert.That(protectedPaths.Count, Is.Zero);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 42 }));
    }

    [Test]
    public void ExistingOverBudgetDocumentRemainsEditableButCannotGrowAfterReductions()
    {
        var original = new AssetBlob(Enumerable.Repeat((byte)1, 12).ToArray());
        var edit = new EditSession(ImageDocument(original), 8, 8);
        var image = edit.Document.Layers.Single();
        edit.UpdateLayer(image.Id, layer => layer.Name = "name remains editable");
        edit.UpdateDocument(document => document.BackgroundArgb = 0xFF123456);
        Assert.That(AssetResourceBudget.UniqueBytes(edit.Document.Assets.Values), Is.EqualTo(12));
        edit.ReplaceImageLayer(image.Id, new AssetBlob(Enumerable.Repeat((byte)2, 12).ToArray()));
        Assert.That(AssetResourceBudget.UniqueBytes(edit.Document.Assets.Values), Is.EqualTo(12));
        edit.ReplaceImageLayer(image.Id, new AssetBlob(Enumerable.Repeat((byte)3, 10).ToArray()));
        Assert.That(AssetResourceBudget.UniqueBytes(edit.Document.Assets.Values), Is.EqualTo(10));
        var before = Capture(edit);
        Assert.Throws<InvalidDataException>(() => edit.ReplaceImageLayer(image.Id,
            new AssetBlob(Enumerable.Repeat((byte)4, 11).ToArray())));
        AssertState(edit, before);
        Assert.Throws<InvalidDataException>(() => edit.AddImageLayer(ImageLayer(), new AssetBlob([99])));
        AssertState(edit, before);
    }

    [Test]
    public void ExistingOverBudgetPayloadMayBeReusedWithoutChargingForItsNewAssetId()
    {
        var existing = new AssetBlob(Enumerable.Repeat((byte)1, 12).ToArray());
        var edit = new EditSession(ImageDocument(existing), 8, 8);
        edit.AddImageLayer(ImageLayer(), new AssetBlob(existing.ToArray()));
        Assert.That(edit.Document.Assets, Has.Count.EqualTo(2));
        Assert.That(edit.Document.Assets.Values.All(blob => ReferenceEquals(blob, existing)), Is.True);
        Assert.That(AssetResourceBudget.UniqueBytes(edit.Document.Assets.Values), Is.EqualTo(12));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void ReplaceAssetCountsAndPreservesUnreferencedInventoryInsteadOfSilentlyCleaningIt()
    {
        var document = ImageDocument(new AssetBlob([1, 2, 3, 4]));
        var orphanId = Guid.NewGuid();
        var orphan = document.AddAsset(orphanId, [5, 6, 7]);
        var edit = new EditSession(document, 8, 8);
        var assetId = edit.Document.Layers.Single().AssetId!.Value;
        var before = Capture(edit);
        Assert.Throws<InvalidDataException>(() => edit.ReplaceAsset(assetId, [10, 11, 12, 13, 14, 15]));
        AssertState(edit, before);
        edit.ReplaceAsset(assetId, [20, 21, 22, 23, 24]);
        Assert.That(edit.Document.Assets[orphanId], Is.SameAs(orphan));
        Assert.That(AssetResourceBudget.UniqueBytes(edit.Document.Assets.Values), Is.EqualTo(8));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void AdmittedRealPngPayloadsSaveAndReopenAtTheExactUniqueByteBudget()
    {
        var bytes = TestImages.EncodePng(8, 6, TestImages.SyntheticBgra(8, 6));
        var edit = new EditSession(new EditorDocument(), 8, bytes.Length);
        edit.AddImagePayloadLayers([(ImageLayer(), new AssetBlob(bytes)), (ImageLayer(), new AssetBlob(bytes))]);
        var path = Path.Combine(_directory, "bounded.ppgproj");
        NativePackageStore.Save(path, edit.Document, "security-resource-budget");
        var reopened = NativePackageStore.Read(path);
        TestImages.ValidateImages(reopened);
        Assert.Multiple(() =>
        {
            Assert.That(reopened.Layers, Has.Count.EqualTo(2));
            Assert.That(reopened.Assets, Has.Count.EqualTo(2));
            Assert.That(AssetResourceBudget.UniqueBytes(reopened.Assets.Values), Is.EqualTo(bytes.Length));
            Assert.That(reopened.Assets.Values.All(blob => blob.ToArray().SequenceEqual(bytes)), Is.True);
        });
        using var archive = ZipFile.OpenRead(path);
        Assert.That(archive.Entries.Count(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal)),
            Is.EqualTo(1), "native persistence deduplicates the same payload shared by two asset IDs");
    }

    private EditSession SmallSession(long budget, byte[] bytes)
    {
        var edit = new EditSession(ImageDocument(new AssetBlob(bytes)), 8, budget);
        edit.SelectedLayerId = edit.Document.Layers.Single().Id;
        return edit;
    }

    private static Layer ImageLayer() => new()
    {
        Kind = LayerKind.Image, Name = "image", AssetId = Guid.NewGuid(), Bounds = new RectD(10, 10, 8, 6)
    };

    private static EditorDocument ImageDocument(AssetBlob image)
    {
        var layer = ImageLayer();
        var document = new EditorDocument { Layers = [layer] };
        document.AddAsset(layer.AssetId!.Value, image);
        return document;
    }

    private string[] Paths(params string[] names) => names.Select(name =>
    {
        var path = Path.Combine(_directory, name);
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
            Assert.That(edit.Document.StateEquals(before.Document), Is.True, "failed admission must leave document state intact");
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
