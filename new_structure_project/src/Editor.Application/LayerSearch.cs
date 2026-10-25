using Editor.Core;

namespace Editor.Application;

/// <summary>Session-only lookup over layer names and text. Never changes the document.</summary>
public static class LayerSearch
{
    /// <summary>Returns unique layer IDs in the visible layer-list order (topmost first).</summary>
    public static IReadOnlyList<Guid> Matches(EditorDocument document, string? query)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(query)) return [];
        var term = query.Trim();
        return document.Layers.AsEnumerable().Reverse()
            .Where(layer => Contains(layer.Name, term) || Contains(layer.Text, term))
            .Select(layer => layer.Id)
            .ToArray();
    }

    private static bool Contains(string value, string term) =>
        value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
