using System.Buffers.Binary;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P2ImageValidationTests
{
    [Test]
    public void UnsupportedAndTruncatedImageHeadersAreRejectedBeforeDecode()
    {
        Assert.That(() => ImageAssetValidator.Validate(new AssetBlob([1, 2, 3])),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("truncated"));
        Assert.That(() => ImageAssetValidator.Validate(new AssetBlob("GIF89a unsupported"u8.ToArray())),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("Unsupported image format"));
    }


    [TestCase(0u, 16u)]
    [TestCase(16u, 0u)]
    [TestCase(32_769u, 1u)]
    [TestCase(10_000u, 10_000u)]
    public void PngDimensionGuardRejectsZeroExtremeAxisAndOverEightyMillionPixels(uint width, uint height)
    {
        Assert.That(() => ImageAssetValidator.Validate(new AssetBlob(PngHeader(width, height))),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("dimensions"));
    }

    [Test]
    public void JpegAndBmpMetadataAreGuardedBeforeFullDecode()
    {
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xc0, 0x00, 0x07, 0x08,
            0x00, 0x10, 0x80, 0x01, 0x00, 0, 0, 0, 0];
        Assert.That(() => ImageAssetValidator.Validate(new AssetBlob(jpeg)),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("dimensions"));
        var bmp = new byte[26];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), 100);
        Assert.That(() => ImageAssetValidator.Validate(new AssetBlob(bmp)),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("dimensions"));
    }


    [Test]
    public void HashCorrectButUnsupportedImageCannotReplaceDirtyActiveDocument()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p2-image-rejection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "hash-correct.package");
            var imageId = Guid.NewGuid();
            var candidate = new EditorDocument();
            candidate.AddAsset(imageId, "GIF89a unsupported"u8.ToArray());
            candidate.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = imageId, Name = "broken" });
            NativePackageStore.Save(path, candidate, "p2-test");
            Assert.DoesNotThrow(() => NativePackageStore.Read(path)); // Hash verification alone succeeds.

            var session = new EditSession(new EditorDocument());
            var original = new Layer { Kind = LayerKind.Text, Text = "unsaved" };
            session.AddLayer(original);
            var undoCount = session.UndoCount;
            var workflow = new NativeProjectWorkflow(session, TestImages.ValidateImages, "p2-test");
            Assert.That(() => workflow.Open(path),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("Image asset"));
            Assert.Multiple(() =>
            {
                Assert.That(session.Document.Find(original.Id).Text, Is.EqualTo("unsaved"));
                Assert.That(session.UndoCount, Is.EqualTo(undoCount));
                Assert.That(workflow.IsDirty, Is.True);
                Assert.That(workflow.CurrentPath, Is.Null);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static byte[] PngHeader(uint width, uint height)
    {
        byte[] data = [137, 80, 78, 71, 13, 10, 26, 10,
            0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20, 4), height);
        return data;
    }
}
