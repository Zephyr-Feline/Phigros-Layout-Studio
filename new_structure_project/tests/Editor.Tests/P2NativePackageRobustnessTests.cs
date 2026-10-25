using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P2NativePackageRobustnessTests
{
    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p2-robustness",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void EmptyAndRichDocumentsRoundTripAndRepeatSaveIsDeterministic()
    {
        var path = Path.Combine(_directory, "native.internal");
        var empty = new EditorDocument { CanvasWidth = 1920, CanvasHeight = 1080,
            BackgroundArgb = 0xFF123456 };
        NativePackageStore.Save(path, empty, "2.0.0");
        var emptyRead = NativePackageStore.Read(path);
        Assert.Multiple(() =>
        {
            Assert.That(emptyRead.CanvasWidth, Is.EqualTo(1920));
            Assert.That(emptyRead.BackgroundArgb, Is.EqualTo(0xFF123456));
            Assert.That(emptyRead.Layers, Is.Empty);
            Assert.That(emptyRead.Assets, Is.Empty);
        });

        var original = new EditorDocument { CanvasWidth = 1600, CanvasHeight = 900,
            BackgroundArgb = 0xFFABCDEF };
        original.ExtensionData["test/source"] = "self-authored";
        var groupId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        original.AddAsset(first, [1, 2, 3]);
        original.AddAsset(second, [4, 5, 6]);
        original.FixedBackgroundAssetId = second;
        original.Layers.AddRange([
            new Layer { Kind = LayerKind.Image, AssetId = first, Name = "image",
                Bounds = new RectD(13, 29, 420, 210), Visible = false, Locked = true,
                GroupId = groupId, KeepAspect = true },
            new Layer { Kind = LayerKind.Text, Text = "中文\nEnglish", Name = "text",
                FontFamily = "Microsoft YaHei UI", FontSize = 51, Bold = true, Italic = true,
                LineSpacing = 1.6, Alignment = HorizontalAlignment.Right,
                ColorArgb = 0x80443322, Bounds = new RectD(77, 81, 500, 150) },
            new Layer { Kind = LayerKind.Shape, Name = "shape", ColorArgb = 0xFF00AA11,
                Bounds = new RectD(3, 4, 50, 70) },
            EditorDocument.NewBadge(new PointD(600, 200), 256)
        ]);
        NativePackageStore.Save(path, original, "2.0.0");
        var firstSave = File.ReadAllBytes(path);
        NativePackageStore.Save(path, original, "2.0.0");
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(firstSave));
        var read = NativePackageStore.Read(path);
        Assert.Multiple(() =>
        {
            Assert.That(read.CanvasWidth, Is.EqualTo(original.CanvasWidth));
            Assert.That(read.CanvasHeight, Is.EqualTo(original.CanvasHeight));
            Assert.That(read.BackgroundArgb, Is.EqualTo(original.BackgroundArgb));
            Assert.That(read.FixedBackgroundAssetId, Is.EqualTo(second));
            Assert.That(read.Layers.Select(layer => layer.Id), Is.EqualTo(original.Layers.Select(layer => layer.Id)));
            Assert.That(read.Layers.Select(layer => layer.Bounds), Is.EqualTo(original.Layers.Select(layer => layer.Bounds)));
            Assert.That(read.Layers[0].Visible, Is.False);
            Assert.That(read.Layers[0].Locked, Is.True);
            Assert.That(read.Layers[0].GroupId, Is.EqualTo(groupId));
            Assert.That(read.ExtensionData["test/source"], Is.EqualTo("self-authored"));
            Assert.That(read.Layers[1].Text, Is.EqualTo("中文\nEnglish"));
            Assert.That(read.Layers[1].Bold && read.Layers[1].Italic, Is.True);
            Assert.That(read.Layers[1].LineSpacing, Is.EqualTo(1.6));
            Assert.That(read.Layers[1].ColorArgb, Is.EqualTo(0x80443322));
            Assert.That(read.Assets[first].ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(read.Assets[second].ToArray(), Is.EqualTo(new byte[] { 4, 5, 6 }));
        });
    }

    [Test]
    public void SourceNamesDoNotAffectContentIdentityOrOnDiskDeduplication()
    {
        var sourceA = Path.Combine(_directory, "a");
        var sourceB = Path.Combine(_directory, "b");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);
        File.WriteAllBytes(Path.Combine(sourceA, "same.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(sourceB, "same.png"), [4, 5, 6]);
        File.WriteAllBytes(Path.Combine(sourceB, "other.png"), [1, 2, 3]);
        var doc = new EditorDocument();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        doc.AddAsset(ids[0], File.ReadAllBytes(Path.Combine(sourceA, "same.png")));
        doc.AddAsset(ids[1], File.ReadAllBytes(Path.Combine(sourceB, "same.png")));
        doc.AddAsset(ids[2], File.ReadAllBytes(Path.Combine(sourceB, "other.png")));
        foreach (var id in ids) doc.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = id });
        Assert.That(doc.Assets[ids[0]], Is.SameAs(doc.Assets[ids[2]]));
        Assert.That(doc.Assets[ids[0]], Is.Not.SameAs(doc.Assets[ids[1]]));
        var path = Path.Combine(_directory, "dedup.internal");
        NativePackageStore.Save(path, doc, "2.0.0");
        using (var zip = ZipFile.OpenRead(path))
            Assert.That(zip.Entries.Count(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal)),
                Is.EqualTo(2));
        var read = NativePackageStore.Read(path);
        Assert.That(read.Assets[ids[0]], Is.SameAs(read.Assets[ids[2]]));
        Assert.That(read.Assets[ids[0]], Is.Not.SameAs(read.Assets[ids[1]]));
    }

    [Test]
    public void InvalidContainerManifestIdentifierAndVersionsAreRejected()
    {
        var path = Path.Combine(_directory, "bad.internal");
        File.WriteAllText(path, "not a zip");
        Assert.Throws<InvalidDataException>(() => NativePackageStore.Read(path));
        File.Delete(path);
        using (var emptyZip = ZipFile.Open(path, ZipArchiveMode.Create))
            emptyZip.CreateEntry("unrelated.txt");
        Assert.Throws<InvalidDataException>(() => NativePackageStore.Read(path));

        NativePackageStore.Save(path, new EditorDocument(), "2.0.0");
        ReplaceManifest(path, _ => "{ broken");
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("manifest"));
        NativePackageStore.Save(path, new EditorDocument(), "2.0.0");
        ReplaceManifest(path, json => json.Replace(NativePackageStore.FormatId, "unknown-format"));
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("format identifier"));
        NativePackageStore.Save(path, new EditorDocument(), "2.0.0");
        ReplaceManifest(path, json => json.Replace($"\"schemaVersion\": {NativePackageStore.SchemaVersion}", "\"schemaVersion\": 0"));
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unsupported native schema"));
    }

    [Test]
    public void MissingAssetUnknownReferenceAndConflictingEntriesAreRejected()
    {
        var path = AssetPackage();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            zip.Entries.Single(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal)).Delete();
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("missing asset"));

        path = AssetPackage();
        ReplaceManifest(path, json =>
        {
            var node = JsonNode.Parse(json)!;
            node["layers"]![0]!["assetId"] = "00000000-0000-0000-0000-000000000001";
            return node.ToJsonString();
        });
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("Missing image asset"));

        path = AssetPackage();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var name = zip.Entries.Single(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal)).FullName;
            zip.CreateEntry(name);
        }
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("duplicate"));
    }

    [TestCase("../escape.bin")]
    [TestCase("/absolute.bin")]
    [TestCase("C:/absolute.bin")]
    [TestCase("assets/../../../escape.bin")]
    [TestCase("assets/invalid\uFFFD.bin")]
    [TestCase("assets\\..\\escape.bin")]
    public void UnsafeAndUnexpectedEntryNamesAreRejected(string entryName)
    {
        var path = AssetPackage();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) zip.CreateEntry(entryName);
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unexpected"));
        Assert.That(File.Exists(Path.Combine(_directory, "escape.bin")), Is.False);
    }

    [Test]
    public void OverlongAndUnreferencedEntryNamesAreRejected()
    {
        var path = AssetPackage();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            zip.CreateEntry("assets/" + new string('a', 4096) + ".bin");
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unexpected"));

        path = AssetPackage();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            zip.CreateEntry("assets/" + new string('a', 64) + ".bin");
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("unreferenced"));
    }

    [Test]
    public void MissingManifestAndOversizedManifestAreRejected()
    {
        var path = AssetPackage();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) zip.GetEntry("project.json")!.Delete();
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("missing project.json"));
        path = AssetPackage();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("project.json")!.Delete();
            using var output = zip.CreateEntry("project.json").Open();
            output.Write(new byte[4 * 1024 * 1024 + 1]);
        }
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("too large"));
    }

    private string AssetPackage()
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".internal");
        var id = Guid.NewGuid();
        var doc = new EditorDocument();
        doc.AddAsset(id, [1, 2, 3]);
        doc.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = id });
        NativePackageStore.Save(path, doc, "2.0.0");
        return path;
    }

    private static void ReplaceManifest(string path, Func<string, string> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("project.json")!;
        string json;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) json = reader.ReadToEnd();
        entry.Delete();
        using var output = new StreamWriter(zip.CreateEntry("project.json").Open(), new UTF8Encoding(false));
        output.Write(change(json));
    }
}
