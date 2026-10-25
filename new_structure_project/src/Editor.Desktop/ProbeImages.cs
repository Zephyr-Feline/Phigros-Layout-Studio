using Avalonia.Media.Imaging;
using Editor.Rendering;

namespace Editor.Desktop;

/// <summary>Deterministic in-memory images for the headless probes.</summary>
internal static class ProbeImages
{
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

    public static byte[] EncodePng(int width, int height, byte[] bgra)
    {
        using var bitmap = new DecodedRaster(width, height, bgra).ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }
}
