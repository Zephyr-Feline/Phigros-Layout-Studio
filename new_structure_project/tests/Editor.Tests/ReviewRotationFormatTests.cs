using System.IO.Compression;
using System.Text.Json.Nodes;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class ReviewRotationFormatTests
{
    private string _directory = "";
    [SetUp] public void SetUp() => _directory = Directory.CreateTempSubdirectory("review-rotation-").FullName;
    [TearDown] public void TearDown() => Directory.Delete(_directory, true);

    [TestCase(-180)] [TestCase(-33.5)] [TestCase(0)] [TestCase(90)] [TestCase(180)]
    public void NativeAndTemporaryRoundTripTheAngleAndRenderedPixels(double angle)
    {
        var document = Document(angle); var package = Path.Combine(_directory, "native.ppgproj");
        var json = Path.Combine(_directory, "temporary.json");
        NativePackageStore.Save(package, document, "review-v5"); NativeDocumentStore.Save(json, document);
        var native = NativePackageStore.Read(package); var temporary = NativeDocumentStore.Read(json);
        Assert.Multiple(() =>
        {
            Assert.That(Manifest(package)["schemaVersion"]!.GetValue<int>(),
                Is.EqualTo(NativePackageStore.SchemaVersion));
            Assert.That(JsonNode.Parse(File.ReadAllText(json))!["FormatVersion"]!.GetValue<int>(),
                Is.EqualTo(NativeDocumentStore.CurrentVersion));
            Assert.That(native.StateEquals(document), Is.True);
            Assert.That(temporary.StateEquals(document), Is.True);
            Assert.That(TestImages.RenderPng(native, true), Is.EqualTo(TestImages.RenderPng(document, true)));
            Assert.That(TestImages.RenderPng(temporary, true), Is.EqualTo(TestImages.RenderPng(document, true)));
        });
    }

    [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
    public void OlderPackagesLoadWithZeroRotationInMemoryAndUpgradeOnlyOnSave(int version)
    {
        var source = Path.Combine(_directory, "old.ppgproj");
        NativePackageStore.Save(source, Document(), "old-producer"); var manifest = Manifest(source);
        manifest["schemaVersion"] = version;
        LegacyFormatTestHelpers.RemoveV8BackgroundFields(manifest, temporary: false);
        foreach (var layer in manifest["layers"]!.AsArray())
        {
            layer!.AsObject().Remove("rotationDegrees");
            layer.AsObject().Remove("opacity");
            layer.AsObject().Remove("imageFit");
            layer.AsObject().Remove("crop");
            if (version < 4) layer.AsObject().Remove("slanted");
            if (version < 3) layer.AsObject().Remove("verticalAlignment");
        }
        if (version < 4) manifest["backgroundBlend"]!.AsObject().Remove("includeFlatImages");
        if (version == 1) manifest.AsObject().Remove("backgroundBlend");
        WriteManifest(source, manifest);
        var bytes = File.ReadAllBytes(source); var timestamp = File.GetLastWriteTimeUtc(source);
        var loaded = NativePackageStore.Read(source);
        Assert.That(loaded.Layers.Single().RotationDegrees, Is.Zero);
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(timestamp));
        var output = Path.Combine(_directory, "upgraded.ppgproj");
        NativePackageStore.Save(output, loaded, "review-v5");
        Assert.That(Manifest(output)["schemaVersion"]!.GetValue<int>(),
            Is.EqualTo(NativePackageStore.SchemaVersion));
        Assert.That(NativePackageStore.Read(output).StateEquals(loaded), Is.True);
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(bytes));
    }

    [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)]
    public void OlderTemporaryJsonLoadsMissingAngleAsZeroWithoutRewriting(int version)
    {
        var source = Path.Combine(_directory, "old.json"); NativeDocumentStore.Save(source, Document());
        var json = JsonNode.Parse(File.ReadAllText(source))!; json["FormatVersion"] = version;
        LegacyFormatTestHelpers.RemoveV8BackgroundFields(json, temporary: true);
        json["Document"]!["Layers"]![0]!.AsObject().Remove("RotationDegrees");
        File.WriteAllText(source, json.ToJsonString()); var bytes = File.ReadAllBytes(source);
        Assert.That(NativeDocumentStore.Read(source).Layers.Single().RotationDegrees, Is.Zero);
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(bytes));
    }

    [Test]
    public void V5MissingAngleDefaultsToZeroAndFutureSchemaIsRejected()
    {
        var path = Path.Combine(_directory, "missing.ppgproj"); NativePackageStore.Save(path, Document(), "review-v5");
        var manifest = Manifest(path); manifest["layers"]![0]!.AsObject().Remove("rotationDegrees");
        WriteManifest(path, manifest); Assert.That(NativePackageStore.Read(path).Layers.Single().RotationDegrees, Is.Zero);
        manifest["schemaVersion"] = 99; WriteManifest(path, manifest);
        Assert.That(() => NativePackageStore.Read(path), Throws.TypeOf<InvalidDataException>().With.Message.Contains("newer"));
    }

    [TestCase(-180.01)] [TestCase(180.01)]
    public void InvalidPackageAnglesAreRejectedBeforeRendering(double angle)
    {
        var path = Path.Combine(_directory, "invalid.ppgproj"); NativePackageStore.Save(path, Document(), "review-v5");
        var manifest = Manifest(path); manifest["layers"]![0]!["rotationDegrees"] = angle; WriteManifest(path, manifest);
        Assert.Throws<InvalidDataException>(() => NativePackageStore.Read(path));
        Assert.Throws<InvalidDataException>(() => Document(angle).Validate());
    }

    [Test]
    public void NonfiniteModelAnglesAreRejectedAndAFalseOlderVersionCannotCarryRotation()
    {
        Assert.Throws<InvalidDataException>(() => Document(double.NaN).Validate());
        Assert.Throws<InvalidDataException>(() => Document(double.PositiveInfinity).Validate());
        var path = Path.Combine(_directory, "old-but-rotated.ppgproj"); NativePackageStore.Save(path, Document(45), "review-v5");
        var manifest = Manifest(path); manifest["schemaVersion"] = 4; WriteManifest(path, manifest);
        Assert.Throws<InvalidDataException>(() => NativePackageStore.Read(path));
        var jsonPath = Path.Combine(_directory, "old-but-rotated.json"); NativeDocumentStore.Save(jsonPath, Document(45));
        var json = JsonNode.Parse(File.ReadAllText(jsonPath))!; json["FormatVersion"] = 4;
        File.WriteAllText(jsonPath, json.ToJsonString()); Assert.Throws<InvalidDataException>(() => NativeDocumentStore.Read(jsonPath));
    }

    private static EditorDocument Document(double angle = 0) => new()
    {
        CanvasWidth = 400, CanvasHeight = 300,
        Layers = [new Layer { Kind = LayerKind.Shape, Bounds = new(80, 80, 200, 100), RotationDegrees = angle }]
    };
    private static JsonObject Manifest(string path)
    {
        using var archive = ZipFile.OpenRead(path); using var reader = new StreamReader(archive.GetEntry("project.json")!.Open());
        return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }
    private static void WriteManifest(string path, JsonNode manifest)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update); archive.GetEntry("project.json")!.Delete();
        using var writer = new StreamWriter(archive.CreateEntry("project.json").Open()); writer.Write(manifest.ToJsonString());
    }
}
