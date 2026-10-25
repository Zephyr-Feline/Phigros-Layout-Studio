using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Imaging.Windows;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5PreviewImageCacheTests
{
    [Test]
    public void RepaintingTheSameArtworkDoesNotDecodeItAgain()
    {
        AvaloniaTestHost.EnsureStarted();
        var fallback = new CountingFallback();
        var decoder = new AssetImageDecoder(fallback);
        var document = Document(new AssetBlob([1]));
        using var cache = new DecodedImageCache();
        for (var frame = 0; frame < 2; frame++) Render(document, decoder, cache);
        Assert.That(fallback.Calls, Is.EqualTo(1), "Repeated preview frames should reuse decoded artwork.");
    }

    [Test]
    public void ContentDeduplicationSharesPixelsAcrossLayerIdsAndTheFixedBackground()
    {
        var fallback = new CountingFallback(); var decoder = new AssetImageDecoder(fallback);
        var document = Document(new AssetBlob([1]));
        var second = Guid.NewGuid(); document.Assets.Add(second, new AssetBlob([1]));
        document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = second, Bounds = new(100, 90, 120, 90), Slanted = true });
        document.FixedBackgroundAssetId = second;
        using var cache = new DecodedImageCache();
        var png = Render(document, decoder, cache);
        Assert.That(fallback.Calls, Is.EqualTo(1));
        Assert.That(cache.Count, Is.EqualTo(1));
        Assert.That(png, Is.EqualTo(Render(document, decoder)));
    }

    [TestCase(3, 320, 240, false)]
    [TestCase(3, 480, 270, true)]
    [TestCase(5, 320, 240, true)]
    [TestCase(5, 480, 270, false)]
    public void RealPixelsRemainIdenticalForCanvasRatiosSlantsAndTransparentExport(int count, int width,
        int height, bool transparent)
    {
        var document = RealDocument(count, width, height);
        using var cache = new DecodedImageCache(); var background = new BackgroundRenderCache();
        var before = document.Copy();
        for (var frame = 0; frame < 3; frame++)
            Assert.That(Render(document, TestImages.Decoder, cache, background, transparent),
                Is.EqualTo(TestImages.RenderPng(document, transparent)));
        Assert.That(document.StateEquals(before), Is.True);
        Assert.That(cache.Count, Is.EqualTo(count + (transparent ? 0 : 1)));
    }

    [TestCase("png")]
    [TestCase("jpeg")]
    [TestCase("tiff")]
    public void ActivePlatformDecodersKeepTheirExistingPixels(string format)
    {
        var pixels = TestImages.SyntheticBgra(16, 8);
        byte[] bytes;
        if (format == "tiff") bytes = WicTiffEncoder.EncodeBgra(16, 8, pixels);
        else
        {
            using var bitmap = new DecodedRaster(16, 8, pixels).ToBitmap();
            using var stream = new MemoryStream();
            if (format == "jpeg") bitmap.Save(stream, new JpegBitmapEncoderOptions());
            else bitmap.Save(stream, new PngBitmapEncoderOptions());
            bytes = stream.ToArray();
        }
        var document = Document(new AssetBlob(bytes)); document.Layers[0].Slanted = true;
        using var cache = new DecodedImageCache();
        var expected = TestImages.RenderPng(document);
        Assert.That(Render(document, TestImages.Decoder, cache), Is.EqualTo(expected));
        Assert.That(Render(document, TestImages.Decoder, cache), Is.EqualTo(expected));
    }

    [TestCase("bounds")]
    [TestCase("slanted")]
    [TestCase("color")]
    [TestCase("canvas")]
    [TestCase("name")]
    public void GeometryAndStyleEditsReuseArtworkButKeepFreshDrawing(string change)
    {
        var fallback = new CountingFallback(); var decoder = new AssetImageDecoder(fallback);
        var document = Document(new AssetBlob([1])); using var cache = new DecodedImageCache();
        Render(document, decoder, cache);
        switch (change)
        {
            case "bounds": document.Layers[0].Bounds = new(80, 90, 130, 70); break;
            case "slanted": document.Layers[0].Slanted = true; break;
            case "color": document.BackgroundArgb = 0xFF993388; break;
            case "canvas": document.CanvasWidth = 400; break;
            case "name": document.Layers[0].Name = "Renamed"; break;
        }
        var actual = Render(document, decoder, cache);
        Assert.That(fallback.Calls, Is.EqualTo(1));
        Assert.That(actual, Is.EqualTo(Render(document, decoder)));
    }

    [Test]
    public void ReplacingHidingUndoAndRedoNeverReuseStalePixels()
    {
        var edit = new EditSession(Document(new AssetBlob(TestImages.EncodePng(16, 8, TestImages.SyntheticBgra(16, 8)))));
        using var cache = new DecodedImageCache(); var id = edit.Document.Layers[0].Id;
        var original = Render(edit.Document, TestImages.Decoder, cache);
        var newPixels = TestImages.SyntheticBgra(8, 16);
        edit.ReplaceImageLayer(id, TestImages.EncodePng(8, 16, newPixels));
        var replaced = Render(edit.Document, TestImages.Decoder, cache);
        Assert.That(replaced, Is.Not.EqualTo(original)); Assert.That(cache.Count, Is.EqualTo(1));
        Assert.That(replaced, Is.EqualTo(TestImages.RenderPng(edit.Document)));
        edit.Undo(); Assert.That(Render(edit.Document, TestImages.Decoder, cache), Is.EqualTo(original));
        edit.Redo(); Assert.That(Render(edit.Document, TestImages.Decoder, cache), Is.EqualTo(replaced));
        edit.UpdateLayer(id, layer => layer.Visible = false);
        Assert.That(Render(edit.Document, TestImages.Decoder, cache), Is.EqualTo(TestImages.RenderPng(edit.Document)));
        Assert.That(cache.Count, Is.Zero);
        edit.Undo(); Assert.That(Render(edit.Document, TestImages.Decoder, cache), Is.EqualTo(replaced));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void SameAssetIdWithChangedBytesAndDecoderChangesInvalidateThePixels()
    {
        var document = Document(new AssetBlob([1])); var first = new CountingFallback();
        var decoder = new AssetImageDecoder(first); using var cache = new DecodedImageCache();
        var original = Render(document, decoder, cache);
        document.Assets[document.Layers[0].AssetId!.Value] = new AssetBlob([2]);
        var replaced = Render(document, decoder, cache);
        Assert.That(replaced, Is.Not.EqualTo(original)); Assert.That(first.Calls, Is.EqualTo(2));
        var second = new CountingFallback(100); var otherDecoder = new AssetImageDecoder(second);
        var redecoded = Render(document, otherDecoder, cache);
        Assert.That(second.Calls, Is.EqualTo(1)); Assert.That(cache.Count, Is.EqualTo(1));
        Assert.That(redecoded, Is.Not.EqualTo(replaced));
        Assert.That(redecoded, Is.EqualTo(Render(document, otherDecoder)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LeastRecentlyUsedEvictionHonorsBothLimits(bool useEntryLimit)
    {
        var fallback = new CountingFallback(); var decoder = new AssetImageDecoder(fallback);
        using var cache = new DecodedImageCache(useEntryLimit ? 1024 : 128, useEntryLimit ? 2 : 64);
        AssetBlob a = new([1]), b = new([2]), c = new([3]);
        void Use(AssetBlob asset) => cache.Use(asset, decoder, _ => { });
        Use(a); Use(b); Use(a); Use(c); Use(a);
        Assert.That(fallback.Calls, Is.EqualTo(3), "The recently reused artwork must survive eviction.");
        Assert.That(cache.Count, Is.EqualTo(2)); Assert.That(cache.EstimatedPixelBytes, Is.EqualTo(128));
        Use(b); Assert.That(fallback.Calls, Is.EqualTo(4), "The least recently used image must decode again.");
        Assert.That(cache.EstimatedPixelBytes, Is.EqualTo(128));
    }

    [TestCase(1)]
    [TestCase(2)]
    public void EvictionDuringADrawingContextPreservesAllRecordedImagePixels(int entries)
    {
        var document = RealDocument(5, 480, 270);
        using var cache = new DecodedImageCache(entryLimit: entries);
        var expected = TestImages.RenderPng(document);
        Assert.That(Render(document, TestImages.Decoder, cache), Is.EqualTo(expected));
        Assert.That(Render(document, TestImages.Decoder, cache), Is.EqualTo(expected));
        Assert.That(cache.Count, Is.EqualTo(entries));
    }

    [TestCase(0)]
    [TestCase(63)]
    public void OversizedImagesUseTheOriginalPerDrawLifetimeWithoutEvictingResidentPixels(int budget)
    {
        var fallback = new CountingFallback(); var decoder = new AssetImageDecoder(fallback);
        using var cache = new DecodedImageCache(budget);
        cache.Use(new([1]), decoder, _ => { }); cache.Use(new([1]), decoder, _ => { });
        Assert.That(fallback.Calls, Is.EqualTo(2)); Assert.That(cache.Count, Is.Zero);
        Assert.That(cache.EstimatedPixelBytes, Is.Zero);
    }

    [Test]
    public void AnOversizedImageDoesNotEvictASmallerResidentImage()
    {
        AssetBlob small = new(TestImages.EncodePng(4, 4, TestImages.SyntheticBgra(4, 4)));
        AssetBlob large = new(TestImages.EncodePng(16, 8, TestImages.SyntheticBgra(16, 8)));
        using var cache = new DecodedImageCache(128);
        Bitmap? original = null;
        cache.Use(small, TestImages.Decoder, image => original = image);
        cache.Use(large, TestImages.Decoder, image => Assert.That(image.PixelSize, Is.EqualTo(new PixelSize(16, 8))));
        cache.Use(small, TestImages.Decoder, image => Assert.That(image, Is.SameAs(original)));
        Assert.That(cache.Count, Is.EqualTo(1)); Assert.That(cache.EstimatedPixelBytes, Is.EqualTo(64));
    }

    [Test]
    public void TransparentRenderingRetiresFixedBackgroundPixelsAndUnusedAssets()
    {
        var document = RealDocument(1, 320, 240);
        var fixedId = Guid.NewGuid(); document.AddAsset(fixedId, TestImages.EncodePng(4, 4, TestImages.SyntheticBgra(4, 4)));
        document.FixedBackgroundAssetId = fixedId;
        var unused = Guid.NewGuid(); document.AddAsset(unused, TestImages.EncodePng(5, 5, TestImages.SyntheticBgra(5, 5)));
        using var cache = new DecodedImageCache();
        Render(document, TestImages.Decoder, cache); Assert.That(cache.Count, Is.EqualTo(2));
        Assert.That(Render(document, TestImages.Decoder, cache, transparent: true), Is.EqualTo(TestImages.RenderPng(document, true)));
        Assert.That(cache.Count, Is.EqualTo(1));
        document.Layers[0].Visible = false;
        Render(document, TestImages.Decoder, cache, transparent: true); Assert.That(cache.Count, Is.Zero);
    }

    [Test]
    public void DecodeAndDrawingFailuresLeaveTheCacheUsable()
    {
        using var cache = new DecodedImageCache();
        Assert.Throws<InvalidDataException>(() => cache.Use(new([1]), TestImages.Decoder, _ => { }));
        Assert.That(cache.Count, Is.Zero);
        var fallback = new CountingFallback(); var decoder = new AssetImageDecoder(fallback);
        Assert.Throws<IOException>(() => cache.Use(new([2]), decoder, _ => throw new IOException("draw failed")));
        cache.Use(new([2]), decoder, _ => { });
        Assert.That(fallback.Calls, Is.EqualTo(1)); Assert.That(cache.Count, Is.EqualTo(1));
        cache.Clear(); Assert.That(cache.EstimatedPixelBytes, Is.Zero);
        cache.Use(new([2]), decoder, _ => { }); Assert.That(fallback.Calls, Is.EqualTo(2));
        cache.Dispose(); cache.Dispose(); Assert.That(cache.Count, Is.Zero);
        Assert.Throws<ObjectDisposedException>(() => cache.Use(new([2]), decoder, _ => { }));
        Assert.Throws<ObjectDisposedException>(() => cache.Retain([], decoder));
    }

    [Test]
    public void MovingArtworkDoesNotAccumulateDerivedBackgroundImages()
    {
        var edit = new EditSession(RealDocument(3, 320, 240));
        using var cache = new DecodedImageCache(); var background = new BackgroundRenderCache();
        var original = Render(edit.Document, TestImages.Decoder, cache, background);
        edit.BeginGesture(edit.Document.Layers[0].Id);
        for (var frame = 0; frame < 12; frame++)
        {
            edit.MoveBy(frame * 2, frame);
            Assert.That(Render(edit.Document, TestImages.Decoder, cache, background), Is.EqualTo(TestImages.RenderPng(edit.Document)));
            Assert.That(cache.Count, Is.EqualTo(4));
        }
        edit.CancelGesture();
        Assert.That(Render(edit.Document, TestImages.Decoder, cache, background), Is.EqualTo(original));
        Assert.That(edit.UndoCount, Is.Zero);
        edit.SetFixedBackground(TestImages.EncodePng(4, 4, TestImages.SyntheticBgra(4, 4)));
        Assert.That(Render(edit.Document, TestImages.Decoder, cache, background), Is.EqualTo(TestImages.RenderPng(edit.Document)));
        edit.Undo(); Assert.That(Render(edit.Document, TestImages.Decoder, cache, background), Is.EqualTo(original));
        foreach (var layer in edit.Document.Layers) layer.Visible = false;
        Render(edit.Document, TestImages.Decoder, cache, background); Assert.That(cache.Count, Is.Zero);
    }

    [TestCase("three-columns", 3)]
    [TestCase("five-point", 5)]
    public void TemplatePlacementAndNativeReopenMatchFreshExport(string template, int count)
    {
        var edit = new EditSession(RealDocument(count, 1440, 1080));
        using var cache = new DecodedImageCache(); var background = new BackgroundRenderCache();
        Render(edit.Document, TestImages.Decoder, cache, background);
        edit.ApplyLayoutTemplate(template, edit.Document.Layers.Select(layer => layer.Id));
        var actual = Render(edit.Document, TestImages.Decoder, cache, background);
        Assert.That(actual, Is.EqualTo(TestImages.RenderPng(edit.Document)));
        var path = Path.Combine(Path.GetTempPath(), "preview-cache-" + Guid.NewGuid() + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "cache-tests");
            var reopened = NativePackageStore.Read(path);
            Assert.That(Render(reopened, TestImages.Decoder, cache, background), Is.EqualTo(actual));
        }
        finally { File.Delete(path); }
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void TwoHundredLayersShareFiveDecodedArtworksAndRespectTheBudget()
    {
        var document = RealDocument(5, 480, 270); document.BackgroundBlend = new();
        var originals = document.Layers.ToArray();
        while (document.Layers.Count < 200)
        {
            var layer = originals[document.Layers.Count % 5].Copy(); layer.Id = Guid.NewGuid();
            layer.Bounds = layer.Bounds with { X = document.Layers.Count % 20 * 12, Y = document.Layers.Count / 20 * 10 };
            document.Layers.Add(layer);
        }
        using var cache = new DecodedImageCache();
        Assert.That(Render(document, TestImages.Decoder, cache), Is.EqualTo(TestImages.RenderPng(document)));
        Assert.That(cache.Count, Is.EqualTo(5));
        Assert.That(cache.EstimatedPixelBytes, Is.LessThanOrEqualTo(DecodedImageCache.DefaultPixelBudget));
    }

    [Test]
    public void ActualCanvasCachesPixelsAndClearsThemWhenDetachedThenCanBeReattached()
    {
        var edit = new EditSession(RealDocument(3, 320, 240));
        var canvas = new SceneCanvas(edit); var window = new Window { Width = 640, Height = 480, Content = canvas };
        var cache = (DecodedImageCache)typeof(SceneCanvas).GetField("_imageCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(canvas)!;
        try
        {
            window.Show(); Paint(); Assert.That(cache.Count, Is.EqualTo(4));
            var point = new ViewportTransform(320, 240, canvas.Bounds.Width, canvas.Bounds.Height).ToView(new PointD(75, 45));
            window.MouseDown(new(point.X, point.Y), MouseButton.Left);
            Assert.That(edit.SelectedLayerId, Is.EqualTo(edit.Document.Layers[0].Id));
            window.MouseMove(new(point.X + 30, point.Y + 12), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new(point.X + 30, point.Y + 12), MouseButton.Left);
            Assert.That(edit.UndoCount, Is.EqualTo(1)); Paint();
            edit.Undo(); Paint(); Assert.That(edit.IsDirty, Is.False);
            edit.ReplaceDocument(new EditorDocument { CanvasWidth = 320, CanvasHeight = 240 }); canvas.Refresh(); Paint();
            Assert.That(cache.Count, Is.Zero);
            edit.ReplaceDocument(RealDocument(3, 320, 240)); Paint(); Assert.That(cache.Count, Is.EqualTo(4));
            window.Content = null; Assert.That(cache.Count, Is.Zero);
            window.Content = canvas; Paint(); Assert.That(cache.Count, Is.EqualTo(4));
        }
        finally { window.Close(); }
        Assert.That(cache.Count, Is.Zero);
        Assert.That(cache.EstimatedPixelBytes, Is.Zero);

        void Paint()
        {
            using var target = new RenderTargetBitmap(new(640, 480), new(96, 96));
            using var context = target.CreateDrawingContext(); canvas.Render(context);
        }
    }

    private static EditorDocument RealDocument(int count, int width, int height)
    {
        var document = new EditorDocument { CanvasWidth = width, CanvasHeight = height,
            BackgroundBlend = new(true, IncludeFlatImages: true) };
        for (var i = 0; i < count; i++)
        {
            var w = i % 2 == 0 ? 16 : 12; var pixels = TestImages.SyntheticBgra(w, 9);
            for (var p = 0; p < pixels.Length; p += 4) { pixels[p] = (byte)(pixels[p] + i * 30); pixels[p + 3] = (byte)(i % 2 == 0 ? 255 : 160); }
            var id = Guid.NewGuid(); document.AddAsset(id, TestImages.EncodePng(w, 9, pixels));
            document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = id, Slanted = i % 2 == 0,
                Bounds = new(40 + i * 38, 40 + i * 10, 70, 90) });
        }
        return document;
    }

    private static EditorDocument Document(AssetBlob image)
    {
        var document = new EditorDocument { CanvasWidth = 320, CanvasHeight = 240, BackgroundBlend = new() };
        var id = Guid.NewGuid(); document.AddAsset(id, image);
        document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = id, Bounds = new(40, 40, 160, 120) });
        return document;
    }

    private static byte[] Render(EditorDocument document, AssetImageDecoder decoder, DecodedImageCache? cache = null,
        BackgroundRenderCache? background = null, bool transparent = false)
    {
        using var target = new RenderTargetBitmap(new(document.CanvasWidth, document.CanvasHeight), new(96, 96));
        using (var context = target.CreateDrawingContext())
            ScenePainter.Paint(context, document, decoder, transparent, background, cache);
        using var stream = new MemoryStream(); target.Save(stream, new PngBitmapEncoderOptions()); return stream.ToArray();
    }

    private sealed class CountingFallback(byte colorOffset = 0) : IImageFallbackDecoder
    {
        public int Calls { get; private set; }
        public DecodedRaster? TryDecode(AssetBlob asset)
        {
            Calls++;
            var value = asset.ToArray()[0];
            return new(4, 4, Enumerable.Repeat(new byte[] { (byte)(40 + value + colorOffset), 70, 190, 255 }, 16).SelectMany(p => p).ToArray());
        }
    }
}
