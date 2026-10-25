using System.IO.Compression;
using System.Text;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P2NativePackageTests
{
    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p2-package-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void NativePackageRoundTripDeduplicatesAssetsAndKeepsLayerOrder()
    {
        var path = Path.Combine(_directory, "内部候选.probezip");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var document = new EditorDocument { FixedBackgroundAssetId = second };
        document.AddAsset(first, bytes);
        document.AddAsset(second, bytes);
        var image = new Layer { Kind = LayerKind.Image, Name = "self-authored image",
            AssetId = first, Bounds = new RectD(40, 60, 320, 200) };
        var text = new Layer { Kind = LayerKind.Text, Text = "中文 English", FontSize = 42,
            Alignment = HorizontalAlignment.Right, LineSpacing = 1.5,
            Bounds = new RectD(90, 200, 500, 120) };
        document.Layers.AddRange([image, text, EditorDocument.NewBadge(new PointD(700, 250))]);

        NativePackageStore.Save(path, document, "p2-test");
        using (var zip = ZipFile.OpenRead(path))
            Assert.That(zip.Entries.Count(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal)),
                Is.EqualTo(1));
        var before = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        var reopened = NativePackageStore.Read(path);

        Assert.Multiple(() =>
        {
            Assert.That(reopened.Layers.Select(layer => layer.Id), Is.EqualTo(document.Layers.Select(layer => layer.Id)));
            Assert.That(reopened.Layers[1].Text, Is.EqualTo("中文 English"));
            Assert.That(reopened.Layers[1].Alignment, Is.EqualTo(HorizontalAlignment.Right));
            Assert.That(reopened.Layers[1].LineSpacing, Is.EqualTo(1.5));
            Assert.That(reopened.FixedBackgroundAssetId, Is.EqualTo(second));
            Assert.That(reopened.Assets[first].ToArray(), Is.EqualTo(bytes));
            Assert.That(reopened.Assets[second].ToArray(), Is.EqualTo(bytes));
            Assert.That(ReferenceEquals(reopened.Assets[first], reopened.Assets[second]), Is.True);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(stamp));
        });
    }

    [Test]
    public void FutureSchemaAndUnknownFieldsAreRejectedWithoutTouchingFile()
    {
        var path = Path.Combine(_directory, "future.probezip");
        NativePackageStore.Save(path, new EditorDocument(), "p2-test");
        ReplaceManifest(path, json => json.Replace($"\"schemaVersion\": {NativePackageStore.SchemaVersion}", "\"schemaVersion\": 99"));
        var before = File.ReadAllBytes(path);
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("newer schema"));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));

        ReplaceManifest(path, json => json.Replace("\"schemaVersion\": 99",
            $"\"schemaVersion\": {NativePackageStore.SchemaVersion},\n  \"unknown\": true"));
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("unknown fields"));
    }

    [Test]
    public void TamperedAssetAndTraversalEntryAreRejected()
    {
        var path = Path.Combine(_directory, "tamper.probezip");
        var id = Guid.NewGuid();
        var document = new EditorDocument();
        document.AddAsset(id, [1, 2, 3]);
        document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = id });
        NativePackageStore.Save(path, document, "p2-test");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var asset = zip.Entries.Single(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal));
            var name = asset.FullName;
            asset.Delete();
            using var output = zip.CreateEntry(name).Open();
            output.Write([9, 9, 9]);
        }
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("SHA-256"));

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            using (var output = zip.CreateEntry("../escape.bin").Open()) output.WriteByte(1);
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unexpected"));
    }

    [Test]
    public void UnknownLayerKindAndDuplicateManifestKeyAreRejected()
    {
        var path = Path.Combine(_directory, "bad-manifest.probezip");
        var document = new EditorDocument();
        document.Layers.Add(new Layer { Kind = LayerKind.Text, Text = "自制文本" });
        NativePackageStore.Save(path, document, "p2-test");
        ReplaceManifest(path, json => json.Replace("\"kind\": \"Text\"", "\"kind\": \"FutureKind\""));
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("invalid or unknown fields"));

        ReplaceManifest(path, json => json.Replace("\"kind\": \"FutureKind\"",
            "\"kind\": \"Text\", \"kind\": \"Text\""));
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("Duplicate native manifest field"));
    }

    [Test]
    public void InvalidDocumentCannotOverwriteExistingNativePackage()
    {
        var path = Path.Combine(_directory, "existing.probezip");
        NativePackageStore.Save(path, new EditorDocument(), "p2-test");
        var before = File.ReadAllBytes(path);
        var invalid = new EditorDocument { CanvasWidth = 0 };
        Assert.Throws<InvalidDataException>(() => NativePackageStore.Save(path, invalid, "p2-test"));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        Assert.DoesNotThrow(() => NativePackageStore.Read(path));
    }

    [Test]
    [Platform("Win")]
    public void LockedNativePackageCannotBeHalfOverwritten()
    {
        var path = Path.Combine(_directory, "locked.probezip");
        NativePackageStore.Save(path, new EditorDocument { BackgroundArgb = 0xFF102030 }, "p2-test");
        var before = File.ReadAllBytes(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => NativePackageStore.Save(path,
                new EditorDocument { BackgroundArgb = 0xFF405060 }, "p2-test"));
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            Assert.That(NativePackageStore.Read(path).BackgroundArgb, Is.EqualTo(0xFF102030));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        });
    }

    private static void ReplaceManifest(string path, Func<string, string> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("project.json")!;
        string json;
        using (var input = new StreamReader(entry.Open(), Encoding.UTF8)) json = input.ReadToEnd();
        entry.Delete();
        var changed = change(json);
        Assert.That(changed, Is.Not.EqualTo(json), "The mutation must change the manifest under test.");
        using var output = new StreamWriter(zip.CreateEntry("project.json").Open(), new UTF8Encoding(false));
        output.Write(changed);
    }
}
