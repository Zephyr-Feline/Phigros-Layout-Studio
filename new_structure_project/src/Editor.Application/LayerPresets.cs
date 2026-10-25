using Editor.Core;

namespace Editor.Application;

/// <summary>Native equivalents of the free editor's shape, song and Original defaults.</summary>
public static class LayerPresets
{
    public static Layer Shape(EditorDocument document) => new()
    {
        Kind = LayerKind.Shape, Name = "Shape", Slanted = true,
        Bounds = new RectD(document.CanvasWidth / 2.0 - 200,
            document.CanvasHeight / 2.0 - 35, 400, 300),
        ColorArgb = 0x96142837
    };

    internal static Layer[] OriginalTag(EditorDocument document, Layer? artwork, Guid group)
    {
        var scale = artwork is null ? 1 : Math.Clamp(artwork.Bounds.Width / 520, 0.1, 10);
        var width = 240 * scale;
        var height = 36 * scale;
        var x = artwork is null ? document.CanvasWidth / 2.0 - width / 2 :
            artwork.Bounds.X + artwork.Bounds.Width - width;
        var y = artwork is null ? document.CanvasHeight / 2.0 - height / 2 :
            artwork.Bounds.Y + artwork.Bounds.Height + 115 * scale;
        var padding = Math.Min(width * 0.2, height * 0.268 + 4);
        return
        [
            new Layer
            {
                Kind = LayerKind.Shape, Name = "Original label", GroupId = group,
                Bounds = new RectD(x, y, width, height), Slanted = true, KeepAspect = true,
                Visible = artwork?.Visible ?? true, ColorArgb = 0xFFFFFFFF
            },
            new Layer
            {
                Kind = LayerKind.Text, Name = "Original label caption", GroupId = group,
                Bounds = new RectD(x + padding, y, Math.Max(1, width - 2 * padding), height),
                Text = "Phigros Original", FontSize = 22 * scale,
                Alignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                KeepAspect = true,
                Visible = artwork?.Visible ?? true, ColorArgb = 0xFF000000
            }
        ];
    }

    internal static Layer[] SongCompanions(Layer artwork, Guid group)
    {
        var art = artwork.Bounds;
        var scale = Math.Min(art.Width / 520, art.Height / 250);
        if (scale < 8.0 / 104 || scale > 1000.0 / 34)
            throw new InvalidOperationException("Artwork is too small or too large for a song card.");
        var bottom = art.Y + art.Height;
        return
        [
            new Layer
            {
                Kind = LayerKind.Shape, Name = "New song · info bar", GroupId = group,
                SongRole = SongRole.Plate,
                Bounds = new RectD(art.X - 15 * scale, bottom - 10 * scale,
                    520 * scale, 125 * scale), Slanted = true, ColorArgb = 0xBE0A121A,
                Visible = artwork.Visible
            },
            new Layer
            {
                Kind = LayerKind.Text, Name = "New song · title", GroupId = group,
                SongRole = SongRole.Title,
                Bounds = new RectD(art.X + 12 * scale, bottom, 420 * scale, 49 * scale),
                Text = "New song", FontSize = 34 * scale,
                VerticalAlignment = VerticalAlignment.Center, KeepAspect = true,
                Visible = artwork.Visible
            },
            new Layer
            {
                Kind = LayerKind.Text, Name = "New song · artist", GroupId = group,
                SongRole = SongRole.Artist,
                Bounds = new RectD(art.X + 12 * scale, bottom + 45 * scale, 420 * scale, 35 * scale),
                Text = "Music artist", FontSize = 23 * scale,
                VerticalAlignment = VerticalAlignment.Center, KeepAspect = true,
                Visible = artwork.Visible
            },
            new Layer
            {
                Kind = LayerKind.Text, Name = "New song · charter", GroupId = group,
                SongRole = SongRole.Charter,
                Bounds = new RectD(art.X + 12 * scale, bottom + 79 * scale, 420 * scale, 32 * scale),
                Text = "Chart author", FontSize = 21 * scale, ColorArgb = 0xFFD2E1EB,
                VerticalAlignment = VerticalAlignment.Center, KeepAspect = true,
                Visible = artwork.Visible
            },
            new Layer
            {
                Kind = LayerKind.Badge, Name = "New song · difficulty", GroupId = group,
                SongRole = SongRole.Badge,
                Bounds = new RectD(art.X + art.Width - 101 * scale,
                    bottom - 66 * scale, 104 * scale, 104 * scale / EditorDocument.BadgeRatio),
                KeepAspect = true, Difficulty = "IN", Level = 15, Visible = artwork.Visible
            }
        ];
    }
}
