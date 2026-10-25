using Editor.Core;

namespace Editor.Application;

/// <summary>
/// Process-local clipboard for a foreground layer composition. The snapshot is detached from
/// its source session and contains only layers/assets; no source path or temporary file is kept.
/// </summary>
public sealed class LayerClipboard
{
    public static LayerClipboard Shared { get; } = new();

    private EditorDocument? _snapshot;

    public bool HasContent => _snapshot is not null;
    public int LayerCount => _snapshot?.Layers.Count ?? 0;

    public int Copy(EditorDocument source, IEnumerable<Guid> ids, bool includeGroups)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ids);
        var snapshot = SelectionProject.Create(source, ids, includeGroups);
        _snapshot = snapshot.Copy();
        return _snapshot.Layers.Count;
    }

    public IReadOnlyList<Guid> Paste(EditSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_snapshot is null)
            throw new InvalidOperationException("The layer clipboard is empty.");
        return session.ImportProjectLayers(_snapshot.Copy());
    }

    public void Clear() => _snapshot = null;
}
