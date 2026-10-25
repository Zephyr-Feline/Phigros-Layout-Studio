using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Editor.Core;
using Editor.Imaging.Windows;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>Shared decode, render and pixel helpers for the image and geometry assertions.</summary>
internal static class TestImages
{
    /// <summary>Skia plus the Windows TIFF codec, matching the shipping editor.</summary>
    public static AssetImageDecoder Decoder { get; } = new(new WicTiffImageDecoder());

    public static void ValidateImages(EditorDocument document) =>
        ImageAssetValidator.Validate(document, Decoder);

    public static byte[] RenderPng(EditorDocument document, bool transparentBackground = false,
        int? outputWidth = null)
    {
        AvaloniaTestHost.EnsureStarted();
        return PngExporter.Render(document, transparentBackground, Decoder, outputWidth);
    }

    public static byte[] RenderPngRegion(EditorDocument document, bool transparentBackground,
        int outputWidth, PngExportRegion outputRegion)
    {
        AvaloniaTestHost.EnsureStarted();
        return PngExporter.Render(document, transparentBackground, Decoder, outputWidth, outputRegion);
    }

    public static (int Width, int Height, byte[] Bgra) DecodePng(byte[] png)
    {
        AvaloniaTestHost.EnsureStarted();
        using var bitmap = new Bitmap(new MemoryStream(png));
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(),
                pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }
        return (width, height, pixels);
    }

    /// <summary>
    /// Ink bounding box of a render made with a black background and white layers. Channel order is
    /// irrelevant because every channel of a black pixel is zero.
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom) InkBounds(byte[] png)
    {
        var (width, height, pixels) = DecodePng(png);
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                if (pixels[offset] == 0 && pixels[offset + 1] == 0 && pixels[offset + 2] == 0) continue;
                if (x < left) left = x;
                if (y < top) top = y;
                if (x > right) right = x;
                if (y > bottom) bottom = y;
            }
        }
        return (left, top, right, bottom);
    }

    public static byte[] EncodePng(int width, int height, byte[] bgra)
    {
        AvaloniaTestHost.EnsureStarted();
        using var bitmap = new DecodedRaster(width, height, bgra).ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    public static byte[] SyntheticBgra(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)(x * 4);
                pixels[offset + 1] = (byte)(y * 8);
                pixels[offset + 2] = (byte)((x + y) * 3);
                pixels[offset + 3] = 255;
            }
        }
        return pixels;
    }

    /// <summary>
    /// Compares two renders. Payload decoding is pixel exact (delta 0); drawing a platform bitmap
    /// can differ from a Skia-decoded bitmap by one rounding step once the image is resampled, so
    /// scaled comparisons pass a small channel tolerance while still requiring identical size and
    /// identical ink placement.
    /// </summary>
    public static void AssertRendersEquivalent(byte[] expected, byte[] actual, int maxChannelDelta)
    {
        var (width, height, first) = DecodePng(expected);
        var (otherWidth, otherHeight, second) = DecodePng(actual);
        Assert.Multiple(() =>
        {
            Assert.That(otherWidth, Is.EqualTo(width), "render width");
            Assert.That(otherHeight, Is.EqualTo(height), "render height");
            Assert.That(InkBounds(actual), Is.EqualTo(InkBounds(expected)), "ink bounding box");
        });
        var max = 0;
        var differing = 0;
        for (var i = 0; i < first.Length; i++)
        {
            var delta = Math.Abs(first[i] - second[i]);
            if (delta == 0) continue;
            differing++;
            if (delta > max) max = delta;
        }
        Assert.Multiple(() =>
        {
            Assert.That(max, Is.LessThanOrEqualTo(maxChannelDelta), "channel delta");
            if (maxChannelDelta == 0) Assert.That(differing, Is.Zero, "differing bytes");
        });
    }
}
