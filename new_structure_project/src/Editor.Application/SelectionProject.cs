using Editor.Core;

namespace Editor.Application;

/// <summary>A reusable foreground composition, using the native canvas's original logical units.</summary>
public static class SelectionProject
{
    public static EditorDocument Create(EditorDocument source, IEnumerable<Guid> selectedIds, bool includeGroups)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selectedIds);
        source.Validate();
        var ids = LayoutSelection.Units(source, selectedIds, includeGroups)
            .SelectMany(unit => unit).Select(layer => layer.Id).ToHashSet();
        if (ids.Count == 0) throw new InvalidOperationException("Select layers or a song group to save.");
        var result = new EditorDocument
        {
            CanvasWidth = source.CanvasWidth,
            CanvasHeight = source.CanvasHeight,
            BackgroundArgb = source.BackgroundArgb,
            Layers = source.Layers.Where(layer => ids.Contains(layer.Id)).Select(layer => layer.Copy()).ToList()
        };
        foreach (var id in result.Layers.Select(layer => layer.AssetId).OfType<Guid>().Distinct())
            result.AddAsset(id, source.Assets[id]);
        result.Validate();
        return result;
    }
}
