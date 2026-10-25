using Avalonia.LogicalTree;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4SlantedLayerTests
{
    [TestCase(400, 300)]
    [TestCase(800, 450)]
    public void ShapePixelsAndLogicalHitTestUseTheSameFreeLayoutPolygon(int canvasWidth, int canvasHeight)
    {
        var bounds = new RectD(20, 20, 120, 120);
        var shape = new Layer { Kind = LayerKind.Shape, Bounds = bounds,
            ColorArgb = 0xFFFFFFFF, Slanted = true };
        var document = new EditorDocument { CanvasWidth = canvasWidth, CanvasHeight = canvasHeight,
            Layers = [shape] };
        var (_, _, pixels) = TestImages.DecodePng(TestImages.RenderPng(document, true));
        byte Alpha(int x, int y) => pixels[(y * canvasWidth + x) * 4 + 3];
        Assert.Multiple(() =>
        {
            Assert.That(SlantedLayerGeometry.Inset(bounds), Is.EqualTo(32.16).Within(1e-9));
            Assert.That(Alpha(25, 25), Is.Zero);
            Assert.That(Alpha(135, 25), Is.EqualTo(255));
            Assert.That(Alpha(25, 135), Is.EqualTo(255));
            Assert.That(Alpha(135, 135), Is.Zero);
            Assert.That(SlantedLayerGeometry.Contains(bounds, new PointD(25, 25), true), Is.False);
            Assert.That(SlantedLayerGeometry.Contains(bounds, new PointD(135, 25), true), Is.True);
            Assert.That(SlantedLayerGeometry.Contains(bounds, new PointD(25, 135), true), Is.True);
            Assert.That(SlantedLayerGeometry.Contains(bounds, new PointD(135, 135), true), Is.False);
            Assert.That(SlantedLayerGeometry.Contains(bounds, new PointD(25, 25), false), Is.True);
        });
        Assert.That(SlantedLayerGeometry.Inset(new RectD(0, 0, 10, 100)), Is.EqualTo(4),
            "narrow layers use the width cap, not the classic badge slant");
    }

    [Test]
    public void SlantedImageCoversThenClipsWhileFlatImageKeepsItsPreviousStretch()
    {
        var image = SixByTwoStripe();
        var id = Guid.NewGuid();
        var layer = new Layer { Kind = LayerKind.Image, AssetId = id,
            Bounds = new RectD(20, 20, 120, 120), Slanted = true };
        var document = new EditorDocument { CanvasWidth = 400, CanvasHeight = 300,
            Layers = [layer] };
        document.AddAsset(id, image);
        var (_, _, slanted) = TestImages.DecodePng(TestImages.RenderPng(document, true));
        static byte[] Pixel(byte[] pixels, int x, int y) => pixels[((y * 400 + x) * 4)..][..4];
        Assert.Multiple(() =>
        {
            Assert.That(Pixel(slanted, 25, 25)[3], Is.Zero);
            Assert.That(Pixel(slanted, 135, 135)[3], Is.Zero);
            Assert.That(Pixel(slanted, 45, 80)[1], Is.GreaterThan(Pixel(slanted, 45, 80)[2]),
                "the square cover crop samples the green centre, not the red outer stripe");
        });
        layer.Slanted = false;
        var (_, _, flat) = TestImages.DecodePng(TestImages.RenderPng(document, true));
        Assert.Multiple(() =>
        {
            Assert.That(Pixel(flat, 25, 25)[3], Is.EqualTo(255));
            Assert.That(Pixel(flat, 45, 80)[2], Is.GreaterThan(Pixel(flat, 45, 80)[1]),
                "old flat-image stretch still shows the red outer stripe");
        });
    }

    [Test]
    public void CanvasClickFallsThroughTransparentSlantedCorner()
    {
        AvaloniaTestHost.EnsureStarted();
        var back = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(20, 20, 120, 120) };
        var front = new Layer { Kind = LayerKind.Shape, Bounds = back.Bounds, Slanted = true };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 400, CanvasHeight = 300,
            Layers = [back, front] });
        var canvas = new SceneCanvas(edit);
        var window = new Window { Width = 800, Height = 600, Content = canvas };
        try
        {
            window.Show();
            var transform = new ViewportTransform(400, 300, canvas.Bounds.Width, canvas.Bounds.Height);
            Point View(double x, double y)
            {
                var point = transform.ToView(new PointD(x, y));
                return new Point(point.X, point.Y);
            }
            var corner = View(25, 25);
            window.MouseDown(corner, MouseButton.Left);
            window.MouseUp(corner, MouseButton.Left);
            Assert.That(edit.SelectedLayerId, Is.EqualTo(back.Id));
            var interior = View(80, 80);
            window.MouseDown(interior, MouseButton.Left);
            window.MouseUp(interior, MouseButton.Left);
            Assert.That(edit.SelectedLayerId, Is.EqualTo(front.Id));
        }
        finally { window.Close(); }
    }

    [Test]
    public void NewBackgroundUsesOnlyVisibleSlantedArtworkAndInvalidatesItsCache()
    {
        AvaloniaTestHost.EnsureStarted();
        var document = new EditorDocument { CanvasWidth = 400, CanvasHeight = 300,
            BackgroundBlend = new(true) };
        var red = Guid.NewGuid();
        var blue = Guid.NewGuid();
        document.AddAsset(red, P4AutomaticBackgroundTests.Solid(0xFFFF0000));
        document.AddAsset(blue, P4AutomaticBackgroundTests.Solid(0xFF0000FF));
        document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = red,
            Slanted = true, Bounds = new RectD(40, 30, 100, 100) });
        document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = blue,
            Bounds = new RectD(240, 30, 100, 100) });
        var cache = new BackgroundRenderCache();
        var first = cache.Get(document, TestImages.Decoder)!;
        Assert.That(first, Is.Not.Null);
        using (var target = new RenderTargetBitmap(new PixelSize(400, 300), new Vector(96, 96)))
        {
            using (var context = target.CreateDrawingContext())
                ScenePainter.Paint(context, document, TestImages.Decoder, backgroundCache: cache);
            using var stream = new MemoryStream();
            target.Save(stream, new PngBitmapEncoderOptions());
            Assert.That(stream.ToArray(), Is.EqualTo(TestImages.RenderPng(document)),
                "cached preview and fresh PNG must agree with slanted-only composition");
        }
        document.Layers[1].Visible = false;
        Assert.That(cache.Get(document, TestImages.Decoder), Is.SameAs(first),
            "flat artwork is not a contributor in new projects");
        document.Layers[1].Visible = true;
        document.Layers[1].Slanted = true;
        var second = cache.Get(document, TestImages.Decoder)!;
        Assert.That(second, Is.Not.SameAs(first));
        Assert.That(second.ToArray(), Is.Not.EqualTo(first.ToArray()));
        Assert.That(second.ToArray(), Is.EqualTo(BackgroundComposer.Generate(document, TestImages.Decoder)));
        document.Layers[0].Visible = false;
        document.Layers[1].Visible = false;
        Assert.That(cache.Get(document, TestImages.Decoder), Is.Null);
        document.Layers[1].Visible = true;
        document.Layers[1].Slanted = false;
        Assert.Throws<InvalidOperationException>(() =>
            BackgroundComposer.Generate(document, TestImages.Decoder));
    }

    [Test]
    public void SlantedBackgroundSamplesCoverCropAndCacheTracksTheSamplingMode()
    {
        AvaloniaTestHost.EnsureStarted();
        var id = Guid.NewGuid();
        var layer = new Layer { Kind = LayerKind.Image, AssetId = id,
            Bounds = new RectD(20, 20, 120, 120) };
        var document = new EditorDocument { CanvasWidth = 400, CanvasHeight = 300,
            BackgroundBlend = new(true, BlurRadius: 0, Darkness: 0, IncludeFlatImages: true),
            Layers = [layer] };
        document.AddAsset(id, SixByTwoStripe());
        var cache = new BackgroundRenderCache();
        var flat = cache.Get(document, TestImages.Decoder)!;
        var (width, height, flatPixels) = TestImages.DecodePng(flat.ToArray());
        Assert.That((width, height), Is.EqualTo((256, 192)));
        var at = (height / 2 * width) * 4;
        Assert.That(flatPixels[at + 2], Is.GreaterThan(flatPixels[at + 1]),
            "legacy flat-image sampling keeps its previous stretched red edge");

        layer.Slanted = true;
        var slanted = cache.Get(document, TestImages.Decoder)!;
        var (_, _, slantedPixels) = TestImages.DecodePng(slanted.ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(slanted, Is.Not.SameAs(flat));
            Assert.That(slantedPixels[at + 1], Is.GreaterThan(slantedPixels[at + 2]),
                "slanted artwork samples the green centre after a square cover crop");
            Assert.That(slanted.ToArray(), Is.EqualTo(BackgroundComposer.Generate(document, TestImages.Decoder)));
        });
    }

    [Test]
    public void SchemaV3MigratesFlatImageAndAllImageBlendWithoutTouchingSource()
    {
        var directory = Directory.CreateTempSubdirectory("ppg-slant-v3-").FullName;
        try
        {
            var document = new EditorDocument { CanvasWidth = 400, CanvasHeight = 300,
                BackgroundBlend = new(true, IncludeFlatImages: true) };
            var id = Guid.NewGuid();
            document.AddAsset(id, SixByTwoStripe());
            document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = id,
                Bounds = new RectD(20, 20, 120, 120) });
            var originalPixels = TestImages.RenderPng(document);
            var path = Path.Combine(directory, "old-v3.ppgproj");
            NativePackageStore.Save(path, document, "p4-test");
            var manifest = ReadManifest(path);
            manifest["schemaVersion"] = 3;
            manifest["backgroundBlend"]!.AsObject().Remove("includeFlatImages");
            LegacyFormatTestHelpers.RemoveV8BackgroundFields(manifest, temporary: false);
            foreach (var layer in manifest["layers"]!.AsArray())
            {
                layer!.AsObject().Remove("slanted");
                layer.AsObject().Remove("opacity");
                layer.AsObject().Remove("imageFit");
                layer.AsObject().Remove("crop");
            }
            ReplaceManifest(path, manifest);
            var bytes = File.ReadAllBytes(path);
            var stamp = File.GetLastWriteTimeUtc(path);
            var migrated = NativePackageStore.Read(path);
            Assert.Multiple(() =>
            {
                Assert.That(migrated.StateEquals(document), Is.True);
                Assert.That(migrated.BackgroundBlend.IncludeFlatImages, Is.True);
                Assert.That(migrated.Layers.Single().Slanted, Is.False);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
                Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(stamp));
            });
            Assert.That(TestImages.RenderPng(migrated), Is.EqualTo(originalPixels));
            var saved = Path.Combine(directory, "upgraded.ppgproj");
            NativePackageStore.Save(saved, migrated, "p4-test");
            Assert.That(ReadManifest(saved)["schemaVersion"]!.GetValue<int>(), Is.EqualTo(NativePackageStore.SchemaVersion));
            Assert.That(NativePackageStore.Read(saved).StateEquals(migrated), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void TemporaryV3MigratesAllImageContributionInMemory()
    {
        var directory = Directory.CreateTempSubdirectory("ppg-slant-json-").FullName;
        try
        {
            var document = new EditorDocument
                { BackgroundBlend = new(true, IncludeFlatImages: true) };
            var id = Guid.NewGuid();
            document.AddAsset(id, P4AutomaticBackgroundTests.Solid(0xFFAA3311));
            document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = id });
            var path = Path.Combine(directory, "old-v3.json");
            NativeDocumentStore.Save(path, document);
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["FormatVersion"] = 3;
            json["Document"]!["BackgroundBlend"]!.AsObject().Remove("IncludeFlatImages");
            LegacyFormatTestHelpers.RemoveV8BackgroundFields(json, temporary: true);
            json["Document"]!["Layers"]![0]!.AsObject().Remove("Slanted");
            json["Document"]!["Layers"]![0]!.AsObject().Remove("Opacity");
            json["Document"]!["Layers"]![0]!.AsObject().Remove("ImageFit");
            json["Document"]!["Layers"]![0]!.AsObject().Remove("ImageCrop");
            File.WriteAllText(path, json.ToJsonString());
            var bytes = File.ReadAllBytes(path);
            var migrated = NativeDocumentStore.Read(path);
            Assert.Multiple(() =>
            {
                Assert.That(migrated.StateEquals(document), Is.True);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            });
            Assert.That(TestImages.RenderPng(migrated), Is.EqualTo(TestImages.RenderPng(document)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestCase("missing-slanted")]
    [TestCase("null-slanted")]
    [TestCase("missing-policy")]
    [TestCase("old-with-slanted")]
    [TestCase("old-with-policy")]
    public void VersionedSlantFieldsAreStrictAndFailedReadPreservesSource(string change)
    {
        var directory = Directory.CreateTempSubdirectory("ppg-slant-invalid-").FullName;
        try
        {
            var path = Path.Combine(directory, "invalid.ppgproj");
            var document = new EditorDocument { Layers = [new Layer { Kind = LayerKind.Shape }] };
            NativePackageStore.Save(path, document, "p4-test");
            var manifest = ReadManifest(path);
            var layer = manifest["layers"]![0]!.AsObject();
            switch (change)
            {
                case "missing-slanted": layer.Remove("slanted"); break;
                case "null-slanted": layer["slanted"] = null; break;
                case "missing-policy": manifest["backgroundBlend"]!.AsObject().Remove("includeFlatImages"); break;
                case "old-with-slanted":
                    manifest["schemaVersion"] = 3;
                    manifest["backgroundBlend"]!.AsObject().Remove("includeFlatImages");
                    break;
                case "old-with-policy":
                    manifest["schemaVersion"] = 3;
                    layer.Remove("slanted");
                    break;
            }
            ReplaceManifest(path, manifest);
            var bytes = File.ReadAllBytes(path);
            Assert.Throws<InvalidDataException>(() => NativePackageStore.Read(path));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void PropertyPanelTogglesSlantAsOneUndoableEdit()
    {
        AvaloniaTestHost.EnsureStarted();
        var shape = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(20, 20, 120, 120) };
        var edit = new EditSession(new EditorDocument { Layers = [shape] });
        edit.SelectedLayerId = shape.Id;
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var slant = window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(control => control.Name == "LayerSlanted");
            var apply = UiTestActions.FindNamed(window, "ApplyLayerProperties");
            Assert.That(slant.IsVisible, Is.True);
            slant.IsChecked = true;
            UiTestActions.Click(apply);
            Assert.That(edit.Document.Find(shape.Id).Slanted, Is.True);
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(shape.Id).Slanted, Is.False);
            Assert.That(edit.Redo(), Is.True);
            Assert.That(edit.Document.Find(shape.Id).Slanted, Is.True);
        }
        finally { window.Close(); }
    }

    private static byte[] SixByTwoStripe()
    {
        var pixels = new byte[6 * 2 * 4];
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 6; x++)
        {
            var at = (y * 6 + x) * 4;
            if (x < 2) pixels[at + 2] = 255;
            else if (x < 4) pixels[at + 1] = 255;
            else pixels[at] = 255;
            pixels[at + 3] = 255;
        }
        return TestImages.EncodePng(6, 2, pixels);
    }

    private static JsonObject ReadManifest(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry("project.json")!.Open();
        return JsonNode.Parse(stream)!.AsObject();
    }

    private static void ReplaceManifest(string path, JsonObject manifest)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry("project.json")!.Delete();
        using var writer = new StreamWriter(archive.CreateEntry("project.json").Open());
        writer.Write(manifest.ToJsonString());
    }
}
