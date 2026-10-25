using System.Buffers.Binary;
using Editor.Core;
using Editor.Imaging.Windows;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>Small classic TIFF fixtures exercise the page boundary without large allocations.</summary>
public sealed class SecurityTiffFirstFrameTests
{
    private const int EntryCount = 9;
    private const int IfdLength = 2 + EntryCount * 12 + 4;
    private const int FirstIfd = 8;
    private const int FirstPageLink = FirstIfd + 2 + EntryCount * 12;
    private const int SecondIfd = FirstIfd + IfdLength;
    private const int FirstPixels = SecondIfd + IfdLength;
    private const int SecondPixels = FirstPixels + 4;

    [TestCase(false)]
    [TestCase(true)]
    public void TwoValidPagesDecodeOnlyTheUnchangedFirstPage(bool bigEndian)
    {
        var bytes = TwoPageTiff(bigEndian);
        var asset = new AssetBlob(bytes);
        var hash = asset.Sha256;
        var result = new WicTiffImageDecoder().TryDecode(asset);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Width, Is.EqualTo(2));
            Assert.That(result.Height, Is.EqualTo(2));
            Assert.That(result.Bgra, Is.EqualTo(new byte[]
            {
                0, 0, 0, 255, 64, 64, 64, 255,
                128, 128, 128, 255, 255, 255, 255, 255
            }));
            Assert.That(asset.ToArray(), Is.EqualTo(bytes));
            Assert.That(asset.Sha256, Is.EqualTo(hash));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnusedSecondPageDoesNotNeedDecodablePixelData(bool bigEndian)
    {
        var bytes = TwoPageTiff(bigEndian);
        // The ignored page remains tiny (2 x 2), but its strip points past the file. A decoder
        // that materializes every page will reject it; no large-image/OOM sample is involved.
        Write32(bytes, SecondIfd + 2 + 5 * 12 + 8, 0x7fff_0000, bigEndian);
        var result = new WicTiffImageDecoder().TryDecode(new AssetBlob(bytes));
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Bgra[4], Is.EqualTo(64));
            Assert.That(result.Bgra[^4], Is.EqualTo(255));
        });
    }

    [TestCase(false, 1)]
    [TestCase(false, 3)]
    [TestCase(false, 17)]
    [TestCase(true, 1)]
    [TestCase(true, 3)]
    [TestCase(true, 17)]
    public void StreamViewMasksExactlyFourLinkBytesAcrossPartialReads(bool bigEndian, int chunk)
    {
        var bytes = TwoPageTiff(bigEndian);
        var expected = (byte[])bytes.Clone();
        expected.AsSpan(FirstPageLink, 4).Clear();
        using var stream = new FirstFrameTiffStream(new MemoryStream(bytes, writable: false));
        using var output = new MemoryStream();
        var buffer = new byte[chunk + 2];
        int read;
        while ((read = stream.Read(buffer, 1, chunk)) != 0) output.Write(buffer, 1, read);
        Assert.Multiple(() =>
        {
            Assert.That(output.ToArray(), Is.EqualTo(expected));
            Assert.That(stream.Length, Is.EqualTo(bytes.Length));
            Assert.That(stream.Position, Is.EqualTo(bytes.Length));
            Assert.That(stream.ReadByte(), Is.EqualTo(-1));
            Assert.That(stream.CanWrite, Is.False);
        });
        stream.Seek(FirstPageLink - 1, SeekOrigin.Begin);
        Assert.That(stream.ReadByte(), Is.EqualTo(bytes[FirstPageLink - 1]));
        for (var index = 0; index < 4; index++) Assert.That(stream.ReadByte(), Is.Zero);
        Assert.That(stream.ReadByte(), Is.EqualTo(bytes[FirstPageLink + 4]));
        stream.Position = FirstPageLink + 1;
        Span<byte> middle = stackalloc byte[2];
        Assert.That(stream.Read(middle), Is.EqualTo(2));
        Assert.That(middle.ToArray(), Is.EqualTo(new byte[2]));
        stream.Seek(-4, SeekOrigin.End);
        Assert.That(stream.ReadByte(), Is.EqualTo(bytes[^4]));
        Assert.That(bytes.AsSpan(FirstPageLink, 4).ToArray(),
            Is.Not.EqualTo(new byte[4]), "The underlying asset must keep its original page link.");
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FirstPageLinkIsMaskedEvenWhenItIsCyclic(bool bigEndian)
    {
        var bytes = TwoPageTiff(bigEndian);
        Write32(bytes, FirstPageLink, FirstIfd, bigEndian);
        // Exercise the managed view only; never pass a cyclic TIFF to a native codec.
        using var stream = new FirstFrameTiffStream(new MemoryStream(bytes, writable: false));
        stream.Position = FirstPageLink;
        Span<byte> link = stackalloc byte[4];
        stream.ReadExactly(link);
        Assert.That(link.ToArray(), Is.EqualTo(new byte[4]));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MissingFirstIfdLinkAndExcessiveEntryCountsAreRejected(bool bigEndian)
    {
        var bytes = TwoPageTiff(bigEndian);
        Assert.Throws<InvalidDataException>(() =>
            new FirstFrameTiffStream(new MemoryStream(bytes[..(FirstPageLink + 3)])));
        Write16(bytes, FirstIfd, ushort.MaxValue, bigEndian);
        Assert.Throws<InvalidDataException>(() =>
            new FirstFrameTiffStream(new MemoryStream(bytes)));
        Write32(bytes, 4, uint.MaxValue, bigEndian);
        Assert.Throws<InvalidDataException>(() =>
            new FirstFrameTiffStream(new MemoryStream(bytes)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DirectFallbackChecksFirstPageDimensionsBeforeNativeDecode(bool bigEndian)
    {
        var bytes = TwoPageTiff(bigEndian);
        Write32(bytes, FirstIfd + 2 + 8, 32_769, bigEndian);
        Assert.That(new WicTiffImageDecoder().TryDecode(new AssetBlob(bytes)), Is.Null);
    }

    [TestCase(false, 273, 279)]
    [TestCase(true, 273, 279)]
    [TestCase(false, 324, 325)]
    [TestCase(true, 324, 325)]
    [TestCase(false, 513, 514)]
    [TestCase(true, 513, 514)]
    public void PixelRegionsThatAliasThePageLinkAreRejectedBeforeNativeDecode(
        bool bigEndian, int offsetTag, int countTag)
    {
        var bytes = TwoPageTiff(bigEndian);
        var offsetEntry = FirstIfd + 2 + 5 * 12;
        var countEntry = FirstIfd + 2 + 8 * 12;
        Write16(bytes, offsetEntry, checked((ushort)offsetTag), bigEndian);
        Write16(bytes, countEntry, checked((ushort)countTag), bigEndian);
        // The page-link slot falls inside this four-byte region, not just at its boundary.
        Write32(bytes, offsetEntry + 8, FirstPageLink - 1, bigEndian);
        Assert.That(() => new FirstFrameTiffStream(new MemoryStream(bytes)),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("pixel data"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OffsetArraysAndOtherFieldArraysCannotAliasThePageLink(bool bigEndian)
    {
        foreach (var entryIndex in new[] { 5, 2 }) // StripOffsets and BitsPerSample.
        {
            var bytes = TwoPageTiff(bigEndian);
            var entry = FirstIfd + 2 + entryIndex * 12;
            Write32(bytes, entry + 4, 3, bigEndian); // More than four bytes, so an indirect array.
            Write32(bytes, entry + 8, FirstPageLink, bigEndian);
            Assert.That(() => new FirstFrameTiffStream(new MemoryStream(bytes)),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("field data"));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TruncatedOrExcessiveDataBlockArraysAreRejectedWithoutWalkingThem(bool bigEndian)
    {
        var bytes = TwoPageTiff(bigEndian);
        var entry = FirstIfd + 2 + 5 * 12;
        Write32(bytes, entry + 4, 2, bigEndian);
        Write32(bytes, entry + 8, (uint)(bytes.Length - 4), bigEndian);
        Assert.Throws<InvalidDataException>(() => new FirstFrameTiffStream(new MemoryStream(bytes)));
        Write32(bytes, entry + 4, 131_073, bigEndian);
        Write32(bytes, entry + 8, 8, bigEndian);
        Assert.Throws<InvalidDataException>(() => new FirstFrameTiffStream(new MemoryStream(bytes)));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void BoundedMultiStripArraysKeepFirstPagePixels(bool bigEndian, bool inlineShortCounts)
    {
        var bytes = TwoPageTiff(bigEndian);
        var originalLength = bytes.Length;
        Array.Resize(ref bytes, bytes.Length + 16);
        var offsets = FirstIfd + 2 + 5 * 12;
        Write32(bytes, offsets + 4, 2, bigEndian);
        Write32(bytes, offsets + 8, (uint)originalLength, bigEndian);
        Write32(bytes, originalLength, FirstPixels, bigEndian);
        Write32(bytes, originalLength + 4, FirstPixels + 2, bigEndian);
        var counts = FirstIfd + 2 + 8 * 12;
        Write32(bytes, counts + 4, 2, bigEndian);
        if (inlineShortCounts)
        {
            Write16(bytes, counts + 2, 3, bigEndian);
            Write16(bytes, counts + 8, 2, bigEndian);
            Write16(bytes, counts + 10, 2, bigEndian);
        }
        else
        {
            Write32(bytes, counts + 8, (uint)(originalLength + 8), bigEndian);
            Write32(bytes, originalLength + 8, 2, bigEndian);
            Write32(bytes, originalLength + 12, 2, bigEndian);
        }
        Write32(bytes, FirstIfd + 2 + 7 * 12 + 8, 1, bigEndian); // One row per strip.
        var result = new WicTiffImageDecoder().TryDecode(new AssetBlob(bytes));
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Bgra, Is.EqualTo(new byte[]
        {
            0, 0, 0, 255, 64, 64, 64, 255,
            128, 128, 128, 255, 255, 255, 255, 255
        }));
    }

    private static byte[] TwoPageTiff(bool bigEndian)
    {
        var bytes = new byte[SecondPixels + 4];
        bytes[0] = bytes[1] = (byte)(bigEndian ? 'M' : 'I');
        Write16(bytes, 2, 42, bigEndian);
        Write32(bytes, 4, FirstIfd, bigEndian);
        WriteIfd(FirstIfd, FirstPixels, SecondIfd);
        WriteIfd(SecondIfd, SecondPixels, 0);
        new byte[] { 0, 64, 128, 255 }.CopyTo(bytes, FirstPixels);
        new byte[] { 250, 250, 250, 250 }.CopyTo(bytes, SecondPixels);
        return bytes;

        void WriteIfd(int offset, int pixels, int next)
        {
            Write16(bytes, offset, EntryCount, bigEndian);
            var index = 0;
            Entry(256, 4, 2); // Width: LONG.
            Entry(257, 4, 2); // Height: LONG.
            Entry(258, 3, 8); // BitsPerSample: SHORT, grayscale.
            Entry(259, 3, 1); // Uncompressed.
            Entry(262, 3, 1); // BlackIsZero.
            Entry(273, 4, (uint)pixels);
            Entry(277, 3, 1); // SamplesPerPixel.
            Entry(278, 4, 2); // RowsPerStrip.
            Entry(279, 4, 4); // StripByteCounts.
            Write32(bytes, offset + 2 + EntryCount * 12, (uint)next, bigEndian);

            void Entry(ushort tag, ushort type, uint value)
            {
                var at = offset + 2 + index++ * 12;
                Write16(bytes, at, tag, bigEndian);
                Write16(bytes, at + 2, type, bigEndian);
                Write32(bytes, at + 4, 1, bigEndian);
                if (type == 3) Write16(bytes, at + 8, (ushort)value, bigEndian);
                else Write32(bytes, at + 8, value, bigEndian);
            }
        }
    }

    private static void Write16(byte[] bytes, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value);
        else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    }

    private static void Write32(byte[] bytes, int offset, uint value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
        else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
}
