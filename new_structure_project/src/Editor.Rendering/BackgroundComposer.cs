using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Editor.Core;

namespace Editor.Rendering;

/// <summary>Builds one opaque, seamless background image from the current visible artwork.</summary>
public static class BackgroundComposer
{
    private const int SampleSize = 12;
    private const int MaxPreviewSide = 256;
    private const double BaseWeight = 0.16;

    private sealed record Contributor(RectD Bounds, byte[] Samples, double Opacity);

    public static Layer[] ContributingLayers(EditorDocument document)
    {
        var settings = document.BackgroundBlend;
        var layers = document.Layers.Where(layer => layer.Visible && layer.Kind == LayerKind.Image &&
            layer.Opacity > 0 && (layer.Slanted || settings.IncludeFlatImages));
        if (settings.ContributionMode == BackgroundContributionMode.Selected)
        {
            var selected = (settings.SelectedArtworkLayerIds ?? []).ToHashSet();
            layers = layers.Where(layer => selected.Contains(layer.Id));
        }
        return layers.ToArray();
    }

    public static byte[] Generate(EditorDocument document, AssetImageDecoder? decoder = null)
        => Generate(document, decoder ?? AssetImageDecoder.RendererOnly, null);

    internal static byte[] Generate(EditorDocument document, AssetImageDecoder decoder,
        Func<AssetBlob, ImageFitMode, RectD, byte[]>? sample)
    {
        document.Validate();
        var contributors = ContributingLayers(document)
            .Select(layer => new Contributor(LayerGeometry.VisualBounds(layer),
                sample?.Invoke(document.Assets[layer.AssetId!.Value],
                    layer.EffectiveImageFit, layer.ImageCrop) ??
                Sample(document.Assets[layer.AssetId!.Value], decoder,
                    layer.EffectiveImageFit, layer.ImageCrop),
                layer.Opacity))
            .ToArray();
        if (contributors.Length == 0)
            throw new InvalidOperationException("Add at least one visible slanted artwork layer before generating a blended background.");

        var settings = document.BackgroundBlend;
        var factor = (double)MaxPreviewSide / Math.Max(document.CanvasWidth, document.CanvasHeight);
        var width = Math.Max(1, (int)Math.Round(document.CanvasWidth * factor));
        var height = Math.Max(1, (int)Math.Round(document.CanvasHeight * factor));
        var pixels = new double[width * height * 3];
        var baseR = (byte)(document.BackgroundArgb >> 16);
        var baseG = (byte)(document.BackgroundArgb >> 8);
        var baseB = (byte)document.BackgroundArgb;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var px = (x + 0.5) * document.CanvasWidth / width;
            var py = (y + 0.5) * document.CanvasHeight / height;
            var total = BaseWeight;
            var red = baseR * total;
            var green = baseG * total;
            var blue = baseB * total;
            foreach (var art in contributors)
            {
                var box = art.Bounds;
                var dx = (px - box.X - box.Width / 2) /
                    (Math.Max(document.CanvasWidth * 0.24, box.Width) * settings.Range);
                var dy = (py - box.Y - box.Height / 2) /
                    (Math.Max(document.CanvasHeight * 0.48, box.Height) * settings.Range);
                var sx = Math.Clamp((int)Math.Round(5.5 + dx * 4), 0, SampleSize - 1);
                var sy = Math.Clamp((int)Math.Round(5.5 + dy * 4), 0, SampleSize - 1);
                var at = (sy * SampleSize + sx) * 4;
                var alpha = art.Samples[at + 3];
                if (alpha == 0) continue;
                var falloff = 2.75 - 2.5 * settings.Transition;
                var weight = Math.Exp(-falloff * (dx * dx + dy * dy)) * alpha / 255.0 * art.Opacity;
                total += weight;
                // RenderTargetBitmap returns premultiplied BGRA; undo that before weighting
                // by opacity so a translucent source is attenuated only once.
                blue += Math.Min(255, art.Samples[at] * 255.0 / alpha) * weight;
                green += Math.Min(255, art.Samples[at + 1] * 255.0 / alpha) * weight;
                red += Math.Min(255, art.Samples[at + 2] * 255.0 / alpha) * weight;
            }
            var output = (y * width + x) * 3;
            pixels[output] = blue / total;
            pixels[output + 1] = green / total;
            pixels[output + 2] = red / total;
        }

        pixels = Blur(pixels, width, height, document.BackgroundBlend.BlurRadius);
        var darkness = document.BackgroundBlend.Darkness * 255;
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var from = i * 3;
            var to = i * 4;
            // Match the existing free editor's subtle dark overlay, applied only to the background.
            bgra[to] = Channel((pixels[from] * (255 - darkness) + 20 * darkness) / 255);
            bgra[to + 1] = Channel((pixels[from + 1] * (255 - darkness) + 12 * darkness) / 255);
            bgra[to + 2] = Channel((pixels[from + 2] * (255 - darkness) + 4 * darkness) / 255);
            bgra[to + 3] = 255;
        }
        using var bitmap = new DecodedRaster(width, height, bgra).ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    internal static byte[] Sample(AssetBlob asset, AssetImageDecoder decoder,
        ImageFitMode fit, RectD crop)
    {
        using var bitmap = decoder.Decode(asset);
        using var target = new RenderTargetBitmap(new PixelSize(SampleSize, SampleSize), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            var width = bitmap.PixelSize.Width;
            var height = bitmap.PixelSize.Height;
            var left = Math.Clamp((int)Math.Floor(crop.X * width), 0, width - 1);
            var top = Math.Clamp((int)Math.Floor(crop.Y * height), 0, height - 1);
            var right = Math.Clamp((int)Math.Ceiling((crop.X + crop.Width) * width),
                left + 1, width);
            var bottom = Math.Clamp((int)Math.Ceiling((crop.Y + crop.Height) * height),
                top + 1, height);
            var source = new Rect(left, top, right - left, bottom - top);
            var destination = new Rect(0, 0, SampleSize, SampleSize);
            var sampled = fit switch
            {
                ImageFitMode.Cover => ScenePainter.CoverSource(source.Width, source.Height,
                    SampleSize, SampleSize, source.X, source.Y),
                _ => source
            };
            if (fit == ImageFitMode.Contain)
            {
                var scale = Math.Min((double)SampleSize / source.Width, (double)SampleSize / source.Height);
                destination = new Rect((SampleSize - source.Width * scale) / 2,
                    (SampleSize - source.Height * scale) / 2,
                    source.Width * scale, source.Height * scale);
            }
            context.DrawImage(bitmap, sampled, destination);
        }
        var pixels = new byte[SampleSize * SampleSize * 4];
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(0, 0, SampleSize, SampleSize),
                pinned.AddrOfPinnedObject(), pixels.Length, SampleSize * 4);
        }
        finally { pinned.Free(); }
        return pixels;
    }

    private static double[] Blur(double[] source, int width, int height, int radius)
    {
        if (radius == 0) return source;
        var weights = Enumerable.Range(-radius, radius * 2 + 1)
            .Select(offset => Math.Exp(-offset * offset / (radius * radius / 2.0))).ToArray();
        var sum = weights.Sum();
        var temporary = new double[source.Length];
        var result = new double[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var value = 0.0;
            for (var step = -radius; step <= radius; step++)
                value += source[(y * width + Math.Clamp(x + step, 0, width - 1)) * 3 + channel] *
                    weights[step + radius];
            temporary[(y * width + x) * 3 + channel] = value / sum;
        }
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var channel = 0; channel < 3; channel++)
        {
            var value = 0.0;
            for (var step = -radius; step <= radius; step++)
                value += temporary[(Math.Clamp(y + step, 0, height - 1) * width + x) * 3 + channel] *
                    weights[step + radius];
            result[(y * width + x) * 3 + channel] = value / sum;
        }
        return result;
    }

    private static byte Channel(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
