using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Editor.Core;

namespace Editor.Rendering;

/// <summary>Single document scene path used by preview and offscreen PNG. No editor overlays.</summary>
public static class ScenePainter
{
    public static void Paint(DrawingContext context, EditorDocument document,
        AssetImageDecoder? decoder = null, bool transparentBackground = false,
        BackgroundRenderCache? backgroundCache = null, DecodedImageCache? imageCache = null)
    {
        document.Validate();
        decoder ??= AssetImageDecoder.RendererOnly;
        AssetBlob? background = null;
        var coverBackground = false;
        if (!transparentBackground)
        {
            if (document.FixedBackgroundAssetId is Guid backgroundId)
            {
                background = document.Assets[backgroundId];
                coverBackground = true;
                backgroundCache?.Clear();
            }
            else if (document.BackgroundBlend.Enabled)
                background = (backgroundCache ?? new BackgroundRenderCache()).Get(document, decoder);
            else backgroundCache?.Clear();
        }
        else backgroundCache?.Clear();
        imageCache?.Retain(document.Layers.Where(layer => layer.Visible && layer.Kind == LayerKind.Image)
            .Select(layer => document.Assets[layer.AssetId!.Value])
            .Concat(background is null ? [] : new[] { background }), decoder);
        if (!transparentBackground)
        {
            context.FillRectangle(Brush(document.BackgroundArgb),
                new Rect(0, 0, document.CanvasWidth, document.CanvasHeight));
            if (background is not null)
                DrawImage(context, decoder, background,
                    new Rect(0, 0, document.CanvasWidth, document.CanvasHeight), imageCache,
                    coverBackground ? ImageFitMode.Cover : ImageFitMode.Stretch);
        }

        using (context.PushClip(new Rect(0, 0, document.CanvasWidth, document.CanvasHeight)))
        {
            foreach (var layer in document.Layers.Where(layer => layer.Visible))
            {
                var box = layer.Bounds;
                var rect = new Rect(box.X, box.Y, box.Width, box.Height);
                var centre = LayerGeometry.Centre(box);
                using var rotation = context.PushTransform(
                    Matrix.CreateTranslation(-centre.X, -centre.Y) *
                    Matrix.CreateRotation(layer.RotationDegrees * Math.PI / 180) *
                    Matrix.CreateTranslation(centre.X, centre.Y));
                using var opacity = context.PushOpacity(layer.Opacity);
                switch (layer.Kind)
                {
                    case LayerKind.Image:
                        if (layer.Slanted)
                        {
                            using (context.PushGeometryClip(SlantedGeometry(box)))
                                DrawImage(context, decoder,
                                    document.Assets[layer.AssetId!.Value], rect, imageCache,
                                    layer.EffectiveImageFit, layer.ImageCrop);
                        }
                        else
                            DrawImage(context, decoder, document.Assets[layer.AssetId!.Value], rect,
                                imageCache, layer.EffectiveImageFit, layer.ImageCrop);
                        break;
                    case LayerKind.Shape:
                        if (layer.Slanted)
                            context.DrawGeometry(Brush(layer.ColorArgb), null, SlantedGeometry(box));
                        else
                            context.FillRectangle(Brush(layer.ColorArgb), rect);
                        break;
                    case LayerKind.Text:
                        DrawText(context, layer, rect);
                        break;
                    case LayerKind.Badge:
                        DrawBadge(context, layer, rect);
                        break;
                }
            }
        }
    }

    private static void DrawImage(DrawingContext context, AssetImageDecoder decoder, AssetBlob asset,
        Rect destination, DecodedImageCache? cache, ImageFitMode fit = ImageFitMode.Stretch,
        RectD? crop = null)
    {
        if (cache is not null) { cache.Use(asset, decoder, Draw); return; }
        using var bitmap = decoder.Decode(asset);
        Draw(bitmap);

        void Draw(Bitmap image)
        {
            var source = SourceRect(image.PixelSize.Width, image.PixelSize.Height, crop);
            var target = destination;
            var sampled = fit switch
            {
                ImageFitMode.Cover => CoverSource(source.Width, source.Height,
                    destination.Width, destination.Height, source.X, source.Y),
                _ => source
            };
            if (fit == ImageFitMode.Contain)
            {
                var scale = Math.Min(destination.Width / source.Width, destination.Height / source.Height);
                target = new Rect(destination.X + (destination.Width - source.Width * scale) / 2,
                    destination.Y + (destination.Height - source.Height * scale) / 2,
                    source.Width * scale, source.Height * scale);
                sampled = source;
            }
            context.DrawImage(image, sampled, target);
        }
    }

    private static Rect SourceRect(int width, int height, RectD? crop)
    {
        var box = crop ?? new RectD(0, 0, 1, 1);
        var left = Math.Clamp((int)Math.Floor(box.X * width), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(box.Y * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((box.X + box.Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((box.Y + box.Height) * height), top + 1, height);
        return new Rect(left, top, right - left, bottom - top);
    }

    internal static Rect CoverSource(double sourceWidth, double sourceHeight,
        double targetWidth, double targetHeight, double sourceX = 0, double sourceY = 0)
    {
        var scale = Math.Max(targetWidth / sourceWidth, targetHeight / sourceHeight);
        var cropWidth = targetWidth / scale;
        var cropHeight = targetHeight / scale;
        return new Rect(sourceX + (sourceWidth - cropWidth) / 2, sourceY + (sourceHeight - cropHeight) / 2,
            cropWidth, cropHeight);
    }

    private static StreamGeometry SlantedGeometry(RectD bounds)
    {
        var points = SlantedLayerGeometry.Polygon(bounds);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(points[0].X, points[0].Y), true);
            foreach (var point in points.Skip(1))
                path.LineTo(new Point(point.X, point.Y));
            path.EndFigure(true);
        }
        return geometry;
    }

    private static void DrawText(DrawingContext context, Layer layer, Rect rect)
    {
        using (context.PushClip(rect))
        {
            var typeface = new Typeface(LocalFontRegistry.Resolve(layer.FontFamily),
                layer.Italic ? FontStyle.Italic : FontStyle.Normal,
                layer.Bold ? FontWeight.Bold : FontWeight.Normal);
            var alignment = layer.Alignment switch
            {
                Editor.Core.HorizontalAlignment.Center => TextAlignment.Center,
                Editor.Core.HorizontalAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left
            };
            using var layout = new TextLayout(layer.Text, typeface, layer.FontSize,
                Brush(layer.ColorArgb), textAlignment: alignment,
                textWrapping: TextWrapping.Wrap, maxWidth: rect.Width,
                lineHeight: layer.FontSize * layer.LineSpacing);
            var available = Math.Max(0, rect.Height - layout.Height);
            var offsetY = layer.VerticalAlignment switch
            {
                Editor.Core.VerticalAlignment.Center => available / 2,
                Editor.Core.VerticalAlignment.Bottom => available,
                _ => 0
            };
            layout.Draw(context, new Point(rect.X, rect.Y + offsetY));
        }
    }

    private static void DrawBadge(DrawingContext context, Layer layer, Rect rect)
    {
        uint color = layer.Difficulty switch
        {
            "EZ" => 0xFF0FB428,
            "HD" => 0xFF0078AF,
            "IN" => 0xFFCF1313,
            "AT" => 0xFF232323,
            _ => throw new InvalidDataException("Unknown difficulty.")
        };
        context.FillRectangle(Brush(color), rect);
        using (context.PushClip(rect))
        {
            var font = new Typeface("Microsoft YaHei UI", FontStyle.Normal, FontWeight.Bold);
            var label = $"{layer.Difficulty}\n{layer.Level.ToString(CultureInfo.InvariantCulture)}";
            using var layout = new TextLayout(label, font, rect.Height * 0.28,
                Brushes.White, textAlignment: TextAlignment.Center,
                textWrapping: TextWrapping.NoWrap, maxWidth: rect.Width);
            layout.Draw(context, rect.TopLeft);
        }
    }

    private static IBrush Brush(uint argb) => new SolidColorBrush(Color.FromUInt32(argb));
}
