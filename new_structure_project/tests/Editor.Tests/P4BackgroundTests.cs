using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4BackgroundTests
{
    [Test]
    public void FixedBackgroundReplacementClearUndoAndNativeRoundTripKeepEmbeddedBytes()
    {
        AvaloniaTestHost.EnsureStarted();
        var first = SolidPng(400, 200, 0xFFAA0000);
        var second = SolidPng(400, 200, 0xFF0000AA);
        var edit = new EditSession(new EditorDocument { CanvasWidth = 320, CanvasHeight = 240 });
        edit.SetFixedBackground(first);
        var firstId = edit.Document.FixedBackgroundAssetId!.Value;
        edit.MarkSaved();
        edit.SetFixedBackground((byte[])first.Clone());
        Assert.That(edit.UndoCount, Is.EqualTo(1), "setting identical bytes is a no-op");
        Assert.That(edit.IsDirty, Is.False);

        edit.SetFixedBackground(second);
        var secondId = edit.Document.FixedBackgroundAssetId!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(secondId, Is.Not.EqualTo(firstId));
            Assert.That(edit.Document.Assets.Keys, Is.EquivalentTo(new[] { secondId }));
            Assert.That(edit.UndoCount, Is.EqualTo(2));
        });
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p4-background-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            var pixelsBeforeSave = TestImages.RenderPng(edit.Document);
            NativePackageStore.Save(path, edit.Document, "p4-test");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.StateEquals(edit.Document), Is.True);
            Assert.That(TestImages.RenderPng(reopened), Is.EqualTo(pixelsBeforeSave));
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        edit.ClearFixedBackground();
        Assert.That(edit.Document.FixedBackgroundAssetId, Is.Null);
        Assert.That(edit.Document.Assets, Is.Empty);
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Assets[secondId].ToArray(), Is.EqualTo(second));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Assets[firstId].ToArray(), Is.EqualTo(first));
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.FixedBackgroundAssetId, Is.EqualTo(secondId));
    }

    [Test]
    public void FixedBackgroundUsesCenterCoverAndTransparentExportSkipsIt()
    {
        const int sourceWidth = 400, sourceHeight = 200;
        var pixels = new byte[sourceWidth * sourceHeight * 4];
        for (var y = 0; y < sourceHeight; y++)
        for (var x = 0; x < sourceWidth; x++)
        {
            var at = (y * sourceWidth + x) * 4;
            pixels[at] = x >= 300 ? (byte)255 : (byte)0;       // blue outer band
            pixels[at + 1] = x is >= 100 and < 300 ? (byte)255 : (byte)0;
            pixels[at + 2] = x < 100 ? (byte)255 : (byte)0;     // red outer band
            pixels[at + 3] = 255;
        }
        var edit = new EditSession(new EditorDocument { CanvasWidth = 320, CanvasHeight = 320,
            BackgroundArgb = 0xFF121212 });
        edit.SetFixedBackground(TestImages.EncodePng(sourceWidth, sourceHeight, pixels));
        var (width, height, cover) = TestImages.DecodePng(TestImages.RenderPng(edit.Document));
        Assert.That((width, height), Is.EqualTo((320, 320)));
        foreach (var x in new[] { 10, 160, 309 })
        {
            var at = (160 * width + x) * 4;
            Assert.That(cover[at..(at + 4)], Is.EqualTo(new byte[] { 0, 255, 0, 255 }),
                $"cover crop at x={x}");
        }
        var (_, _, transparent) = TestImages.DecodePng(TestImages.RenderPng(edit.Document, true));
        Assert.That(transparent[(160 * width + 160) * 4 + 3], Is.Zero);
        edit.ClearFixedBackground();
        var (_, _, solid) = TestImages.DecodePng(TestImages.RenderPng(edit.Document));
        var center = (160 * width + 160) * 4;
        Assert.That(solid[center..(center + 4)],
            Is.EqualTo(new byte[] { 0x12, 0x12, 0x12, 0xFF }));
    }

    private static byte[] SolidPng(int width, int height, uint argb)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)argb;
            pixels[i + 1] = (byte)(argb >> 8);
            pixels[i + 2] = (byte)(argb >> 16);
            pixels[i + 3] = (byte)(argb >> 24);
        }
        return TestImages.EncodePng(width, height, pixels);
    }
}
