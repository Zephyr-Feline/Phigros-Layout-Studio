using System.IO.Compression;
using System.Text.Json.Nodes;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4NativeV2Tests
{
    private string _directory = "";
    [SetUp] public void SetUp() => _directory = Directory.CreateTempSubdirectory("ppg-native-v2-").FullName;
    [TearDown] public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void ActualV1ManifestMigratesInMemoryWithoutWritingAndResavesAsCurrentSchema()
    {
        var path = Path.Combine(_directory, "old.ppgproj");
        const string oldJson = """
            {"formatId":"phigros-preview-editor-native","schemaVersion":1,
             "applicationVersion":"2.0.0-p3","canvasWidth":800,"canvasHeight":600,
             "backgroundArgb":4280295456,"fixedBackgroundAssetId":null,
             "layers":[{"id":"11111111-1111-1111-1111-111111111111","kind":"Text",
              "bounds":{"x":20,"y":30,"width":240,"height":80},"text":"Old project","fontSize":48}],
             "assets":[],"extensionData":{"future-owner-field":"keep me"}}
            """;
        WritePackage(path, oldJson);
        var bytes = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        var document = NativePackageStore.Read(path);
        Assert.Multiple(() =>
        {
            Assert.That(document.BackgroundBlend,
                Is.EqualTo(new BackgroundBlendSettings(IncludeFlatImages: true)));
            Assert.That(document.Layers.Single().Text, Is.EqualTo("Old project"));
            Assert.That(document.Layers.Single().VerticalAlignment, Is.EqualTo(VerticalAlignment.Top));
            Assert.That(document.ExtensionData["future-owner-field"], Is.EqualTo("keep me"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(stamp));
        });
        var saved = Path.Combine(_directory, "new.ppgproj");
        NativePackageStore.Save(saved, document, "p4-test");
        Assert.That(Manifest(saved)["schemaVersion"]!.GetValue<int>(),
            Is.EqualTo(NativePackageStore.SchemaVersion));
        Assert.That(NativePackageStore.Read(saved).StateEquals(document), Is.True);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
    }

    [Test]
    public void AutomaticBackgroundRoundTripRetainsSettingsAndPixelsWithoutDerivedAssets()
    {
        var document = P4AutomaticBackgroundTests.MakeDocument();
        document.BackgroundBlend = new(true, 12, 0.65, IncludeFlatImages: true);
        var path = Path.Combine(_directory, "auto.ppgproj");
        NativePackageStore.Save(path, document, "p4-test");
        var loaded = NativePackageStore.Read(path);
        Assert.That(loaded.StateEquals(document), Is.True);
        Assert.That(loaded.Assets.Count, Is.EqualTo(3));
        Assert.That(TestImages.RenderPng(loaded), Is.EqualTo(TestImages.RenderPng(document)));
        var jsonPath = Path.Combine(_directory, "temporary.json");
        NativeDocumentStore.Save(jsonPath, document);
        Assert.That(NativeDocumentStore.Read(jsonPath).StateEquals(document), Is.True);
    }

    [Test]
    public void V1FixedBackgroundAndArtworkRetainTheirOriginalPixelsAndIdentities()
    {
        var document = P4AutomaticBackgroundTests.MakeDocument();
        document.BackgroundBlend = new(IncludeFlatImages: true);
        document.FixedBackgroundAssetId = document.Layers[1].AssetId;
        var path = Path.Combine(_directory, "v1-images.ppgproj");
        NativePackageStore.Save(path, document, "old-producer");
        var manifest = Manifest(path);
        manifest.Remove("backgroundBlend");
        foreach (var layer in manifest["layers"]!.AsArray())
        {
            layer!.AsObject().Remove("verticalAlignment");
            layer.AsObject().Remove("slanted");
            layer.AsObject().Remove("opacity");
            layer.AsObject().Remove("imageFit");
            layer.AsObject().Remove("crop");
        }
        manifest["schemaVersion"] = 1;
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("project.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("project.json").Open());
            writer.Write(manifest.ToJsonString());
        }
        var before = File.ReadAllBytes(path);
        var read = NativePackageStore.Read(path);
        Assert.That(read.StateEquals(document), Is.True);
        Assert.That(TestImages.RenderPng(read), Is.EqualTo(TestImages.RenderPng(document)));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
    }

    [Test]
    public void V2ManifestMigratesTopAlignmentInMemoryWithoutChangingSourcePixels()
    {
        var document = new EditorDocument { CanvasWidth = 480, CanvasHeight = 300,
            BackgroundArgb = 0xFF000000,
            BackgroundBlend = new(IncludeFlatImages: true),
            Layers = [new Layer { Kind = LayerKind.Text, Text = "Top", FontSize = 42,
                Bounds = new RectD(20, 20, 220, 160) }] };
        var path = Path.Combine(_directory, "v2.ppgproj");
        NativePackageStore.Save(path, document, "p4-test");
        var manifest = Manifest(path);
        manifest["schemaVersion"] = 2;
        manifest["backgroundBlend"]!.AsObject().Remove("includeFlatImages");
        LegacyFormatTestHelpers.RemoveV8BackgroundFields(manifest, temporary: false);
        foreach (var layer in manifest["layers"]!.AsArray())
        {
            layer!.AsObject().Remove("verticalAlignment");
            layer.AsObject().Remove("slanted");
            layer.AsObject().Remove("opacity");
            layer.AsObject().Remove("imageFit");
            layer.AsObject().Remove("crop");
        }
        WritePackage(path, manifest.ToJsonString());
        var bytes = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        var imported = NativePackageStore.Read(path);
        Assert.Multiple(() =>
        {
            Assert.That(imported.Layers.Single().VerticalAlignment, Is.EqualTo(VerticalAlignment.Top));
            Assert.That(imported.StateEquals(document), Is.True);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(stamp));
        });
        Assert.That(TestImages.RenderPng(imported), Is.EqualTo(TestImages.RenderPng(document)));
    }

    [TestCase("missing")]
    [TestCase("null")]
    [TestCase("invalid")]
    [TestCase("future")]
    [TestCase("v1-with-v2-fields")]
    [TestCase("v2-with-v3-fields")]
    [TestCase("v3-missing-vertical")]
    [TestCase("v3-invalid-vertical")]
    [TestCase("version-as-string")]
    [TestCase("root-array")]
    public void InvalidVersionedSettingsAreRejectedAndSourceIsPreserved(string kind)
    {
        var path = Path.Combine(_directory, "invalid.ppgproj");
        NativePackageStore.Save(path, new EditorDocument(), "p4-test");
        var manifest = Manifest(path);
        switch (kind)
        {
            case "missing": manifest.Remove("backgroundBlend"); break;
            case "null": manifest["backgroundBlend"] = null; break;
            case "invalid": manifest["backgroundBlend"]!["darkness"] = 2; break;
            case "future": manifest["schemaVersion"] = 99; manifest["futureField"] = true; break;
            case "v1-with-v2-fields": manifest["schemaVersion"] = 1; break;
            case "v2-with-v3-fields":
                manifest["schemaVersion"] = 2;
                manifest["layers"] = new JsonArray(JsonNode.Parse("""
                    {"id":"11111111-1111-1111-1111-111111111111","kind":"Text",
                     "bounds":{"x":0,"y":0,"width":100,"height":100},
                     "verticalAlignment":"Top"}
                    """)!);
                break;
            case "v3-missing-vertical":
                manifest["layers"] = new JsonArray(JsonNode.Parse("""
                    {"id":"11111111-1111-1111-1111-111111111111","kind":"Text",
                     "bounds":{"x":0,"y":0,"width":100,"height":100}}
                    """)!);
                break;
            case "v3-invalid-vertical":
                manifest["layers"] = new JsonArray(JsonNode.Parse("""
                    {"id":"11111111-1111-1111-1111-111111111111","kind":"Text",
                     "bounds":{"x":0,"y":0,"width":100,"height":100},
                     "verticalAlignment":"Beyond"}
                    """)!);
                break;
            case "version-as-string": manifest["schemaVersion"] = "2"; break;
        }
        WritePackage(path, kind == "root-array" ? "[]" : manifest.ToJsonString());
        var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => NativePackageStore.Read(path));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
    }

    private static JsonObject Manifest(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("project.json")!.Open();
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static void WritePackage(string path, string json)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using var output = new StreamWriter(zip.CreateEntry("project.json").Open());
        output.Write(json);
    }
}
