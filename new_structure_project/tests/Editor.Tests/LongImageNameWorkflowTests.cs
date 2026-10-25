using System.Text;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class LongImageNameWorkflowTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        AvaloniaTestHost.EnsureStarted();
        // Long file components belong in a short external directory so these cases do not
        // accidentally exercise Windows' total-path limit instead of the layer-name limit.
        _directory = Path.Combine(Path.GetTempPath(), "lin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [TestCase(200)]
    [TestCase(201)]
    public async Task ARealPngAtOrBeyondTheNameLimitImportsWithoutChangingItsSource(int fileNameLength)
    {
        var fileName = new string('a', fileNameLength - 4) + ".png";
        await AssertSuccessfulAdd(fileName, fileNameLength == 200 ? fileName : fileName[..200]);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TruncationKeepsACompleteUnicodeScalarAtTheTwoHundredUnitBoundary(bool pairFits)
    {
        var prefix = new string('a', pairFits ? 198 : 199);
        const string scalar = "\U0001F3B5";
        var fileName = prefix + scalar + "尾.png";
        var expected = pairFits ? prefix + scalar : prefix;

        var added = await AssertSuccessfulAdd(fileName, expected);

        Assert.DoesNotThrow(() => new UTF8Encoding(false, true).GetBytes(added.Name),
            "the automatically generated name must not end with an unpaired high surrogate");
        Assert.That(added.Name.Length, Is.EqualTo(pairFits ? 200 : 199));
    }

    [TestCase("read")]
    [TestCase("layer-limit")]
    public void AFailedLongNameImportKeepsTheDocumentSelectionRedoAndSourcesAtomic(string failure)
    {
        var source = CreateSource(new string('b', 197) + ".png");
        var edit = Session(failure == "layer-limit" ? 200 : 1);
        SeedRedo(edit);
        var before = edit.Document.Copy();
        var selection = edit.SelectedLayerIds.ToArray();
        var sources = new ProtectedSourcePaths();
        var workflow = new ImageImportWorkflow(edit, LoadImage, sources, () => null);
        Task<bool> Import() => workflow.ImportAsync(ImageImportAction.Add, () => Task.FromResult<string?>(source.Path));

        if (failure == "read")
        {
            using var locked = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.ThrowsAsync<IOException>(Import);
        }
        else Assert.ThrowsAsync<InvalidDataException>(Import);

        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.StateEquals(before), Is.True);
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection));
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.RedoCount, Is.EqualTo(1));
            Assert.That(edit.IsDirty, Is.False);
            Assert.That(sources.Count, Is.Zero);
        });
        AssertSourceUnchanged(source);
    }

    [TestCase(ImageImportAction.Replace)]
    [TestCase(ImageImportAction.Background)]
    public async Task ALongSourceNameDoesNotRenameAnExistingLayerWhenReplacingOrSettingTheBackground(ImageImportAction action)
    {
        var source = CreateSource(new string('c', 197) + ".png");
        var assetId = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = assetId, Name = "保留用户的名称 \U0001F3B5",
            GroupId = Guid.NewGuid(), Bounds = new(40, 60, 120, 60), KeepAspect = true, RotationDegrees = 17 };
        var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 480, Layers = [image] };
        document.AddAsset(assetId, TestImages.EncodePng(12, 6, TestImages.SyntheticBgra(12, 6)));
        var edit = new EditSession(document) { SelectedLayerId = image.Id };
        SeedRedo(edit);
        var before = edit.Document.Copy();
        var sources = new ProtectedSourcePaths();
        var workflow = new ImageImportWorkflow(edit, LoadImage, sources, () => null);

        Assert.That(await workflow.ImportAsync(action, () => Task.FromResult<string?>(source.Path)), Is.True);

        var afterImage = edit.Document.Find(image.Id);
        Assert.That(afterImage.Name, Is.EqualTo(image.Name));
        var comparable = afterImage.Copy();
        comparable.AssetId = image.AssetId;
        Assert.That(comparable.StateEquals(image), Is.True, "file names cannot change existing geometry, style or identity");
        if (action == ImageImportAction.Replace)
            Assert.That(edit.Document.Assets[afterImage.AssetId!.Value].ToArray(), Is.EqualTo(source.Bytes));
        else
        {
            Assert.That(afterImage.StateEquals(image), Is.True);
            Assert.That(edit.Document.FixedBackgroundAssetId, Is.Not.Null);
            Assert.That(edit.Document.Assets[edit.Document.FixedBackgroundAssetId!.Value].ToArray(), Is.EqualTo(source.Bytes));
        }
        Assert.That(sources.Contains(source.Path), Is.True);
        AssertHistoryAndNativeReopen(edit, before);
        AssertSourceUnchanged(source);
    }

    private async Task<Layer> AssertSuccessfulAdd(string fileName, string expectedName)
    {
        var source = CreateSource(fileName);
        var edit = Session();
        SeedRedo(edit);
        var before = edit.Document.Copy();
        var sources = new ProtectedSourcePaths();
        var workflow = new ImageImportWorkflow(edit, LoadImage, sources, () => null);

        Assert.That(await workflow.ImportAsync(ImageImportAction.Add, () => Task.FromResult<string?>(source.Path)), Is.True);

        var added = edit.Document.Find(edit.SelectedLayerId!.Value);
        Assert.Multiple(() =>
        {
            Assert.That(added.Name, Is.EqualTo(expectedName));
            Assert.That(added.Name.Length, Is.LessThanOrEqualTo(200));
            Assert.That(added.Bounds, Is.EqualTo(new RectD(90, 90, 80, 45)));
            Assert.That(edit.Document.Assets[added.AssetId!.Value].ToArray(), Is.EqualTo(source.Bytes));
            Assert.That(edit.Document.Layers[0].StateEquals(before.Layers[0]), Is.True);
            Assert.That(sources.Contains(source.Path), Is.True);
            Assert.Throws<InvalidOperationException>(() => sources.EnsureNotProtected(source.Path));
        });
        AssertHistoryAndNativeReopen(edit, before);
        AssertSourceUnchanged(source);
        return added;
    }

    private sealed record SourceFile(string Path, string Name, byte[] Bytes, DateTime Written);

    private SourceFile CreateSource(string name)
    {
        var path = Path.Combine(_directory, name);
        Assert.That(path.Length, Is.LessThan(260), "use a short temp directory to isolate the file-name boundary");
        var bytes = TestImages.EncodePng(80, 45, TestImages.SyntheticBgra(80, 45));
        File.WriteAllBytes(path, bytes);
        return new(path, name, bytes, File.GetLastWriteTimeUtc(path));
    }

    private void AssertSourceUnchanged(SourceFile source)
    {
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(source.Path), Is.True);
            Assert.That(Directory.GetFiles(_directory).Select(Path.GetFileName), Does.Contain(source.Name));
            Assert.That(File.ReadAllBytes(source.Path), Is.EqualTo(source.Bytes));
            Assert.That(File.GetLastWriteTimeUtc(source.Path), Is.EqualTo(source.Written));
        });
    }

    private static Task<LoadedImage> LoadImage(string path)
    {
        // This fixture isolates name and file boundaries; UI async scheduling has its own suite.
        var bytes = File.ReadAllBytes(path);
        var blob = new AssetBlob(bytes);
        ImageAssetValidator.Validate(blob, TestImages.Decoder);
        var (width, height) = ImageAssetValidator.ReadEncodedDimensions(blob);
        return Task.FromResult(new LoadedImage(blob, width, height));
    }

    private static EditSession Session(int layerCount = 1)
    {
        var edit = new EditSession(new EditorDocument { CanvasWidth = 640, CanvasHeight = 480,
            Layers = Enumerable.Range(0, layerCount).Select(index => new Layer { Kind = LayerKind.Shape,
                Name = $"原有图层 {index}", Bounds = new(20, 20, 30, 15) }).ToList() });
        edit.SelectedLayerId = edit.Document.Layers[0].Id;
        return edit;
    }

    private static void SeedRedo(EditSession edit)
    {
        var id = edit.SelectedLayerId!.Value;
        edit.UpdateLayer(id, layer => layer.Name += " redo checkpoint");
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.RedoCount, Is.EqualTo(1));
    }

    private void AssertHistoryAndNativeReopen(EditSession edit, EditorDocument before)
    {
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.RedoCount, Is.Zero);
        Assert.That(edit.IsDirty, Is.True);
        var after = edit.Document.Copy();
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.IsDirty, Is.False);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.StateEquals(after), Is.True);
        var outputs = new[] { false, true }.Select(mode => TestImages.RenderPng(after, mode, 320)).ToArray();
        var history = (edit.UndoCount, edit.RedoCount, edit.IsDirty);
        var native = Path.Combine(_directory, "roundtrip.ppgproj");
        NativePackageStore.Save(native, edit.Document, "long-image-name-tests");
        var reopened = NativePackageStore.Read(native);
        TestImages.ValidateImages(reopened);
        Assert.That(reopened.StateEquals(after), Is.True);
        for (var index = 0; index < outputs.Length; index++)
            Assert.That(TestImages.RenderPng(reopened, index == 1, 320), Is.EqualTo(outputs[index]));
        Assert.That(edit.Document.StateEquals(after), Is.True);
        Assert.That((edit.UndoCount, edit.RedoCount, edit.IsDirty), Is.EqualTo(history));
    }
}
