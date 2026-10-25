using System.Buffers.Binary;
using System.IO.Compression;
using Avalonia;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Imaging.Windows;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>
/// REQ-ASSET-001 TIFF coverage. The embedded fixture is the kind of file the previous
/// Skia-only path rejected: a 16 x 8 GDI+ TIFF with LZW compression, horizontal predictor and
/// four samples per pixel, saved next to the PNG of the same pixels.
/// </summary>
public sealed class P2TiffAcceptanceTests
{
    private const string GdiPlusLzwTiff =
        "SUkqAGgAAACAAAAIB/hCBACDQKEwiDwuHQ2IQqIwyJRWKQIQQSHxaNxeOx+JyCDkCNSGTRyTx6Ux0AGCSyiYSqYyKZReBy+a" +
        "zSdSuUgBQTidzOeUKLABgUChzmk0GBOCkUSlVCmQEAAQAP4ABAABAAAAAAAAAAABBAABAAAAEAAAAAEBBAABAAAACAAAAAIB" +
        "AwAEAAAALgEAAAMBAwABAAAABQAAAAYBAwABAAAAAgAAABEBBAABAAAACAAAABUBAwABAAAABAAAABYBBAABAAAACAAAABcB" +
        "BAABAAAAXwAAABoBBQABAAAANgEAABsBBQABAAAAPgEAABwBAwABAAAAAQAAACgBAwABAAAAAgAAAD0BAwABAAAAAgAAAFIB" +
        "AwABAAAAAgAAAAAAAAAIAAgACAAIAAB3AQDoAwAAAHcBAOgDAAA=";

    private const string EquivalentPng =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAICAYAAADwdn+XAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAA" +
        "DsMAAA7DAcdvqGQAAAECSURBVChTFcyBtMVQDADBi1CEIBQhCEW4CEVYhCIEoQhBeAhBqMH+8wdg1lp4LIyF58JceC3cC++F" +
        "LHwW1sJ3YS/8LZyF38K1Ao/ACDwDM/AK3IF3IIFPYAW+gR34C5zAL/6DxCMxEs/ETLwSd+KdSOKTWIlvYif+Eifxy/9g47Ex" +
        "Np4bc+O1cW+8N7Lx2Vgb34298bdxNn77PwAPMMATTPACN3iDgA9Y4As2+AMH/PgPCo/CKDwLs/Aq3IV3IYVPYRW+hV34K5zC" +
        "r/6DxqMxGs/GbLwad+PdSOPTWI1vYzf+Gqfx6/9g8BiMwXMwB6/BPXgPMvgM1uA72IO/wRn8Bv8A+5kzkKA+UXAAAAAASUVO" +
        "RK5CYII=";

    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        AvaloniaTestHost.EnsureStarted();
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p2-tiff",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    private static byte[] Tiff => Convert.FromBase64String(GdiPlusLzwTiff);
    private static byte[] Png => Convert.FromBase64String(EquivalentPng);

    [Test]
    public void GdiPlusTiffIsAcceptedAndRendersLikeTheEquivalentPng()
    {
        Assert.DoesNotThrow(() => ImageAssetValidator.Validate(new AssetBlob(Tiff), TestImages.Decoder));
        using var decoded = TestImages.Decoder.Decode(new AssetBlob(Tiff));
        Assert.That(decoded.PixelSize, Is.EqualTo(new PixelSize(16, 8)));
        // Pixel exact at 1:1; the scaled case only allows one rounding step of resampling.
        TestImages.AssertRendersEquivalent(TestImages.RenderPng(ImageDocument(Png, 16, 8)),
            TestImages.RenderPng(ImageDocument(Tiff, 16, 8)), 0);
        TestImages.AssertRendersEquivalent(TestImages.RenderPng(ImageDocument(Png, 200, 100)),
            TestImages.RenderPng(ImageDocument(Tiff, 200, 100)), 2);
    }

    [Test]
    public void WithoutAPlatformCodecTiffIsRejectedRatherThanSilentlyChanged()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            ImageAssetValidator.Validate(new AssetBlob(Tiff)));
        Assert.That(error!.Message, Does.Contain("cannot be decoded"));
        Assert.That(new AssetImageDecoder().TryDecode(new AssetBlob(Tiff), out _), Is.False);
    }

    [Test]
    public void TiffSurvivesThePublicPackageWhenItsSourceFileIsRemoved()
    {
        var source = Path.Combine(_directory, "source 曲绘.tiff");
        File.WriteAllBytes(source, Tiff);
        var session = new EditSession(new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 });
        var layer = new Layer { Kind = LayerKind.Image, Name = "TIFF 图层", AssetId = Guid.NewGuid(),
            Bounds = new RectD(20, 20, 200, 100) };
        session.AddImageLayer(layer, File.ReadAllBytes(source));
        var workflow = new NativeProjectWorkflow(session, Validate, "p2-test");
        var projectPath = NativeProjectFile.EnsureExtension(Path.Combine(_directory, "工程 名称"));
        workflow.SaveAs(projectPath);
        var expected = TestImages.RenderPng(session.Document);

        File.Delete(source);
        var reopened = new NativeProjectWorkflow(new EditSession(new EditorDocument()), Validate, "p2-test");
        reopened.Open(projectPath);
        Assert.Multiple(() =>
        {
            Assert.That(reopened.IsDirty, Is.False);
            Assert.That(reopened.Session.Document.Find(layer.Id).Name, Is.EqualTo("TIFF 图层"));
            Assert.That(TestImages.RenderPng(reopened.Session.Document), Is.EqualTo(expected));
        });
    }

    [Test]
    public void TiffAssetsDeduplicateByContentInThePackage()
    {
        var document = new EditorDocument();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        document.AddAsset(first, Tiff);
        document.AddAsset(second, Tiff);
        var path = Path.Combine(_directory, "dedup.ppgproj");
        NativePackageStore.Save(path, document, "p2-test");
        using (var zip = ZipFile.OpenRead(path))
            Assert.That(zip.Entries.Count(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal)),
                Is.EqualTo(1));
        var reopened = NativePackageStore.Read(path);
        Assert.Multiple(() =>
        {
            Assert.That(reopened.Assets, Has.Count.EqualTo(2));
            Assert.That(reopened.Assets[first].Sha256, Is.EqualTo(reopened.Assets[second].Sha256));
        });
    }

    [Test]
    public void WindowsEncoderVariantsAreAcceptedAndRendered()
    {
        var raster = TestImages.SyntheticBgra(24, 12);
        foreach (var compressed in new[] { true, false })
        {
            var tiff = WicTiffEncoder.EncodeBgra(24, 12, raster, compressed);
            Assert.DoesNotThrow(() => ImageAssetValidator.Validate(new AssetBlob(tiff), TestImages.Decoder),
                $"compressed={compressed}");
            var png = TestImages.EncodePng(24, 12, raster);
            TestImages.AssertRendersEquivalent(TestImages.RenderPng(ImageDocument(png, 24, 12)),
                TestImages.RenderPng(ImageDocument(tiff, 24, 12)), 0);
            TestImages.AssertRendersEquivalent(TestImages.RenderPng(ImageDocument(png, 200, 100)),
                TestImages.RenderPng(ImageDocument(tiff, 200, 100)), 2);
        }
    }

    [Test]
    public void TruncatedAndCorruptTiffBodiesAreRejected()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<InvalidDataException>(() =>
                ImageAssetValidator.Validate(new AssetBlob(Tiff[..8]), TestImages.Decoder));
            Assert.Throws<InvalidDataException>(() =>
                ImageAssetValidator.Validate(new AssetBlob(Tiff[..16]), TestImages.Decoder));
            Assert.Throws<InvalidDataException>(() =>
                ImageAssetValidator.Validate(new AssetBlob(WithIfdEntryCount(Tiff, 60000)), TestImages.Decoder));
            Assert.Throws<InvalidDataException>(() =>
                ImageAssetValidator.Validate(new AssetBlob(WithBrokenStripOffset(Tiff)), TestImages.Decoder));
        });
    }

    [TestCase(0u, 8u)]
    [TestCase(16u, 0u)]
    [TestCase(32_769u, 8u)]
    [TestCase(10_000u, 10_000u)]
    [TestCase(400_000_000u, 1u)]
    public void TiffDimensionLimitsAreEnforcedBeforeDecode(uint width, uint height)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            ImageAssetValidator.Validate(new AssetBlob(TiffHeader(width, height)), TestImages.Decoder));
        Assert.That(error!.Message, Does.Contain("dimensions"));
    }

    [Test]
    public void HashCorrectButUndecodableTiffCannotReplaceTheActiveSession()
    {
        var brokenDocument = new EditorDocument();
        var assetId = Guid.NewGuid();
        brokenDocument.AddAsset(assetId, WithBrokenStripOffset(Tiff));
        brokenDocument.Layers.Add(new Layer { Kind = LayerKind.Image, Name = "broken tiff", AssetId = assetId });
        var brokenPath = Path.Combine(_directory, "broken-tiff.ppgproj");
        NativePackageStore.Save(brokenPath, brokenDocument, "p2-test");
        Assert.DoesNotThrow(() => NativePackageStore.Read(brokenPath)); // Hash integrity holds.

        var session = new EditSession(new EditorDocument());
        var original = new Layer { Kind = LayerKind.Text, Text = "未保存" };
        session.AddLayer(original);
        var workflow = new NativeProjectWorkflow(session, Validate, "p2-test");
        var undoCount = session.UndoCount;
        Assert.That(() => workflow.Open(brokenPath),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("Image asset"));
        Assert.Multiple(() =>
        {
            Assert.That(session.Document.Find(original.Id).Text, Is.EqualTo("未保存"));
            Assert.That(session.UndoCount, Is.EqualTo(undoCount));
            Assert.That(workflow.IsDirty, Is.True);
            Assert.That(workflow.CurrentPath, Is.Null);
        });
    }

    [Test]
    public void TamperedTiffAssetBytesFailTheHashCheck()
    {
        var document = new EditorDocument();
        var assetId = Guid.NewGuid();
        document.AddAsset(assetId, Tiff);
        document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = assetId });
        var path = Path.Combine(_directory, "tampered.ppgproj");
        NativePackageStore.Save(path, document, "p2-test");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.Single(item => item.FullName.StartsWith("assets/", StringComparison.Ordinal));
            var name = entry.FullName;
            entry.Delete();
            using var output = zip.CreateEntry(name).Open();
            var tampered = Tiff;
            tampered[^1] ^= 0xff;
            output.Write(tampered);
        }
        Assert.That(() => NativePackageStore.Read(path),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("SHA-256"));
    }

    private static void Validate(EditorDocument document) =>
        ImageAssetValidator.Validate(document, TestImages.Decoder);

    private static EditorDocument ImageDocument(byte[] imageBytes, double width, double height)
    {
        var document = new EditorDocument { CanvasWidth = 320, CanvasHeight = 180,
            BackgroundArgb = 0xFF000000 };
        var assetId = Guid.NewGuid();
        document.AddAsset(assetId, imageBytes);
        document.Layers.Add(new Layer { Kind = LayerKind.Image, Name = "image", AssetId = assetId,
            Bounds = new RectD(20, 20, width, height) });
        return document;
    }

    private static byte[] WithIfdEntryCount(byte[] tiff, ushort count)
    {
        var bytes = (byte[])tiff.Clone();
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(ifd, 2), count);
        return bytes;
    }

    private static byte[] WithBrokenStripOffset(byte[] tiff)
    {
        var bytes = (byte[])tiff.Clone();
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(ifd, 2));
        for (var i = 0; i < count; i++)
        {
            var entry = ifd + 2 + i * 12;
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry, 2)) != 273) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 8, 4), 0x7FFF_0000);
            return bytes;
        }
        throw new InvalidDataException("Fixture has no strip offset tag.");
    }

    /// <summary>Minimal classic TIFF with width/height LONG entries and no image data.</summary>
    private static byte[] TiffHeader(uint width, uint height)
    {
        var bytes = new byte[38];
        bytes[0] = 0x49; bytes[1] = 0x49; bytes[2] = 0x2a; bytes[3] = 0x00;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 2);
        WriteEntry(bytes.AsSpan(10), 256, width);
        WriteEntry(bytes.AsSpan(22), 257, height);
        return bytes;

        static void WriteEntry(Span<byte> entry, ushort tag, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(entry, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], 4);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], value);
        }
    }
}
