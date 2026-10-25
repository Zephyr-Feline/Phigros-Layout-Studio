using Avalonia;
using Avalonia.Media.Imaging;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class TranslucentCompositionWorkflowTests
{
    [TestCase(3, 640, 360)]
    [TestCase(5, 640, 360)]
    [TestCase(3, 480, 360)]
    [TestCase(5, 480, 360)]
    public void PatternedTranslucentSongsKeepCachedCompositionHistoryAndReopenedExportsConsistent(
        int count, int width, int height)
    {
        AvaloniaTestHost.EnsureStarted();
        var document = SongBatch.Create(width, height, Enumerable.Range(0, count)
            .Select(index => new SongArtwork($"半透明歌曲 {index}", Pattern(index))).ToArray());
        document.BackgroundArgb = 0x80102030;
        document.BackgroundBlend = new(true, BlurRadius: 3, Darkness: .12);
        var edit = new EditSession(document);
        var artwork = edit.Document.Layers.Where(layer => layer.Kind == LayerKind.Image).ToArray();
        var firstId = artwork[0].Id; var firstGroup = artwork[0].GroupId;
        edit.SetSelection([firstId]);
        edit.UpdateLayer(firstId, layer => layer.Name = "discarded redo branch");
        edit.Undo(); edit.MarkSaved();
        using var images = new DecodedImageCache();
        var background = new BackgroundRenderCache();
        var checkpoints = new List<(EditorDocument Document, byte[] Png)>();
        Checkpoint();
        var originalBackground = background.Get(edit.Document, TestImages.Decoder)!;
        Assert.That(CheckFrame(edit.Document), Is.EqualTo(checkpoints[0].Png));
        Assert.That(background.Get(edit.Document, TestImages.Decoder), Is.SameAs(originalBackground));

        edit.BeginGesture(firstId, includeGroup: true);
        edit.RotateBy(19);
        Assert.That(CheckFrame(edit.Document), Is.Not.EqualTo(checkpoints[0].Png));
        Assert.That(edit.UndoCount, Is.Zero); Assert.That(edit.RedoCount, Is.EqualTo(1));
        edit.CancelGesture();
        Assert.That(edit.Document.StateEquals(checkpoints[0].Document), Is.True);
        Assert.That(CheckFrame(edit.Document), Is.EqualTo(checkpoints[0].Png));
        Assert.That(edit.UndoCount, Is.Zero); Assert.That(edit.RedoCount, Is.EqualTo(1));
        Assert.That(edit.IsDirty, Is.False);

        edit.BeginGesture(firstId, includeGroup: true); edit.RotateBy(19);
        Assert.That(edit.CommitGesture(), Is.True); Checkpoint();
        edit.BeginGesture(firstId, includeGroup: true); edit.MoveBy(13, 9);
        var movingPreview = CheckFrame(edit.Document);
        Assert.That(edit.CommitGesture(), Is.True); Checkpoint();
        Assert.That(checkpoints[^1].Png, Is.EqualTo(movingPreview));
        edit.UpdateDocument(current =>
        {
            foreach (var layer in current.Layers.Where(layer => layer.GroupId != firstGroup)) layer.Visible = false;
        });
        Checkpoint();
        Assert.That(background.Get(edit.Document, TestImages.Decoder)!.ToArray(),
            Is.Not.EqualTo(originalBackground.ToArray()), "rotation, movement and hidden songs affect automatic composition");
        var final = edit.Document.Copy();
        var transparent = TestImages.RenderPng(final, true);
        var centre = LayerGeometry.Centre(final.Find(firstId).Bounds);
        Assert.That(PixelAt(transparent, centre)[3], Is.EqualTo(128).Within(1),
            "The artwork centre keeps its source alpha; transparent export does not flatten it onto the background.");
        var withoutBackground = final.Copy();
        withoutBackground.BackgroundBlend = new(false); withoutBackground.BackgroundArgb = 0;
        Assert.That(TestImages.RenderPng(withoutBackground, true), Is.EqualTo(transparent));

        for (var index = checkpoints.Count - 2; index >= 0; index--)
        {
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.StateEquals(checkpoints[index].Document), Is.True);
            Assert.That(CheckFrame(edit.Document), Is.EqualTo(checkpoints[index].Png), $"cached undo checkpoint {index}");
        }
        Assert.That(edit.IsDirty, Is.False);
        for (var index = 1; index < checkpoints.Count; index++)
        {
            Assert.That(edit.Redo(), Is.True);
            Assert.That(edit.Document.StateEquals(checkpoints[index].Document), Is.True);
            Assert.That(CheckFrame(edit.Document), Is.EqualTo(checkpoints[index].Png), $"cached redo checkpoint {index}");
        }
        Assert.That(edit.Document.StateEquals(final), Is.True);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { firstId }));
        var history = (edit.UndoCount, edit.RedoCount, edit.IsDirty);
        var outputs = new[] { false, true }.Select(mode =>
            (Transparent: mode, Bytes: TestImages.RenderPng(final, mode, 320))).ToArray();
        var path = Path.Combine(Path.GetTempPath(), "translucent-composition-" + Guid.NewGuid() + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "translucent-workflow-tests");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.StateEquals(final), Is.True);
            Assert.That(CheckFrame(reopened), Is.EqualTo(checkpoints[^1].Png),
                "The long-lived preview caches also accept the reopened document's identical assets and geometry.");
            foreach (var output in outputs)
            {
                var actual = TestImages.RenderPng(reopened, output.Transparent, 320);
                Assert.That(actual, Is.EqualTo(output.Bytes));
                var decoded = TestImages.DecodePng(actual);
                Assert.That((decoded.Width, decoded.Height), Is.EqualTo((320, width == 640 ? 180 : 240)));
                Assert.That(decoded.Bgra[3], Is.EqualTo(output.Transparent ? 0 : 255), "empty output corner alpha");
            }
        }
        finally { File.Delete(path); }
        Assert.That(edit.Document.StateEquals(final), Is.True);
        Assert.That((edit.UndoCount, edit.RedoCount, edit.IsDirty), Is.EqualTo(history));

        void Checkpoint()
        {
            Assert.That(edit.UndoCount, Is.EqualTo(checkpoints.Count));
            if (checkpoints.Count > 0) Assert.That(edit.RedoCount, Is.Zero);
            checkpoints.Add((edit.Document.Copy(), CheckFrame(edit.Document)));
        }

        byte[] CheckFrame(EditorDocument current)
        {
            var png = Preview(current, images, background);
            Assert.That(png, Is.EqualTo(TestImages.RenderPng(current)), "cached opaque preview equals fresh export");
            var (_, _, opaquePixels) = TestImages.DecodePng(png);
            Assert.That(Enumerable.Range(0, current.CanvasWidth * current.CanvasHeight)
                .All(index => opaquePixels[index * 4 + 3] == 255), Is.True,
                "Automatic composition is opaque even over the translucent canvas base and source artwork.");
            var transparentPixels = TestImages.DecodePng(TestImages.RenderPng(current, true)).Bgra;
            Assert.That(transparentPixels[3], Is.Zero, "The empty corner omits both automatic background and canvas base.");
            return png;
        }
    }

    private static AssetBlob Pattern(int seed)
    {
        const int width = 24, height = 16;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var at = (y * width + x) * 4;
            pixels[at] = (byte)(30 + (x * 7 + seed * 41) % 210);
            pixels[at + 1] = (byte)(20 + (y * 11 + seed * 53) % 220);
            pixels[at + 2] = (byte)(25 + ((x + y) * 5 + seed * 67) % 215);
            pixels[at + 3] = x is >= 8 and < 16 && y is >= 5 and < 11
                ? (byte)128 : (byte)(64 + ((x / 4 + y / 4 + seed) % 3) * 64);
        }
        return new(TestImages.EncodePng(width, height, pixels));
    }

    private static byte[] Preview(EditorDocument document, DecodedImageCache images, BackgroundRenderCache background)
    {
        using var target = new RenderTargetBitmap(new(document.CanvasWidth, document.CanvasHeight), new(96, 96));
        using (var context = target.CreateDrawingContext())
            ScenePainter.Paint(context, document, TestImages.Decoder, backgroundCache: background, imageCache: images);
        using var stream = new MemoryStream(); target.Save(stream, new PngBitmapEncoderOptions()); return stream.ToArray();
    }

    private static byte[] PixelAt(byte[] png, PointD point)
    {
        var (width, _, pixels) = TestImages.DecodePng(png);
        var offset = ((int)point.Y * width + (int)point.X) * 4;
        return pixels[offset..(offset + 4)];
    }
}
