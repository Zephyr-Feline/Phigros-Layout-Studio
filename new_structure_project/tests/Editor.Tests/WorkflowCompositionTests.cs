using Avalonia;
using Avalonia.Media.Imaging;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class WorkflowCompositionTests
{
    [TestCase(3, 960, 540)]
    [TestCase(5, 960, 540)]
    [TestCase(3, 800, 600)]
    [TestCase(5, 800, 600)]
    [TestCase(3, 1370, 540)]
    [TestCase(5, 1370, 540)]
    [TestCase(3, 540, 960)]
    [TestCase(5, 540, 960)]
    public void EditedSongBatchKeepsOneVisibleSongAndItsPixelsAfterNativeReopen(int count, int width, int height)
    {
        var edit = new EditSession(Batch(count, width, height));
        var groups = edit.Document.Layers.GroupBy(layer => layer.GroupId).ToArray();
        var title = groups[0].Single(layer => layer.Name.EndsWith(" · title", StringComparison.Ordinal));
        var charter = groups[0].Single(layer => layer.Name.EndsWith(" · charter", StringComparison.Ordinal));
        var artwork = groups.Select(group => group.Single(layer => layer.Kind == LayerKind.Image)).ToArray();
        edit.UpdateDocument(document =>
        {
            document.Find(title.Id).Text = string.Concat(Enumerable.Repeat("很长的曲名 Long song title ", 20));
            document.Find(title.Id).LineSpacing = 1.3;
            document.Find(charter.Id).Text = "";
            foreach (var badge in document.Layers.Where(layer => layer.Kind == LayerKind.Badge))
                badge.Level = 16;
        });
        using var images = new DecodedImageCache();
        var background = new BackgroundRenderCache();
        var allSongs = edit.Document.Copy();
        var allPng = Preview(edit.Document, images, background);
        var allBackground = background.Get(edit.Document, TestImages.Decoder)!.ToArray();
        Assert.That(allPng, Is.EqualTo(TestImages.RenderPng(edit.Document)));
        foreach (var image in artwork)
            AssertArtworkCentre(allPng, image, ArtworkColors[Array.IndexOf(artwork, image)]);

        edit.UpdateDocument(document =>
        {
            foreach (var layer in document.Layers.Where(layer => layer.GroupId != groups[0].Key))
                layer.Visible = false;
        });
        var oneSong = edit.Document.Copy();
        var onePng = Preview(edit.Document, images, background);
        Assert.That(onePng, Is.EqualTo(TestImages.RenderPng(edit.Document)));
        Assert.That(background.Get(edit.Document, TestImages.Decoder)!.ToArray(), Is.Not.EqualTo(allBackground));
        var onlyVisible = oneSong.Copy();
        onlyVisible.Layers.RemoveAll(layer => !layer.Visible);
        onlyVisible.RemoveUnreferencedAssets();
        Assert.That(BackgroundComposer.Generate(oneSong, TestImages.Decoder),
            Is.EqualTo(BackgroundComposer.Generate(onlyVisible, TestImages.Decoder)),
            "hidden songs contribute neither their pixels nor their spatial weight to the background");

        var transparent = TestImages.RenderPng(oneSong, true);
        AssertArtworkCentre(transparent, artwork[0], ArtworkColors[0]);
        foreach (var image in artwork.Skip(1))
            Assert.That(PixelAt(transparent, Centre(image.Bounds))[3], Is.Zero, "hidden song foreground");
        Assert.That(PixelAt(transparent, new(1, 1))[3], Is.Zero);
        var noCharter = oneSong.Copy();
        noCharter.Find(charter.Id).Visible = false;
        Assert.That(TestImages.RenderPng(noCharter, true), Is.EqualTo(transparent), "empty charter paints no ink");

        var noTitle = oneSong.Copy();
        noTitle.Find(title.Id).Text = "";
        AssertDifferencesInside(transparent, TestImages.RenderPng(noTitle, true), [title.Bounds]);

        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.StateEquals(allSongs), Is.True);
        Assert.That(Preview(edit.Document, images, background), Is.EqualTo(allPng), "undo refreshes the composition cache");
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.StateEquals(oneSong), Is.True);
        Assert.That(Preview(edit.Document, images, background), Is.EqualTo(onePng));
        AssertNativeReopen(edit, width / 2 + 1);
    }

    [TestCase(3)]
    [TestCase(5)]
    public void FixedSnapshotStaysFrozenThroughArtworkEditsAndTransparentExportSkipsIt(int count)
    {
        var edit = new EditSession(Batch(count, 800, 600));
        var art = edit.Document.Layers.Where(layer => layer.Kind == LayerKind.Image).ToArray();
        edit.SetFixedBackground(BackgroundComposer.Generate(edit.Document, TestImages.Decoder));
        var frozen = edit.Document.Copy();
        using var images = new DecodedImageCache();
        var background = new BackgroundRenderCache();
        var frozenPng = Preview(edit.Document, images, background);
        var hiddenGroupBounds = edit.Document.Layers.Where(layer => layer.GroupId == art[1].GroupId)
            .Select(layer => layer.Bounds).ToArray();
        var replacement = Artwork(64, 32, 0xFF00F040);
        edit.ReplaceImageLayer(art[0].Id, replacement.ToArray());
        edit.UpdateDocument(document =>
        {
            foreach (var layer in document.Layers.Where(layer => layer.GroupId == art[1].GroupId))
                layer.Visible = false;
        });
        edit.UpdateDocument(document => document.BackgroundBlend = new(true, BlurRadius: 18, Darkness: .8));
        var edited = edit.Document.Copy();
        var editedPng = Preview(edit.Document, images, background);
        AssertDifferencesInside(frozenPng, editedPng, hiddenGroupBounds.Append(art[0].Bounds).ToArray());
        AssertArtworkCentre(editedPng, art[0], 0xFF00F040);
        Assert.That(editedPng, Is.EqualTo(TestImages.RenderPng(edit.Document)));
        var noBackground = edit.Document.Copy();
        noBackground.FixedBackgroundAssetId = null;
        noBackground.BackgroundBlend = new(false);
        Assert.That(TestImages.RenderPng(edit.Document, true), Is.EqualTo(TestImages.RenderPng(noBackground, true)),
            "transparent PNG omits the fixed image, automatic background and canvas base color");
        Assert.That(PixelAt(TestImages.RenderPng(edit.Document, true), new(1, 1))[3], Is.Zero);

        for (var i = 0; i < 3; i++) Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.StateEquals(frozen), Is.True);
        Assert.That(Preview(edit.Document, images, background), Is.EqualTo(frozenPng));
        for (var i = 0; i < 3; i++) Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.StateEquals(edited), Is.True);
        Assert.That(Preview(edit.Document, images, background), Is.EqualTo(editedPng));
        AssertNativeReopen(edit, 601);

        edit.ClearFixedBackground();
        var automaticPng = Preview(edit.Document, images, background);
        Assert.That(automaticPng, Is.EqualTo(TestImages.RenderPng(edit.Document)));
        Assert.That(PixelAt(automaticPng, new(1, 1)), Is.Not.EqualTo(PixelAt(editedPng, new(1, 1))),
            "clearing the fixed image resumes the edited automatic background settings");
        Assert.That(edit.Undo(), Is.True);
        Assert.That(Preview(edit.Document, images, background), Is.EqualTo(editedPng));
    }

    private static readonly uint[] ArtworkColors = [0xFFF8F8F8, 0xFF080808, 0xFFFF2030, 0xFF2050FF, 0xFFFFD020];

    private static EditorDocument Batch(int count, int width, int height)
    {
        AvaloniaTestHost.EnsureStarted();
        var sizes = new[] { (96, 24), (24, 96), (48, 48), (80, 40), (40, 80) };
        var document = SongBatch.Create(width, height, Enumerable.Range(0, count)
            .Select(i => new SongArtwork($"歌曲 {i + 1}", Artwork(sizes[i].Item1, sizes[i].Item2, ArtworkColors[i]))).ToArray());
        document.BackgroundArgb = 0xFF102030;
        document.BackgroundBlend = new(true, BlurRadius: 4, Darkness: .15);
        TestImages.ValidateImages(document);
        return document;
    }

    private static AssetBlob Artwork(int width, int height, uint argb)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var at = (y * width + x) * 4;
            var color = x == 0 || y == 0 || x == width - 1 || y == height - 1 ? 0xFF9030A0 : argb;
            pixels[at] = (byte)color;
            pixels[at + 1] = (byte)(color >> 8);
            pixels[at + 2] = (byte)(color >> 16);
            pixels[at + 3] = 255;
        }
        return new(TestImages.EncodePng(width, height, pixels));
    }

    private static void AssertNativeReopen(EditSession edit, int outputWidth)
    {
        var before = edit.Document.Copy();
        var history = (edit.UndoCount, edit.RedoCount, edit.IsDirty);
        var outputs = new[] { false, true }.Select(transparent =>
            (Transparent: transparent, Png: TestImages.RenderPng(before, transparent, outputWidth))).ToArray();
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"composition-{Guid.NewGuid():N}.ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "workflow-composition-test");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.StateEquals(before), Is.True);
            foreach (var output in outputs)
            {
                var png = TestImages.RenderPng(reopened, output.Transparent, outputWidth);
                Assert.That(png, Is.EqualTo(output.Png), $"scaled export, transparent={output.Transparent}");
                var decoded = TestImages.DecodePng(png);
                Assert.That((decoded.Width, decoded.Height), Is.EqualTo((outputWidth,
                    (int)Math.Round((double)before.CanvasHeight * outputWidth / before.CanvasWidth))));
                Assert.That(PixelAt(png, new(1, 1))[3], Is.EqualTo(output.Transparent ? 0 : 255));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That((edit.UndoCount, edit.RedoCount, edit.IsDirty), Is.EqualTo(history), "save/read/render leaves edit state intact");
    }

    private static byte[] Preview(EditorDocument document, DecodedImageCache images, BackgroundRenderCache background)
    {
        using var target = new RenderTargetBitmap(new PixelSize(document.CanvasWidth, document.CanvasHeight), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
            ScenePainter.Paint(context, document, TestImages.Decoder, backgroundCache: background, imageCache: images);
        using var stream = new MemoryStream();
        target.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    private static PointD Centre(RectD box) => new(box.X + box.Width / 2, box.Y + box.Height / 2);

    private static byte[] PixelAt(byte[] png, PointD point)
    {
        var (width, _, pixels) = TestImages.DecodePng(png);
        var offset = ((int)point.Y * width + (int)point.X) * 4;
        return pixels[offset..(offset + 4)];
    }

    private static void AssertArtworkCentre(byte[] png, Layer art, uint color) =>
        Assert.That(PixelAt(png, Centre(art.Bounds)), Is.EqualTo(new[]
            { (byte)color, (byte)(color >> 8), (byte)(color >> 16), (byte)255 }), "foreground artwork remains sharp");

    private static void AssertDifferencesInside(byte[] firstPng, byte[] secondPng, IReadOnlyList<RectD> allowed)
    {
        var (width, height, first) = TestImages.DecodePng(firstPng);
        var (otherWidth, otherHeight, second) = TestImages.DecodePng(secondPng);
        Assert.That((otherWidth, otherHeight), Is.EqualTo((width, height)));
        var differences = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var at = (y * width + x) * 4;
            if (first.AsSpan(at, 4).SequenceEqual(second.AsSpan(at, 4))) continue;
            differences++;
            if (!allowed.Any(box => x >= Math.Floor(box.X) - 1 && x <= Math.Ceiling(box.X + box.Width) + 1 &&
                                    y >= Math.Floor(box.Y) - 1 && y <= Math.Ceiling(box.Y + box.Height) + 1))
                Assert.Fail($"Unexpected composition change outside edited foreground at ({x}, {y}).");
        }
        Assert.That(differences, Is.GreaterThan(0), "the composed render exercises visible edits");
    }
}
