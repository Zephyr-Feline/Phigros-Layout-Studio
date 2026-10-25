using System.Text.Json.Nodes;
using System.IO.Compression;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3OpacityCropTests
{
    [Test]
    public void LayerOpacityMultipliesContentAlphaOnce()
    {
        var white = new Layer
        {
            Kind = LayerKind.Shape, Bounds = new RectD(0, 0, 320, 180),
            ColorArgb = 0xFFFFFFFF, Opacity = 0.5
        };
        var document = new EditorDocument
        {
            CanvasWidth = 320, CanvasHeight = 180, Layers = [white]
        };
        var alpha = Pixel(TestImages.RenderPng(document, true), 160, 90)[3];
        Assert.That(alpha, Is.InRange(126, 129));

        var halfColor = white.Copy();
        halfColor.ColorArgb = 0x80FFFFFF;
        var colorDocument = document.Copy();
        colorDocument.Layers[0] = halfColor;
        var halfAlpha = Pixel(TestImages.RenderPng(colorDocument, true), 160, 90)[3];
        Assert.That(halfAlpha, Is.InRange(62, 66));
    }

    [Test]
    public void ImageCropSamplesTheRequestedSourceRegionAndContainLeavesTransparentBars()
    {
        var pixels = new byte[8 * 4 * 4];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 8; x++)
        {
            var at = (y * 8 + x) * 4;
            pixels[at + 2] = x < 4 ? (byte)255 : (byte)0;
            pixels[at + 1] = x < 4 ? (byte)0 : (byte)255;
            pixels[at + 3] = 255;
        }
        var blob = new AssetBlob(TestImages.EncodePng(8, 4, pixels));
        var image = new Layer
        {
            Kind = LayerKind.Image, AssetId = Guid.NewGuid(),
            Bounds = new RectD(0, 0, 320, 180),
            ImageFit = ImageFitMode.Stretch, ImageCrop = new RectD(.5, 0, .5, 1)
        };
        var document = new EditorDocument
        {
            CanvasWidth = 320, CanvasHeight = 180, Layers = [image]
        };
        document.AddAsset(image.AssetId!.Value, blob);
        var cropped = TestImages.RenderPng(document, true);
        Assert.That(Pixel(cropped, 80, 90)[1], Is.GreaterThan(200), "left sample is green");
        Assert.That(Pixel(cropped, 240, 90)[1], Is.GreaterThan(200), "right sample is green");

        var contain = image.Copy();
        contain.Bounds = new RectD(0, 0, 100, 100);
        contain.ImageCrop = new RectD(0, 0, 1, 1);
        contain.ImageFit = ImageFitMode.Contain;
        var containDocument = new EditorDocument
        {
            CanvasWidth = 320, CanvasHeight = 180, Layers = [contain]
        };
        containDocument.AddAsset(contain.AssetId!.Value, blob);
        var contained = TestImages.RenderPng(containDocument, true);
        Assert.That(Pixel(contained, 50, 50)[3], Is.EqualTo(255));
        Assert.That(Pixel(contained, 50, 5)[3], Is.Zero);
    }

    [Test]
    public void MissingImageFitUsesLegacyStretchOrSlantedCoverSemantics()
    {
        var pixels = new byte[8 * 4 * 4];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 8; x++)
        {
            var at = (y * 8 + x) * 4;
            pixels[at] = x < 4 ? (byte)10 : (byte)220;
            pixels[at + 3] = 255;
        }
        var blob = new AssetBlob(TestImages.EncodePng(8, 4, pixels));
        var flat = new Layer
        {
            Kind = LayerKind.Image, AssetId = Guid.NewGuid(),
            Bounds = new RectD(0, 0, 100, 100)
        };
        var flatDocument = new EditorDocument
        {
            CanvasWidth = 320, CanvasHeight = 180, Layers = [flat]
        };
        flatDocument.AddAsset(flat.AssetId!.Value, blob);
        var explicitStretch = flat.Copy();
        explicitStretch.ImageFit = ImageFitMode.Stretch;
        var explicitStretchDocument = flatDocument.Copy();
        explicitStretchDocument.Layers[0] = explicitStretch;
        Assert.That(TestImages.RenderPng(flatDocument, true),
            Is.EqualTo(TestImages.RenderPng(explicitStretchDocument, true)));

        var slanted = flat.Copy();
        slanted.Slanted = true;
        var slantedDocument = flatDocument.Copy();
        slantedDocument.Layers[0] = slanted;
        var explicitCover = slanted.Copy();
        explicitCover.ImageFit = ImageFitMode.Cover;
        var explicitCoverDocument = slantedDocument.Copy();
        explicitCoverDocument.Layers[0] = explicitCover;
        Assert.That(TestImages.RenderPng(slantedDocument, true),
            Is.EqualTo(TestImages.RenderPng(explicitCoverDocument, true)));
    }

    [Test]
    public void V8NativeAndTemporaryRoundTripKeepOpacityAndCrop()
    {
        var image = new Layer
        {
            Kind = LayerKind.Image, AssetId = Guid.NewGuid(),
            Bounds = new RectD(40, 30, 220, 120),
            Opacity = .35, ImageFit = ImageFitMode.Contain,
            ImageCrop = new RectD(.1, .2, .7, .6)
        };
        var document = new EditorDocument
        {
            CanvasWidth = 400, CanvasHeight = 300, Layers = [image]
        };
        document.AddAsset(image.AssetId!.Value,
            TestImages.EncodePng(8, 4, TestImages.SyntheticBgra(8, 4)));
        var native = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3-v6-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        var temporary = Path.ChangeExtension(native, ".json");
        try
        {
            NativePackageStore.Save(native, document, "r3-test");
            NativeDocumentStore.Save(temporary, document);
            Assert.That(NativePackageStore.Read(native).StateEquals(document), Is.True);
            Assert.That(NativeDocumentStore.Read(temporary).StateEquals(document), Is.True);
            using var archive = System.IO.Compression.ZipFile.OpenRead(native);
            using var reader = new StreamReader(archive.GetEntry("project.json")!.Open());
            var manifest = JsonNode.Parse(reader.ReadToEnd())!;
            Assert.That(manifest["schemaVersion"]!.GetValue<int>(), Is.EqualTo(8));
            var layer = manifest["layers"]![0]!;
            Assert.That(layer["opacity"]!.GetValue<double>(), Is.EqualTo(.35));
            Assert.That(layer["imageFit"]!.GetValue<string>(), Is.EqualTo("Contain"));
            Assert.That(layer["crop"]!["x"]!.GetValue<double>(), Is.EqualTo(.1));
        }
        finally
        {
            if (File.Exists(native)) File.Delete(native);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [Test]
    public void V5TemporaryMigrationDefaultsOpacityAndSlantedCoverWithoutRewriting()
    {
        var image = new Layer
        {
            Kind = LayerKind.Image, AssetId = Guid.NewGuid(),
            Bounds = new RectD(0, 0, 100, 100), Slanted = true
        };
        var document = new EditorDocument
        {
            CanvasWidth = 320, CanvasHeight = 180, Layers = [image]
        };
        document.AddAsset(image.AssetId!.Value,
            TestImages.EncodePng(8, 4, TestImages.SyntheticBgra(8, 4)));
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3-v5-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            NativeDocumentStore.Save(path, document);
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["FormatVersion"] = 5;
            LegacyFormatTestHelpers.RemoveV8BackgroundFields(json, temporary: true);
            var layer = json["Document"]!["Layers"]![0]!.AsObject();
            layer.Remove("Opacity"); layer.Remove("ImageFit"); layer.Remove("ImageCrop");
            File.WriteAllText(path, json.ToJsonString());
            var bytes = File.ReadAllBytes(path);
            var loaded = NativeDocumentStore.Read(path);
            Assert.That(loaded.Layers.Single().Opacity, Is.EqualTo(1));
            Assert.That(loaded.Layers.Single().EffectiveImageFit, Is.EqualTo(ImageFitMode.Cover));
            Assert.That(TestImages.RenderPng(loaded, true),
                Is.EqualTo(TestImages.RenderPng(document, true)));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void V6RequiresOpacityAndImageFieldsWhileV5RejectsThem()
    {
        var image = new Layer
        {
            Kind = LayerKind.Image, AssetId = Guid.NewGuid(),
            Bounds = new RectD(0, 0, 100, 100)
        };
        var document = new EditorDocument { Layers = [image] };
        document.AddAsset(image.AssetId!.Value,
            TestImages.EncodePng(4, 2, TestImages.SyntheticBgra(4, 2)));
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3-required-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, document, "r3-test");
            var manifest = Manifest(path);
            manifest["layers"]![0]!.AsObject().Remove("opacity");
            WriteManifest(path, manifest);
            Assert.That(() => NativePackageStore.Read(path), Throws.InstanceOf<InvalidDataException>());

            NativePackageStore.Save(path, document, "r3-test");
            manifest = Manifest(path);
            manifest["layers"]![0]!.AsObject().Remove("imageFit");
            WriteManifest(path, manifest);
            Assert.That(() => NativePackageStore.Read(path), Throws.InstanceOf<InvalidDataException>());

            NativePackageStore.Save(path, document, "r3-test");
            manifest = Manifest(path);
            manifest["schemaVersion"] = 5;
            WriteManifest(path, manifest);
            Assert.That(() => NativePackageStore.Read(path), Throws.InstanceOf<InvalidDataException>());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void InvalidOpacityOrCropIsRejectedAndTemplateKeepsSamplingSettings()
    {
        var layer = new Layer { Kind = LayerKind.Image, AssetId = Guid.NewGuid() };
        var document = new EditorDocument { Layers = [layer] };
        document.AddAsset(layer.AssetId!.Value,
            TestImages.EncodePng(4, 2, TestImages.SyntheticBgra(4, 2)));
        var invalidOpacity = document.Copy();
        invalidOpacity.Layers[0].Opacity = 1.1;
        Assert.That(() => invalidOpacity.Validate(), Throws.InstanceOf<InvalidDataException>());
        var invalidCrop = document.Copy();
        invalidCrop.Layers[0].ImageCrop = new RectD(.8, 0, .4, 1);
        Assert.That(() => invalidCrop.Validate(), Throws.InstanceOf<InvalidDataException>());
        layer.ImageFit = ImageFitMode.Cover;
        layer.ImageCrop = new RectD(.2, .1, .6, .7);
        layer.Opacity = .4;
        var edit = new EditSession(document);
        edit.SetSelection([layer.Id]);
        var captured = edit.CaptureLayoutTemplate("copy", [layer.Id]);
        edit.ApplyLayoutTemplate(captured, [layer.Id]);
        Assert.That(edit.Document.Find(layer.Id).ImageFit, Is.EqualTo(ImageFitMode.Cover));
        Assert.That(edit.Document.Find(layer.Id).ImageCrop, Is.EqualTo(new RectD(.2, .1, .6, .7)));
        Assert.That(edit.Document.Find(layer.Id).Opacity, Is.EqualTo(.4));
    }

    [Test]
    public void BackgroundCompositionHonorsImageOpacityAndCrop()
    {
        var image = new Layer
        {
            Kind = LayerKind.Image, AssetId = Guid.NewGuid(), Slanted = true,
            Bounds = new RectD(20, 20, 300, 160)
        };
        var document = new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, BackgroundBlend = new(true),
            Layers = [image]
        };
        document.AddAsset(image.AssetId!.Value,
            TestImages.EncodePng(8, 4, TestImages.SyntheticBgra(8, 4)));
        var full = BackgroundComposer.Generate(document, TestImages.Decoder);
        var translucent = document.Copy();
        translucent.Layers[0].Opacity = .4;
        Assert.That(BackgroundComposer.Generate(translucent, TestImages.Decoder),
            Is.Not.EqualTo(full));
        var cropped = document.Copy();
        cropped.Layers[0].ImageCrop = new RectD(.5, 0, .5, 1);
        Assert.That(BackgroundComposer.Generate(cropped, TestImages.Decoder),
            Is.Not.EqualTo(full));
        var invisible = document.Copy();
        invisible.Layers[0].Opacity = 0;
        Assert.That(() => BackgroundComposer.Generate(invisible, TestImages.Decoder),
            Throws.InstanceOf<InvalidOperationException>());
    }

    private static byte[] Pixel(byte[] png, int x, int y)
    {
        var (width, _, pixels) = TestImages.DecodePng(png);
        var offset = (y * width + x) * 4;
        return pixels[offset..(offset + 4)];
    }

    private static JsonObject Manifest(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry("project.json")!.Open());
        return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }

    private static void WriteManifest(string path, JsonNode manifest)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry("project.json")!.Delete();
        using var writer = new StreamWriter(archive.CreateEntry("project.json").Open());
        writer.Write(manifest.ToJsonString());
    }
}
