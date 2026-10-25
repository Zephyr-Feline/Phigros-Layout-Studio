using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Editor.Core;

namespace Editor.Rendering;

/// <summary>Checks encoded image dimensions before asking the renderer to allocate decoded pixels.</summary>
public static class ImageAssetValidator
{
    public const long MaxPixels = 80_000_000; // V1's documented project image limit.
    public const int MaxDimension = 32_768; // Keeps a BGRA row below 128 KiB.
    public const int MaxEncodedBytes = AssetResourceBudget.MaxAssetBytes; // Matches one native ZIP asset entry.

    public static void Validate(EditorDocument document, AssetImageDecoder? decoder = null)
    {
        document.Validate();
        foreach (var (id, asset) in document.Assets)
        {
            try { Validate(asset, decoder); }
            catch (InvalidDataException error)
            {
                var layer = document.Layers.FirstOrDefault(item => item.AssetId == id);
                var owner = layer is null ? "background or unreferenced resource" : $"layer {layer.Id} ({layer.Name})";
                throw new InvalidDataException($"Image asset {id} for {owner}: {error.Message}", error);
            }
        }
    }

    public static void Validate(AssetBlob asset, AssetImageDecoder? decoder = null)
    {
        if (asset.Length > MaxEncodedBytes)
            throw new InvalidDataException("Encoded image exceeds the 100 MiB asset limit.");
        using var headerStream = asset.OpenRead();
        var (width, height) = ReadDimensions(headerStream);
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension ||
            (long)width * height > MaxPixels)
            throw new InvalidDataException($"Image dimensions {width} x {height} exceed the supported limit.");
        try
        {
            using var decoded = (decoder ?? AssetImageDecoder.RendererOnly).Decode(asset);
            if (decoded.PixelSize.Width != width || decoded.PixelSize.Height != height)
                throw new InvalidDataException("Decoded image dimensions disagree with the header.");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new InvalidDataException("Image bytes cannot be decoded by the active renderer.", error);
        }
    }

    /// <summary>
    /// Header-only dimension probe for callers that need the encoded size without a full decode
    /// (the classic compatibility mapping needs the background bitmap size).
    /// </summary>
    public static (int Width, int Height) ReadEncodedDimensions(AssetBlob asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Length > MaxEncodedBytes)
            throw new InvalidDataException("Encoded image exceeds the 100 MiB asset limit.");
        using var stream = asset.OpenRead();
        return ReadDimensions(stream);
    }

    /// <summary>Checks all alpha values using at most sixteen BGRA/RGBA rows, after encoded bounds preflight.</summary>
    public static bool IsOpaque(AssetBlob asset, AssetImageDecoder? decoder = null)
    {
        var (width, height) = ReadEncodedDimensions(asset);
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension ||
            (long)width * height > MaxPixels)
            throw new InvalidDataException($"Image dimensions {width} x {height} exceed the supported limit.");
        using var bitmap = (decoder ?? AssetImageDecoder.RendererOnly).Decode(asset);
        if (bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height)
            throw new InvalidDataException("Decoded image dimensions disagree with the header.");
        if (bitmap.AlphaFormat == AlphaFormat.Opaque) return true;
        if (bitmap.Format != PixelFormat.Bgra8888 && bitmap.Format != PixelFormat.Rgba8888)
            throw new InvalidDataException("Background opacity requires readable BGRA/RGBA pixels.");
        var stride = checked(width * 4);
        var rows = Math.Min(16, height);
        var bytes = new byte[checked(stride * rows)];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            for (var y = 0; y < height; y += rows)
            {
                var count = Math.Min(rows, height - y);
                bitmap.CopyPixels(new PixelRect(0, y, width, count), handle.AddrOfPinnedObject(), stride * count, stride);
                for (var at = 3; at < stride * count; at += 4)
                    if (bytes[at] != 255) return false;
            }
        }
        finally { handle.Free(); }
        return true;
    }

    private static (int Width, int Height) ReadDimensions(Stream stream)
    {
        Span<byte> header = stackalloc byte[26];
        if (stream.Read(header) < 16)
            throw new InvalidDataException("Image header is truncated.");
        stream.Position = 0;
        ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (header[..8].SequenceEqual(pngSignature))
        {
            if (stream.Read(header) < 24 || BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13 ||
                !header[12..16].SequenceEqual("IHDR"u8))
                throw new InvalidDataException("PNG IHDR is missing or truncated.");
            var w = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
            var h = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
            return (w > int.MaxValue ? int.MaxValue : (int)w,
                h > int.MaxValue ? int.MaxValue : (int)h);
        }
        if (header[0] == 0xff && header[1] == 0xd8) return ReadJpeg(stream);
        if (header[0] == 'B' && header[1] == 'M')
        {
            if (stream.Read(header) < 26 || BinaryPrimitives.ReadUInt32LittleEndian(header[14..18]) < 40)
                throw new InvalidDataException("BMP DIB header is unsupported or truncated.");
            var width = BinaryPrimitives.ReadInt32LittleEndian(header[18..22]);
            var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(header[22..26]);
            if (signedHeight == int.MinValue) throw new InvalidDataException("Invalid BMP height.");
            return (width, Math.Abs(signedHeight));
        }
        if ((header[..4].SequenceEqual("II*\0"u8) || header[..4].SequenceEqual("MM\0*"u8)))
            return ReadTiff(stream, header[0] == 'I');
        throw new InvalidDataException("Unsupported image format; expected PNG, JPEG, BMP or TIFF.");
    }

    private static (int Width, int Height) ReadJpeg(Stream stream)
    {
        stream.Position = 2;
        for (var segments = 0; segments < 4096 && stream.Position < stream.Length; segments++)
        {
            if (stream.ReadByte() != 0xff) throw new InvalidDataException("Malformed JPEG marker.");
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xff);
            if (marker < 0 || marker is 0xd9 or 0xda) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
            var high = stream.ReadByte();
            var low = stream.ReadByte();
            var length = (high << 8) | low;
            if (high < 0 || low < 0 || length < 2 || stream.Position + length - 2 > stream.Length)
                throw new InvalidDataException("JPEG segment is truncated.");
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 7) throw new InvalidDataException("JPEG frame is truncated.");
                stream.ReadByte();
                var h = (stream.ReadByte() << 8) | stream.ReadByte();
                var w = (stream.ReadByte() << 8) | stream.ReadByte();
                return (w, h);
            }
            stream.Position += length - 2;
        }
        throw new InvalidDataException("JPEG dimensions are missing.");
    }

    private static (int Width, int Height) ReadTiff(Stream stream, bool littleEndian)
    {
        Span<byte> bytes = stackalloc byte[12];
        stream.Position = 4;
        if (stream.Read(bytes[..4]) != 4) throw new InvalidDataException("TIFF IFD offset is missing.");
        var offset = Read32(bytes[..4], littleEndian);
        if (offset > stream.Length - 2) throw new InvalidDataException("TIFF IFD is outside the image.");
        stream.Position = offset;
        if (stream.Read(bytes[..2]) != 2) throw new InvalidDataException("TIFF IFD is truncated.");
        var count = Read16(bytes[..2], littleEndian);
        if (count > 1024 || stream.Position + count * 12L > stream.Length)
            throw new InvalidDataException("TIFF IFD entry count is invalid.");
        int width = 0, height = 0;
        for (var i = 0; i < count; i++)
        {
            if (stream.Read(bytes) != 12) throw new InvalidDataException("TIFF IFD entry is truncated.");
            var tag = Read16(bytes[..2], littleEndian);
            if (tag is not (256 or 257)) continue;
            var type = Read16(bytes[2..4], littleEndian);
            var itemCount = Read32(bytes[4..8], littleEndian);
            if (itemCount != 1 || type is not (3 or 4))
                throw new InvalidDataException("Unsupported TIFF dimension field.");
            var value = type == 3 ? Read16(bytes[8..10], littleEndian) : Read32(bytes[8..12], littleEndian);
            if (value > int.MaxValue) throw new InvalidDataException("TIFF dimension is too large.");
            if (tag == 256) width = (int)value;
            else height = (int)value;
        }
        if (width == 0 || height == 0) throw new InvalidDataException("TIFF dimensions are missing.");
        return (width, height);
    }

    private static ushort Read16(ReadOnlySpan<byte> bytes, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private static uint Read32(ReadOnlySpan<byte> bytes, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
}
