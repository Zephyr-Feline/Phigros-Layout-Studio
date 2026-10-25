using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Editor.Core;

namespace Editor.Rendering;

/// <summary>Decoded pixels produced by a platform codec the shared Skia renderer cannot read itself.</summary>
public sealed record DecodedRaster(int Width, int Height, byte[] Bgra)
{
    public long PixelCount => (long)Width * Height;

    /// <summary>Wraps the pixels in a bitmap the shared painter can draw.</summary>
    public Bitmap ToBitmap()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Height);
        var rowBytes = checked(Width * 4);
        if (Bgra.Length < (long)rowBytes * Height)
            throw new InvalidDataException("Decoded pixel data is shorter than its declared size.");
        var bitmap = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var buffer = bitmap.Lock())
        {
            for (var y = 0; y < Height; y++)
                Marshal.Copy(Bgra, y * rowBytes, IntPtr.Add(buffer.Address, y * buffer.RowBytes), rowBytes);
        }
        return bitmap;
    }
}

/// <summary>
/// Platform codec boundary. Implementations live outside Editor.Core / Editor.Rendering so the
/// shared projects never depend on a platform imaging stack.
/// </summary>
public interface IImageFallbackDecoder
{
    /// <summary>
    /// Returns decoded 32-bit BGRA pixels, or null when this decoder does not own the payload or
    /// cannot decode it.
    /// </summary>
    DecodedRaster? TryDecode(AssetBlob asset);
}

/// <summary>
/// Single decode entry point used by validation, preview and export. The shared Skia renderer is
/// always tried first so existing formats and pixels keep their current path; registered platform
/// decoders only see payloads it cannot read.
/// </summary>
public sealed class AssetImageDecoder
{
    /// <summary>Renderer-only decoder: payloads outside the Skia codec set are rejected.</summary>
    public static AssetImageDecoder RendererOnly { get; } = new();

    private readonly IImageFallbackDecoder[] _fallbacks;

    public AssetImageDecoder(params IImageFallbackDecoder[] fallbacks) =>
        _fallbacks = fallbacks ?? [];

    public Bitmap Decode(AssetBlob asset) => TryDecode(asset, out var bitmap)
        ? bitmap!
        : throw new InvalidDataException("Image bytes cannot be decoded by the active renderer.");

    public bool TryDecode(AssetBlob asset, out Bitmap? bitmap)
    {
        ArgumentNullException.ThrowIfNull(asset);
        try
        {
            using var stream = asset.OpenRead();
            bitmap = new Bitmap(stream);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The shared renderer has no codec for this payload; platform decoders get their turn.
        }

        foreach (var fallback in _fallbacks)
        {
            DecodedRaster? raster;
            try
            {
                raster = fallback.TryDecode(asset);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                continue;
            }
            if (raster is null || raster.Width <= 0 || raster.Height <= 0) continue;
            bitmap = raster.ToBitmap();
            return true;
        }

        bitmap = null;
        return false;
    }
}
