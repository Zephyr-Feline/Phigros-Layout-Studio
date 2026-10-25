using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Editor.Core;

namespace Editor.Desktop;

/// <summary>Schematic preview of role geometry and colours; never reads text or image content.</summary>
public sealed class SongStylePreview : Control
{
    public static readonly StyledProperty<SongStyle?> StyleProperty =
        AvaloniaProperty.Register<SongStylePreview, SongStyle?>(nameof(Style));

    static SongStylePreview()
    {
        AffectsRender<SongStylePreview>(StyleProperty);
    }

    public SongStyle? Style
    {
        get => GetValue(StyleProperty);
        set => SetValue(StyleProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var outer = new Rect(Bounds.Size);
        if (outer.Width <= 2 || outer.Height <= 2) return;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(24, 38, 47)), outer);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(94, 119, 132)), 1), outer);
        var style = Style;
        if (style is null || style.Layers.Count == 0) return;

        const double margin = 10;
        var minX = Math.Min(0, style.Layers.Min(layer => layer.X));
        var minY = Math.Min(0, style.Layers.Min(layer => layer.Y));
        var maxX = Math.Max(1, style.Layers.Max(layer => layer.X + layer.Width));
        var maxY = Math.Max(1 / style.ReferenceAspectRatio,
            style.Layers.Max(layer => layer.Y + layer.Height));
        var extentWidth = Math.Max(0.01, maxX - minX);
        var extentHeight = Math.Max(0.01, maxY - minY);
        var scale = Math.Min((outer.Width - margin * 2) / extentWidth,
            (outer.Height - margin * 2) / extentHeight);
        if (!double.IsFinite(scale) || scale <= 0) return;
        var originX = margin - minX * scale;
        var originY = margin - minY * scale;
        foreach (var layer in style.Layers)
        {
            var box = new Rect(originX + layer.X * scale, originY + layer.Y * scale,
                Math.Max(1, layer.Width * scale), Math.Max(1, layer.Height * scale));
            var (fill, stroke) = layer.Role switch
            {
                SongRole.Artwork => (Color.FromArgb(125, 58, 144, 196),
                    Color.FromArgb(235, 140, 215, 255)),
                SongRole.Plate => (Color.FromArgb(155, 24, 42, 52),
                    Color.FromArgb(220, 128, 153, 166)),
                SongRole.Title => (Color.FromArgb(130, 255, 195, 62),
                    Color.FromArgb(240, 255, 226, 147)),
                SongRole.Artist => (Color.FromArgb(115, 82, 205, 163),
                    Color.FromArgb(235, 161, 245, 211)),
                SongRole.Charter => (Color.FromArgb(110, 103, 157, 235),
                    Color.FromArgb(235, 178, 207, 255)),
                SongRole.Badge => (Color.FromArgb(145, 207, 69, 69),
                    Color.FromArgb(245, 255, 167, 167)),
                _ => (Color.FromArgb(90, 180, 180, 180), Color.FromArgb(180, 220, 220, 220))
            };
            context.DrawRectangle(new SolidColorBrush(fill),
                new Pen(new SolidColorBrush(stroke), 1), box);
        }
        var artworkBox = new Rect(originX, originY, Math.Max(1, scale),
            Math.Max(1, scale / style.ReferenceAspectRatio));
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1.5),
            artworkBox);
    }
}
