using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P2NativeSecurityAcceptanceTests
{
    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p2-security",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void CentralDirectoryRejectsExcessiveEntryCountBeforeArchiveEnumeration()
    {
        var path = Path.Combine(_directory, "many.package");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            for (var i = 0; i < 202; i++) zip.CreateEntry($"entry-{i}");
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("central directory"));
    }

    [Test]
    public void ForgedUncompressedSizeAndCentralCountAreRejected()
    {
        var path = AssetPackage(1);
        var bytes = File.ReadAllBytes(path);
        var central = FindCentralEntries(bytes).Single(entry => entry.Name.StartsWith("assets/", StringComparison.Ordinal));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central.Offset + 24, 4), 101u * 1024 * 1024);
        File.WriteAllBytes(path, bytes);
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("too large"));

        path = AssetPackage(1);
        bytes = File.ReadAllBytes(path);
        var eocd = FindEocd(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocd + 8, 2), 202);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(eocd + 10, 2), 202);
        File.WriteAllBytes(path, bytes);
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("central directory"));
    }

    [Test]
    public void DeclaredExpandedTotalCannotExceedThreeHundredMib()
    {
        var path = AssetPackage(4);
        var bytes = File.ReadAllBytes(path);
        foreach (var entry in FindCentralEntries(bytes).Where(item => item.Name.StartsWith("assets/", StringComparison.Ordinal)))
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry.Offset + 24, 4), 90u * 1024 * 1024);
        File.WriteAllBytes(path, bytes);
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("expanded size"));
    }

    [TestCase("PROJECT.JSON")]
    [TestCase("assets/ABCDEF.bin")]
    [TestCase("project\u0301.json")]
    public void CaseAndUnicodeLookalikeEntriesAreNotNormalizedIntoAllowedPaths(string name)
    {
        var path = AssetPackage(1);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) zip.CreateEntry(name);
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unexpected"));
    }

    [Test]
    public void DuplicateIdsCanvasBoundsAndLongFieldsAreRejected()
    {
        AssertBadManifest(node => node["layers"]![1]!["id"] = node["layers"]![0]!["id"]!.GetValue<string>());
        AssertBadManifest(node => node["canvasWidth"] = 0);
        AssertBadManifest(node => node["layers"]![0]!["bounds"]!["width"] = -1);
        AssertBadManifest(node => node["layers"]![0]!["name"] = new string('x', 201));
        AssertBadManifest(node => node["layers"]![0]!["text"] = null);
        AssertBadManifest(node => node["extensionData"]!["test/key"] = new string('x', 4097));
        AssertBadManifest(node => node["layers"]![0]!["groupId"] = Guid.Empty.ToString());
    }

    [Test]
    public void UnknownTransformPropertiesAndMissingRequiredFieldsAreRejected()
    {
        AssertBadManifest(node => node["layers"]![0]!["rotation"] = 15);
        AssertBadManifest(node => node["layers"]![0]!["opacity"] = 1.5);
        AssertBadManifest(node => node["layers"]![0]!["scale"] = -1);
        AssertBadManifest(node => node.AsObject().Remove("schemaVersion"));
        AssertBadManifest(node => node["layers"]![0]!["alignment"] = "FutureAlignment");
        AssertBadManifest(node => node["extensionData"]!["test/key"] = new JsonObject { ["arbitrary"] = 1 });
    }

    [TestCase("NaN")]
    [TestCase("Infinity")]
    [TestCase("1e999")]
    public void NonFiniteGeometryCannotEnterTheModel(string invalidNumber)
    {
        var path = AssetPackage(0);
        ReplaceManifest(path, json =>
        {
            var changed = json.Replace("\"x\": 0", $"\"x\": {invalidNumber}", StringComparison.Ordinal);
            Assert.That(changed, Is.Not.EqualTo(json));
            return changed;
        });
        Assert.Throws<InvalidDataException>(() => NativePackageStore.Read(path));
    }

    [Test]
    public void TwoHundredAssetIdsWithOneHashNeedOneEntryAndExtraIndexIsRejected()
    {
        var doc = new EditorDocument();
        for (var i = 0; i < 200; i++) doc.AddAsset(Guid.NewGuid(), [1, 2, 3]);
        var path = Path.Combine(_directory, "many-ids.package");
        NativePackageStore.Save(path, doc, "p2-test");
        using (var zip = ZipFile.OpenRead(path))
            Assert.That(zip.Entries.Count(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal)),
                Is.EqualTo(1));
        Assert.That(NativePackageStore.Read(path).Assets.Count, Is.EqualTo(200));
        ReplaceManifest(path, json =>
        {
            var node = JsonNode.Parse(json)!;
            var index = node["assets"]!.AsArray();
            var extra = index[0]!.DeepClone();
            extra["id"] = Guid.NewGuid().ToString();
            index.Add(extra);
            return node.ToJsonString();
        });
        Assert.Throws<InvalidDataException>(() => NativePackageStore.Read(path));
    }

    private void AssertBadManifest(Action<JsonNode> mutate)
    {
        var path = AssetPackage(0);
        ReplaceManifest(path, json =>
        {
            var node = JsonNode.Parse(json)!;
            mutate(node);
            return node.ToJsonString();
        });
        Assert.Throws<InvalidDataException>(() => NativePackageStore.Read(path));
    }

    private string AssetPackage(int assetCount)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".package");
        var doc = new EditorDocument();
        doc.ExtensionData["test/key"] = "value";
        doc.Layers.Add(new Layer { Kind = LayerKind.Text, Name = "first" });
        doc.Layers.Add(new Layer { Kind = LayerKind.Shape, Name = "second" });
        for (var i = 0; i < assetCount; i++) doc.AddAsset(Guid.NewGuid(), [(byte)(i + 1)]);
        NativePackageStore.Save(path, doc, "p2-test");
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

    private static int FindEocd(byte[] bytes)
    {
        for (var i = bytes.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x06054b50) return i;
        throw new InvalidDataException("Fixture has no EOCD.");
    }

    private static IEnumerable<(int Offset, string Name)> FindCentralEntries(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length - 46; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) != 0x02014b50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28, 2));
            if (i + 46 + nameLength > bytes.Length) continue;
            yield return (i, Encoding.UTF8.GetString(bytes, i + 46, nameLength));
        }
    }
}
