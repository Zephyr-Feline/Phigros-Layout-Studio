using Editor.Core;

namespace Editor.Application;

public sealed record SongInfoMapping(
    Guid GroupId, Guid ArtworkId, Guid TitleId, Guid ArtistId, Guid CharterId, Guid BadgeId);

public sealed record SongInfoValues(
    string Title, string Artist, string Charter, string Difficulty, int Level);

public sealed record SongInfoEdit(
    SongInfoMapping Mapping, SongInfoValues Values, byte[]? ReplacementArtwork = null,
    string? ReplacementArtworkPath = null);

public sealed record SongTextStyle(
    double? FontSize = null, uint? ColorArgb = null, bool? Bold = null)
{
    public bool IsEmpty => FontSize is null && ColorArgb is null && Bold is null;

    public void Validate()
    {
        if (FontSize is double size && (!double.IsFinite(size) || size is < 1 or > 1000))
            throw new InvalidDataException("字号必须在 1 到 1000 之间。");
    }

    public void ApplyTo(Layer layer)
    {
        if (FontSize is double size) layer.FontSize = size;
        if (ColorArgb is uint color) layer.ColorArgb = color;
        if (Bold is bool bold) layer.Bold = bold;
    }
}

public sealed record SongInfoSuggestion(
    Guid GroupId,
    Guid? ArtworkId,
    Guid? TitleId,
    Guid? ArtistId,
    Guid? CharterId,
    Guid? BadgeId,
    string Title,
    string Artist,
    string Charter,
    string Difficulty,
    int Level,
    string GroupLabel,
    bool HasPersistedRoles);

/// <summary>
/// Role suggestions are only a UI convenience. Applying requires an explicit mapping and is
/// validated against the group, kinds and lock state before one transaction.
/// </summary>
public static class SongInfoWorkbench
{
    public static IReadOnlyList<SongInfoSuggestion> Suggest(EditorDocument document,
        IEnumerable<Guid> groupIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        var groups = groupIds.Distinct().ToArray();
        var result = new List<SongInfoSuggestion>(groups.Length);
        foreach (var groupId in groups)
        {
            var layers = document.Layers.Where(layer => layer.GroupId == groupId).ToArray();
            if (layers.Length == 0) continue;
            var artwork = Role(layers, LayerKind.Image, SongRole.Artwork, "· artwork");
            var title = Role(layers, LayerKind.Text, SongRole.Title, "· title");
            var artist = Role(layers, LayerKind.Text, SongRole.Artist, "· artist");
            var charter = Role(layers, LayerKind.Text, SongRole.Charter, "· charter");
            var badge = Role(layers, LayerKind.Badge, SongRole.Badge, "· difficulty");
            result.Add(new(groupId, artwork?.Id, title?.Id, artist?.Id, charter?.Id, badge?.Id,
                title?.Text ?? "", artist?.Text ?? "", charter?.Text ?? "",
                badge?.Difficulty ?? "IN", badge?.Level ?? 15,
                $"组 {groupId.ToString("N")[..8]}",
                layers.Any(layer => layer.SongRole != SongRole.None)));
        }
        return result;
    }

    private static Layer? Unique(IReadOnlyList<Layer> layers, LayerKind kind, string suffix) =>
        layers.Where(layer => layer.Kind == kind &&
            layer.Name.EndsWith(suffix, StringComparison.Ordinal)).Take(2).Count() == 1
                ? layers.Single(layer => layer.Kind == kind &&
                    layer.Name.EndsWith(suffix, StringComparison.Ordinal))
                : null;

    private static Layer? Role(IReadOnlyList<Layer> layers, LayerKind kind,
        SongRole role, string suffix)
    {
        var byRole = layers.Where(layer => layer.SongRole == role).Take(2).ToArray();
        if (byRole.Length == 1 && byRole[0].Kind == kind) return byRole[0];
        return Unique(layers, kind, suffix);
    }
}
