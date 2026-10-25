using Editor.Core;

namespace Editor.Application;

/// <summary>
/// Captures, applies and instantiates role-based song styles. Styles contain no text, image bytes,
/// asset IDs or source layer/group IDs; application to existing groups preserves those values.
/// </summary>
public static class SongStyles
{
    private static readonly SongRole[] CaptureOrder =
    [
        SongRole.Plate, SongRole.Artwork, SongRole.Title, SongRole.Artist,
        SongRole.Charter, SongRole.Badge
    ];

    public static SongStyle Capture(EditorDocument document, Guid groupId, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var roles = RoleLayers(document, groupId);
        var artwork = Required(roles, SongRole.Artwork);
        var scale = artwork.Bounds.Width;
        if (!double.IsFinite(scale) || scale <= 0)
            throw new InvalidDataException("曲绘宽度无效，无法捕获歌曲样式。");
        var layers = CaptureOrder.Where(roles.ContainsKey).Select(role =>
        {
            var layer = roles[role];
            return new SongStyleLayer(
                role,
                layer.Kind,
                (layer.Bounds.X - artwork.Bounds.X) / scale,
                (layer.Bounds.Y - artwork.Bounds.Y) / scale,
                layer.Bounds.Width / scale,
                layer.Bounds.Height / scale,
                layer.Visible,
                layer.Locked,
                layer.KeepAspect,
                layer.Slanted,
                layer.Opacity,
                layer.Kind == LayerKind.Image ? layer.EffectiveImageFit : null,
                layer.RotationDegrees,
                layer.FontSize / scale,
                layer.FontFamily,
                layer.Bold,
                layer.Italic,
                layer.LineSpacing,
                layer.Alignment,
                layer.VerticalAlignment,
                layer.ColorArgb);
        }).ToArray();
        var style = new SongStyle(Guid.NewGuid().ToString("N"), (name ?? "").Trim(),
            artwork.Bounds.Width / artwork.Bounds.Height, Array.AsReadOnly(layers));
        style.Validate();
        return style;
    }

    public static void Apply(EditorDocument document, SongStyle style, Guid groupId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();
        var roles = RoleLayers(document, groupId);
        var artwork = Required(roles, SongRole.Artwork);
        if (roles.Values.Any(layer => layer.Locked))
            throw new InvalidOperationException("歌曲组包含锁定角色图层，请先解锁再应用歌曲样式。");

        var scale = artwork.Bounds.Width;
        var artworkBounds = artwork.Bounds;
        foreach (var styleLayer in style.Layers)
        {
            if (!roles.TryGetValue(styleLayer.Role, out var target))
            {
                if (styleLayer.Role != SongRole.Plate)
                    throw new InvalidDataException("歌曲组缺少样式所需的角色图层。");
                target = NewRoleLayer(styleLayer.Role, groupId);
                var artworkIndex = document.Layers.IndexOf(artwork);
                document.Layers.Insert(Math.Max(0, artworkIndex), target);
                roles[styleLayer.Role] = target;
            }
            ApplyLayer(document, styleLayer, target, artwork, scale);
            if (styleLayer.Role == SongRole.Artwork) target.Bounds = artworkBounds;
        }
    }

    public static IReadOnlyList<Layer> Instantiate(EditorDocument document, SongStyle style,
        Guid sourceGroupId, double offsetX = 24, double offsetY = 24)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            throw new ArgumentOutOfRangeException(nameof(offsetX));
        if (document.Layers.Count + style.Layers.Count > 200)
            throw new InvalidDataException("创建歌曲样式实例后将超过 200 个图层上限。");
        if (document.Assets.Count + 1 > 200)
            throw new InvalidDataException("创建歌曲样式实例后将超过 200 个资源上限。");
        var sourceRoles = RoleLayers(document, sourceGroupId);
        var sourceArtwork = Required(sourceRoles, SongRole.Artwork);
        if (sourceArtwork.AssetId is not Guid sourceAsset ||
            !document.Assets.TryGetValue(sourceAsset, out var image))
            throw new InvalidDataException("歌曲组曲绘缺少内嵌图片资源。");

        var group = Guid.NewGuid();
        var result = new List<Layer>(style.Layers.Count);
        var anchor = sourceArtwork;
        var scale = sourceArtwork.Bounds.Width;
        foreach (var styleLayer in style.Layers)
        {
            sourceRoles.TryGetValue(styleLayer.Role, out var sourceLayer);
            if (sourceLayer is null && styleLayer.Role != SongRole.Plate)
                throw new InvalidDataException("内容源歌曲组缺少样式所需的角色图层。");
            var layer = NewRoleLayer(styleLayer.Role, group);
            ApplyLayer(document, styleLayer, layer, anchor, scale, offsetX, offsetY);
            if (styleLayer.Role == SongRole.Artwork)
            {
                layer.Bounds = new RectD(anchor.Bounds.X + offsetX, anchor.Bounds.Y + offsetY,
                    anchor.Bounds.Width, anchor.Bounds.Height);
                var assetId = Guid.NewGuid();
                document.AddAsset(assetId, image);
                layer.AssetId = assetId;
                if (sourceLayer is null) throw new InvalidDataException("内容源歌曲组缺少曲绘图层。");
                layer.ImageCrop = sourceLayer.ImageCrop;
            }
            else if (styleLayer.Kind == LayerKind.Text)
            {
                if (sourceLayer is null) throw new InvalidDataException("内容源歌曲组缺少文字图层。");
                layer.Text = sourceLayer.Text;
            }
            else if (styleLayer.Kind == LayerKind.Badge)
            {
                if (sourceLayer is null) throw new InvalidDataException("内容源歌曲组缺少难度徽标。");
                layer.Difficulty = sourceLayer.Difficulty;
                layer.Level = sourceLayer.Level;
            }
            result.Add(layer);
        }
        document.Layers.AddRange(result);
        return result;
    }

    private static Dictionary<SongRole, Layer> RoleLayers(EditorDocument document, Guid groupId)
    {
        var group = document.Layers.Where(layer => layer.GroupId == groupId).ToArray();
        if (group.Length == 0)
            throw new InvalidOperationException("所选内容没有歌曲组。");
        var roles = group.Where(layer => layer.SongRole != SongRole.None)
            .GroupBy(layer => layer.SongRole).ToDictionary(role => role.Key, role => role.ToArray());
        if (roles.Any(role => role.Value.Length != 1))
            throw new InvalidDataException("歌曲组存在重复或无效的持久角色。");
        foreach (var role in SongStyle.RequiredRoles)
            if (!roles.ContainsKey(role))
                throw new InvalidOperationException(
                    "歌曲组缺少持久角色；请先在“歌曲信息工作台”确认五个角色映射。");
        return roles.ToDictionary(role => role.Key, role => role.Value[0]);
    }

    private static Layer Required(IReadOnlyDictionary<SongRole, Layer> roles, SongRole role) =>
        roles.TryGetValue(role, out var layer)
            ? layer
            : throw new InvalidDataException("歌曲组缺少样式所需的角色图层。");

    private static Layer NewRoleLayer(SongRole role, Guid group) => new()
    {
        Id = Guid.NewGuid(),
        Kind = role switch
        {
            SongRole.Artwork => LayerKind.Image,
            SongRole.Title or SongRole.Artist or SongRole.Charter => LayerKind.Text,
            SongRole.Badge => LayerKind.Badge,
            SongRole.Plate => LayerKind.Shape,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        },
        Name = role switch
        {
            SongRole.Artwork => "歌曲 · 曲绘",
            SongRole.Title => "歌曲 · 标题",
            SongRole.Artist => "歌曲 · 曲师",
            SongRole.Charter => "歌曲 · 谱师",
            SongRole.Badge => "歌曲 · 难度",
            SongRole.Plate => "歌曲 · 信息底板",
            _ => "歌曲角色"
        },
        GroupId = group,
        SongRole = role
    };

    private static void ApplyLayer(EditorDocument document, SongStyleLayer style, Layer target,
        Layer anchor, double scale, double offsetX = 0, double offsetY = 0)
    {
        var width = style.Width * scale;
        var height = style.Height * scale;
        if (target.Kind == LayerKind.Badge)
        {
            width = Math.Max(width, 8);
            height = width / EditorDocument.BadgeRatio;
        }
        var fontSize = style.FontSizeRatio * scale;
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            width is < 1 or > 20000 || height is < 1 or > 20000 ||
            !double.IsFinite(fontSize) || fontSize is < 1 or > 1000)
            throw new InvalidDataException("曲绘尺寸导致歌曲样式超出图层允许范围。");
        target.Bounds = new RectD(
            anchor.Bounds.X + offsetX + style.X * scale,
            anchor.Bounds.Y + offsetY + style.Y * scale,
            width,
            height);
        target.Visible = style.Visible;
        target.Locked = style.Locked;
        target.KeepAspect = style.KeepAspect;
        target.Slanted = style.Slanted;
        target.Opacity = style.Opacity;
        target.RotationDegrees = style.RotationDegrees;
        target.FontSize = fontSize;
        target.FontFamily = style.FontFamily;
        target.Bold = style.Bold;
        target.Italic = style.Italic;
        target.LineSpacing = style.LineSpacing;
        target.Alignment = style.Alignment;
        target.VerticalAlignment = style.VerticalAlignment;
        target.ColorArgb = style.ColorArgb;
        if (target.Kind == LayerKind.Image)
            target.ImageFit = style.ImageFit ?? ImageFitMode.Stretch;
        else
            target.ImageFit = null;
    }
}
