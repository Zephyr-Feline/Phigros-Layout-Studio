using Editor.Core;

namespace Editor.Application;

public sealed record AssetReference(Guid LayerId, string LayerName, LayerKind Kind,
    bool Visible, bool Locked);

public sealed record AssetReplacement(Guid ReplacementAssetId, IReadOnlyList<Guid> LayerIds);

public sealed record AssetCleanupResult(IReadOnlyList<Guid> AssetIds, long Bytes)
{
    public int Count => AssetIds.Count;
    public bool Changed => AssetIds.Count > 0;
}

public sealed record AssetResource(
    Guid AssetId,
    string Sha256,
    long Length,
    int? Width,
    int? Height,
    string? DimensionDiagnostic,
    IReadOnlyList<AssetReference> LayerReferences,
    bool IsFixedBackground)
{
    public bool IsReferenced => LayerReferences.Count > 0 || IsFixedBackground;
    public bool IsUnreferenced => !IsReferenced;
    public bool IsShared => LayerReferences.Count > 1;
    public bool IsLargeEncoded => Length > 8L * 1024 * 1024;
    public bool IsHighResolution => Width is int w && Height is int h && (long)w * h > 16_000_000;
    public bool HasWarning => IsUnreferenced || IsLargeEncoded || IsHighResolution ||
        DimensionDiagnostic is not null;
    public string Label => $"{AssetId.ToString("N")[..8]} · " +
        (Width is int width && Height is int height ? $"{width}×{height}" : "尺寸不可读") +
        $" · {FormatBytes(Length)} · {LayerReferences.Count} 处图层引用" +
        (IsFixedBackground ? " · 固定背景" : "");

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KiB",
        _ => $"{bytes / (1024.0 * 1024):0.##} MiB"
    };
}

/// <summary>Read-only inventory of embedded assets and their current document references.</summary>
public static class AssetResources
{
    public static string FormatBytes(long bytes) => AssetResource.FormatBytes(bytes);

    public static AssetCleanupResult PlanCleanup(EditorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var used = document.Layers.Where(layer => layer.AssetId.HasValue)
            .Select(layer => layer.AssetId!.Value).ToHashSet();
        if (document.FixedBackgroundAssetId is Guid backgroundId) used.Add(backgroundId);
        var ids = document.Assets.Keys.Where(id => !used.Contains(id)).Order().ToArray();
        return new(Array.AsReadOnly(ids), ids.Sum(id => document.Assets[id].Length));
    }

    public static IReadOnlyList<AssetResource> Inspect(EditorDocument document,
        Func<AssetBlob, (int Width, int Height)>? dimensionProbe = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var references = document.Layers
            .Where(layer => layer.AssetId is not null)
            .SelectMany(layer => new[] { (Layer: layer, AssetId: layer.AssetId!.Value) })
            .GroupBy(pair => pair.AssetId)
            .ToDictionary(group => group.Key, group => group.Select(pair =>
                new AssetReference(pair.Layer.Id, pair.Layer.Name, pair.Layer.Kind,
                    pair.Layer.Visible, pair.Layer.Locked)).ToArray());
        return document.Assets.Select(pair =>
        {
            int? width = null;
            int? height = null;
            string? diagnostic = null;
            if (dimensionProbe is not null)
            {
                try
                {
                    var size = dimensionProbe(pair.Value);
                    width = size.Width;
                    height = size.Height;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    diagnostic = error.Message;
                }
            }
            return new AssetResource(pair.Key, pair.Value.Sha256, pair.Value.Length,
                width, height, diagnostic,
                references.TryGetValue(pair.Key, out var layerReferences)
                    ? Array.AsReadOnly(layerReferences) : Array.Empty<AssetReference>(),
                document.FixedBackgroundAssetId == pair.Key);
        }).OrderByDescending(resource => resource.IsUnreferenced)
          .ThenBy(resource => resource.AssetId).ToArray();
    }
}
