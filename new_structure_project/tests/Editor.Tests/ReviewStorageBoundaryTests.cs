using System.IO.Compression;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Formats.Compatibility;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>Real encoded-byte boundaries, using opaque self-created blobs at the storage boundary.</summary>
public sealed class ReviewStorageBoundaryTests
{
    private const int Mib = 1024 * 1024;
    private const string ApplicationVersion = "review-boundary-test";
    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "review-storage",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(1)]
    public async Task TemporaryJsonSaveAndReadUseTheSameFinalByteLimit(int offset)
    {
        var document = DocumentWithAssets(1);
        var destination = Path.Combine(_directory, "temporary.json");
        NativeDocumentStore.Save(destination, document);
        var originalBytes = File.ReadAllBytes(destination);
        var originalTime = File.GetLastWriteTimeUtc(destination);
        var baseLength = originalBytes.LongLength;
        // Base64 grows in four-byte steps; plain ASCII text fills the remaining 0..3 bytes.
        var assetLength = (int)((CompatibilityInputLimits.TemporaryJsonBytes - 1 - baseLength + 4) / 4 * 3);
        var encodedLength = baseLength - 4 + 4L * ((assetLength + 2) / 3);
        var textLength = (int)(CompatibilityInputLimits.TemporaryJsonBytes + offset - encodedLength);
        Assert.That(textLength, Is.InRange(0, 5));
        var assetId = document.Assets.Keys.Single();
        var blob = Blob(assetLength, 1);
        var native = CreateSavedWorkflow(document);
        native.Session.UpdateDocument(candidate =>
        {
            candidate.Assets[assetId] = blob;
            candidate.Layers[0].Text = new string('x', textLength);
        });
        var before = native.Session.Document.Copy();
        var pathBefore = native.CurrentPath;
        var undoBefore = native.Session.UndoCount;
        var redoBefore = native.Session.RedoCount;
        var nativeBytes = File.ReadAllBytes(pathBefore!);

        if (offset > 0)
        {
            Assert.That(async () => await native.SaveTemporaryAsync(
                    () => Task.FromResult<string?>(destination), new()),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("size limit"));
            AssertFailureState(native, before, pathBefore, undoBefore, redoBefore);
            Assert.That(File.ReadAllBytes(destination), Is.EqualTo(originalBytes));
            Assert.That(File.GetLastWriteTimeUtc(destination), Is.EqualTo(originalTime));
        }
        else
        {
            Assert.That(await native.SaveTemporaryAsync(() => Task.FromResult<string?>(destination), new()), Is.True);
            Assert.That(new FileInfo(destination).Length,
                Is.EqualTo(CompatibilityInputLimits.TemporaryJsonBytes + offset));
            Assert.That(LegacyFormatDetector.DetectFile(destination), Is.EqualTo(LegacyFormatKind.TemporaryJson));
            Assert.That(NativeDocumentStore.Read(destination).StateEquals(before), Is.True);
            Assert.That(native.CurrentPath, Is.Null);
            Assert.That(native.IsDirty, Is.False);
            Assert.That(native.Session.UndoCount, Is.EqualTo(undoBefore));
        }
        Assert.That(File.ReadAllBytes(pathBefore!), Is.EqualTo(nativeBytes));
        Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(1)]
    public void NativeSaveAndReadCountManifestInTheExpandedByteLimit(int offset)
    {
        var document = DocumentWithAssets(3);
        var destination = Path.Combine(_directory, "destination.ppgproj");
        NativePackageStore.Save(destination, document, ApplicationVersion);
        var originalBytes = File.ReadAllBytes(destination);
        var originalTime = File.GetLastWriteTimeUtc(destination);
        long manifestLength;
        using (var archive = ZipFile.OpenRead(destination))
            manifestLength = archive.GetEntry("project.json")!.Length;
        var ids = document.Assets.Keys.ToArray();
        var blobs = new[]
        {
            Blob(100 * Mib, 1), Blob(100 * Mib, 2),
            Blob(100 * Mib - (int)manifestLength + offset, 3)
        };
        var native = CreateSavedWorkflow(document);
        // Storage compatibility still permits existing documents near 300 MiB. Build that
        // fixture as an adopted legacy snapshot, rather than importing new resources through
        // the editor's separate 256 MiB admission policy. An equal-length name edit keeps a
        // real undo state and leaves the exact serialized-manifest size calculation unchanged.
        var existingLargeDocument = document.Copy();
        for (var i = 0; i < ids.Length; i++) existingLargeDocument.Assets[ids[i]] = blobs[i];
        native.Session.ReplaceDocument(existingLargeDocument, saved: false);
        native.Session.UpdateLayer(existingLargeDocument.Layers[0].Id, layer => layer.Name = "Other");
        var before = native.Session.Document.Copy();
        var pathBefore = native.CurrentPath;
        var undoBefore = native.Session.UndoCount;
        var redoBefore = native.Session.RedoCount;
        var nativeBytes = File.ReadAllBytes(pathBefore!);
        Assert.That(blobs.Sum(blob => (long)blob.Length) + manifestLength, Is.EqualTo(300L * Mib + offset));

        if (offset > 0)
        {
            Assert.That(() => native.SaveAs(destination),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("expanded size"));
            AssertFailureState(native, before, pathBefore, undoBefore, redoBefore);
            Assert.That(File.ReadAllBytes(destination), Is.EqualTo(originalBytes));
            Assert.That(File.GetLastWriteTimeUtc(destination), Is.EqualTo(originalTime));
            // Saving over the active path must protect that committed file as well.
            Assert.That(() => native.Save(),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("expanded size"));
            AssertFailureState(native, before, pathBefore, undoBefore, redoBefore);
        }
        else
        {
            native.SaveAs(destination);
            using (var archive = ZipFile.OpenRead(destination))
                Assert.That(archive.Entries.Sum(entry => entry.Length), Is.EqualTo(300L * Mib + offset));
            Assert.That(NativePackageStore.Read(destination).StateEquals(before), Is.True);
            Assert.That(native.CurrentPath, Is.EqualTo(destination));
            Assert.That(native.IsDirty, Is.False);
            Assert.That(native.Session.UndoCount, Is.EqualTo(undoBefore));
        }
        Assert.That(File.ReadAllBytes(pathBefore!), Is.EqualTo(nativeBytes));
        Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
    }

    private NativeProjectWorkflow CreateSavedWorkflow(EditorDocument document)
    {
        // Image decoding is tested separately; this suite isolates container/savepoint limits.
        var native = new NativeProjectWorkflow(new EditSession(document), candidate => candidate.Validate(),
            ApplicationVersion);
        native.SaveAs(Path.Combine(_directory, "current.ppgproj"));
        return native;
    }

    private static EditorDocument DocumentWithAssets(int count)
    {
        var document = new EditorDocument();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            document.AddAsset(id, [(byte)(i + 1)]);
            document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = id });
        }
        return document;
    }

    private static AssetBlob Blob(int length, byte marker)
    {
        var bytes = new byte[length];
        bytes[^1] = marker;
        return new AssetBlob(bytes);
    }

    private static void AssertFailureState(NativeProjectWorkflow native, EditorDocument before,
        string? pathBefore, int undoBefore, int redoBefore)
    {
        Assert.Multiple(() =>
        {
            Assert.That(native.Session.Document.StateEquals(before), Is.True);
            Assert.That(native.CurrentPath, Is.EqualTo(pathBefore));
            Assert.That(native.IsDirty, Is.True);
            Assert.That(native.Session.UndoCount, Is.EqualTo(undoBefore));
            Assert.That(native.Session.RedoCount, Is.EqualTo(redoBefore));
        });
    }
}
