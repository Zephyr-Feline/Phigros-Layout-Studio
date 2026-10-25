namespace Editor.Core;

/// <summary>
/// Content-free geometry and visual style for the stable roles that make up one song card.
/// Positions and sizes are expressed in widths of the reference artwork.
/// </summary>
public sealed record SongStyle(
    string Id,
    string Name,
    double ReferenceAspectRatio,
    IReadOnlyList<SongStyleLayer> Layers)
{
    public static IReadOnlyList<SongRole> RequiredRoles { get; } =
        [SongRole.Artwork, SongRole.Title, SongRole.Artist, SongRole.Charter, SongRole.Badge];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80 || Id.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || Name.Any(char.IsControl))
            throw new InvalidDataException("歌曲样式 ID 或名称缺失/过长。");
        if (!double.IsFinite(ReferenceAspectRatio) || ReferenceAspectRatio is < 0.1 or > 100)
            throw new InvalidDataException("歌曲样式参考宽高比无效。");
        if (Layers is null || Layers.Count is < 5 or > 12)
            throw new InvalidDataException("歌曲样式必须包含五个核心角色，并可选一个底板。");

        var roles = Layers.Select(layer => layer.Role).ToArray();
        if (roles.Any(role => role is SongRole.None || !Enum.IsDefined(role)) ||
            roles.Distinct().Count() != roles.Length)
            throw new InvalidDataException("歌曲样式角色缺失、无效或重复。");
        if (roles.Count(role => RequiredRoles.Contains(role)) != RequiredRoles.Count ||
            roles.Any(role => role is not (SongRole.Artwork or SongRole.Title or SongRole.Artist or
                SongRole.Charter or SongRole.Badge or SongRole.Plate)))
            throw new InvalidDataException("歌曲样式必须恰好包含曲绘、标题、曲师、谱师和难度角色。");

        foreach (var layer in Layers)
        {
            if (!Enum.IsDefined(layer.Kind) || !Enum.IsDefined(layer.Role) ||
                !double.IsFinite(layer.X) || !double.IsFinite(layer.Y) ||
                !double.IsFinite(layer.Width) || !double.IsFinite(layer.Height) ||
                Math.Abs(layer.X) > 20 || Math.Abs(layer.Y) > 20 ||
                layer.Width <= 0 || layer.Height <= 0 ||
                layer.Width > 20 || layer.Height > 20 ||
                !double.IsFinite(layer.Opacity) || layer.Opacity is < 0 or > 1 ||
                !double.IsFinite(layer.RotationDegrees) || layer.RotationDegrees is < -180 or > 180 ||
                !double.IsFinite(layer.FontSizeRatio) || layer.FontSizeRatio is <= 0 or > 10 ||
                string.IsNullOrWhiteSpace(layer.FontFamily) || layer.FontFamily.Length > 200 ||
                !double.IsFinite(layer.LineSpacing) || layer.LineSpacing is < 0.5 or > 4 ||
                !Enum.IsDefined(layer.Alignment) || !Enum.IsDefined(layer.VerticalAlignment) ||
                (layer.ImageFit is ImageFitMode fit && !Enum.IsDefined(fit)))
                throw new InvalidDataException("歌曲样式包含无效的几何或视觉属性。");

            var validKind = layer.Role switch
            {
                SongRole.Artwork => layer.Kind == LayerKind.Image,
                SongRole.Title or SongRole.Artist or SongRole.Charter => layer.Kind == LayerKind.Text,
                SongRole.Badge => layer.Kind == LayerKind.Badge,
                SongRole.Plate => layer.Kind == LayerKind.Shape,
                _ => false
            };
            if (!validKind)
                throw new InvalidDataException("歌曲样式角色与图层类型不匹配。");
            if (layer.Kind != LayerKind.Image && layer.ImageFit is not null)
                throw new InvalidDataException("只有曲绘样式可以保存图片适配方式。");
            if (layer.Slanted && layer.Kind is not (LayerKind.Image or LayerKind.Shape))
                throw new InvalidDataException("文字或徽标不能使用斜切样式。");
            if (layer.Role == SongRole.Artwork &&
                (Math.Abs(layer.X) > 1e-9 || Math.Abs(layer.Y) > 1e-9 ||
                 Math.Abs(layer.Width - 1) > 1e-9 ||
                 Math.Abs(layer.Height - 1 / ReferenceAspectRatio) > 1e-6))
                throw new InvalidDataException("歌曲样式曲绘必须使用完整的参考画框。");
            if (layer.Kind == LayerKind.Badge &&
                (Math.Abs(layer.Width / layer.Height - EditorDocument.BadgeRatio) > 1e-6 ||
                 layer.Width < 8.0 / 20000))
                throw new InvalidDataException("歌曲样式中的难度徽标必须保持 128:108。");
        }
    }

    public bool ContentEquals(SongStyle other) =>
        ReferenceAspectRatio == other.ReferenceAspectRatio &&
        Layers.SequenceEqual(other.Layers);
}

public sealed record SongStyleLayer(
    SongRole Role,
    LayerKind Kind,
    double X,
    double Y,
    double Width,
    double Height,
    bool Visible,
    bool Locked,
    bool KeepAspect,
    bool Slanted,
    double Opacity,
    ImageFitMode? ImageFit,
    double RotationDegrees,
    double FontSizeRatio,
    string FontFamily,
    bool Bold,
    bool Italic,
    double LineSpacing,
    HorizontalAlignment Alignment,
    VerticalAlignment VerticalAlignment,
    uint ColorArgb);
