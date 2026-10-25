using Editor.Core;

namespace Editor.Application;

/// <summary>One unit per selected group or layer, in first-selected order.</summary>
internal static class LayoutSelection
{
    public static List<Layer[]> Units(EditorDocument document, IEnumerable<Guid> selectedIds,
        bool includeGroups = true)
    {
        var units = new List<Layer[]>();
        var seen = new HashSet<(bool Grouped, Guid Id)>();
        foreach (var id in selectedIds.Distinct())
        {
            var selected = document.Find(id);
            var grouped = includeGroups && selected.GroupId is not null;
            var key = (grouped, grouped ? selected.GroupId!.Value : selected.Id);
            if (!seen.Add(key)) continue;
            units.Add(grouped
                ? document.Layers.Where(layer => layer.GroupId == selected.GroupId).ToArray()
                : [selected]);
        }
        return units;
    }
}
