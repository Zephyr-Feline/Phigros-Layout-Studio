using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Editor.Core;
using Editor.Rendering;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Editor.Tests")]

namespace Editor.Imaging.Windows;

/// <summary>
/// TIFF decode through the in-box Windows imaging component (WIC) that WPF exposes. The Skia
/// renderer used by the shared projects ships no TIFF codec, so this boundary keeps
/// <c>REQ-ASSET-001</c>'s TIFF input working without a third-party image package.
/// </summary>
/// <remarks>
/// The decoder is deliberately narrow: only classic little/big-endian TIFF signatures are claimed,
/// only the first frame is exposed for multi-page files, and shared encoded-size, per-side and
/// total-pixel checks run before WIC is created. Windows codecs are also used for the
/// color model (palette, CMYK, 1/4/16-bit samples) through a BGRA32 conversion.
/// </remarks>
public sealed class WicTiffImageDecoder : IImageFallbackDecoder
{
    private static readonly byte[] LittleEndianSignature = [0x49, 0x49, 0x2a, 0x00];
    private static readonly byte[] BigEndianSignature = [0x4d, 0x4d, 0x00, 0x2a];

    public DecodedRaster? TryDecode(AssetBlob asset)
    {
        if (!HasTiffSignature(asset)) return null;
        // Keep this boundary safe even when a caller bypasses ImageAssetValidator.Validate.
        var dimensions = ImageAssetValidator.ReadEncodedDimensions(asset);
        if (!WithinLimits(dimensions.Width, dimensions.Height)) return null;
        using var source = asset.OpenRead();
        // The editor only uses page one. Hide its next-IFD link from WIC rather than letting
        // BitmapDecoder enumerate/cache pages the editor will never use. The asset is unchanged.
        using var stream = new FirstFrameTiffStream(source);
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0) return null;
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (!WithinLimits(width, height) || width != dimensions.Width || height != dimensions.Height)
            return null;
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);
        return new DecodedRaster(width, height, pixels);
    }

    private static bool WithinLimits(int width, int height) =>
        width > 0 && height > 0 && width <= ImageAssetValidator.MaxDimension &&
        height <= ImageAssetValidator.MaxDimension && (long)width * height <= ImageAssetValidator.MaxPixels;

    private static bool HasTiffSignature(AssetBlob asset)
    {
        if (asset.Length < 4) return false;
        Span<byte> header = stackalloc byte[4];
        using var stream = asset.OpenRead();
        return stream.Read(header) == 4 &&
            (header.SequenceEqual(LittleEndianSignature) || header.SequenceEqual(BigEndianSignature));
    }
}

/// <summary>
/// A read-only view of a classic TIFF whose first IFD is the end of the visible page chain.
/// Only four link bytes are overlaid; pixels, metadata offsets and the stored asset stay intact.
/// </summary>
internal sealed class FirstFrameTiffStream : Stream
{
    // At most four planes with one strip per row of a supported 32768-high image. Unusual
    // files with more data blocks are rejected instead of adding an unbounded metadata walk.
    private const int MaxDataBlocks = 131_072;
    private readonly Stream _source;
    private readonly long _nextIfdOffset;

    public FirstFrameTiffStream(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("A readable, seekable TIFF stream is required.", nameof(source));
        _source = source;
        Span<byte> header = stackalloc byte[8];
        source.Position = 0;
        source.ReadExactly(header);
        var little = header[..4].SequenceEqual("II*\0"u8);
        if (!little && !header[..4].SequenceEqual("MM\0*"u8))
            throw new InvalidDataException("Expected a classic TIFF header.");
        var firstIfd = little ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) :
            BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (firstIfd < 8 || firstIfd > source.Length - 2)
            throw new InvalidDataException("TIFF first IFD is outside the image.");
        source.Position = firstIfd;
        Span<byte> countBytes = stackalloc byte[2];
        source.ReadExactly(countBytes);
        var count = little ? BinaryPrimitives.ReadUInt16LittleEndian(countBytes) :
            BinaryPrimitives.ReadUInt16BigEndian(countBytes);
        _nextIfdOffset = checked((long)firstIfd + 2 + count * 12L);
        if (count > 1024 || _nextIfdOffset > source.Length - 4)
            throw new InvalidDataException("TIFF first IFD or page link is truncated.");
        CheckFirstFrameData(firstIfd, count, little);
        source.Position = 0;
    }

    private readonly record struct Values(long Position, uint Count, int Size, ushort Type);

    private void CheckFirstFrameData(uint firstIfd, ushort count, bool little)
    {
        Values? stripOffsets = null, stripBytes = null, tileOffsets = null, tileBytes = null;
        Values? jpegOffset = null, jpegBytes = null;
        Span<byte> entry = stackalloc byte[12];
        for (var index = 0; index < count; index++)
        {
            var entryPosition = (long)firstIfd + 2 + index * 12L;
            _source.Position = entryPosition;
            _source.ReadExactly(entry);
            var tag = Read16(entry, little);
            var type = Read16(entry[2..], little);
            var items = Read32(entry[4..], little);
            var size = type switch
            {
                1 or 2 or 6 or 7 => 1,
                3 or 8 => 2,
                4 or 9 or 11 or 13 => 4,
                5 or 10 or 12 => 8,
                _ => throw new InvalidDataException("Unsupported classic TIFF field type.")
            };
            var length = checked((long)items * size);
            var position = length <= 4 ? entryPosition + 8 : Read32(entry[8..], little);
            if (position > _source.Length - length || OverlapsPageLink(position, length))
                throw new InvalidDataException("TIFF field data is outside the image or overlaps its page link.");
            var values = new Values(position, items, size, type);
            switch (tag)
            {
                case 273: Set(ref stripOffsets, values); break;
                case 279: Set(ref stripBytes, values); break;
                case 324: Set(ref tileOffsets, values); break;
                case 325: Set(ref tileBytes, values); break;
                case 513: Set(ref jpegOffset, values); break;
                case 514: Set(ref jpegBytes, values); break;
            }
        }
        CheckPair(stripOffsets, stripBytes);
        CheckPair(tileOffsets, tileBytes);
        CheckPair(jpegOffset, jpegBytes);
        return;

        static void Set(ref Values? destination, Values values)
        {
            if (destination.HasValue || values.Count is 0 or > MaxDataBlocks || values.Type is not (3 or 4))
                throw new InvalidDataException("TIFF data-block fields are duplicated, unsupported or excessive.");
            destination = values;
        }

        void CheckPair(Values? offsets, Values? lengths)
        {
            if (!offsets.HasValue && !lengths.HasValue) return;
            if (!offsets.HasValue || !lengths.HasValue || offsets.Value.Count != lengths.Value.Count)
                throw new InvalidDataException("TIFF data-block offset/count fields disagree.");
            for (uint index = 0; index < offsets.Value.Count; index++)
            {
                var start = ReadValue(offsets.Value, index);
                var length = ReadValue(lengths.Value, index);
                if (start > _source.Length - length || OverlapsPageLink(start, length))
                    throw new InvalidDataException("TIFF pixel data is outside the image or overlaps its page link.");
            }
        }

        uint ReadValue(Values values, uint index)
        {
            Span<byte> value = stackalloc byte[4];
            _source.Position = checked(values.Position + (long)index * values.Size);
            _source.ReadExactly(value[..values.Size]);
            return values.Size == 2 ? Read16(value, little) : Read32(value, little);
        }
    }

    private bool OverlapsPageLink(long start, long length) =>
        length > 0 && start < _nextIfdOffset + 4 && _nextIfdOffset < start + length;

    private static ushort Read16(ReadOnlySpan<byte> bytes, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint Read32(ReadOnlySpan<byte> bytes, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    public override bool CanRead => _source.CanRead;
    public override bool CanSeek => _source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _source.Length;
    public override long Position { get => _source.Position; set => _source.Position = value; }
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var start = _source.Position;
        var read = _source.Read(buffer);
        var maskStart = Math.Max(start, _nextIfdOffset);
        var maskEnd = Math.Min(start + read, _nextIfdOffset + 4);
        if (maskStart < maskEnd)
            buffer.Slice((int)(maskStart - start), (int)(maskEnd - maskStart)).Clear();
        return read;
    }

    public override int ReadByte()
    {
        Span<byte> value = stackalloc byte[1];
        return Read(value) == 0 ? -1 : value[0];
    }

    public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Encodes a TIFF through the same in-box Windows imaging stack. Used by the probe and tests to
/// build deterministic TIFF fixtures without adding a third-party encoder.
/// </summary>
public static class WicTiffEncoder
{
    public static byte[] EncodeBgra(int width, int height, byte[] bgra, bool compressed = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var stride = checked(width * 4);
        if (bgra.Length < (long)stride * height)
            throw new ArgumentException("Pixel data is shorter than its declared size.", nameof(bgra));
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
        var encoder = new TiffBitmapEncoder
        {
            Compression = compressed ? TiffCompressOption.Zip : TiffCompressOption.None
        };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
