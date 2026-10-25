using Editor.Core;

namespace Editor.Application;

/// <summary>Transient context for an asynchronous request; assets in the snapshot remain shared.</summary>
public sealed class PendingDocumentOperation
{
    private readonly EditSession _session;
    private readonly EditorDocument _document;
    private readonly Func<string?>? _currentPath;
    private readonly string? _originalPath;
    private readonly Guid[]? _selection;
    public EditorDocument Snapshot { get; }

    public PendingDocumentOperation(EditSession session, Func<string?>? currentPath = null,
        bool includeSelection = false)
    {
        _session = session;
        session.EnsureReadyForPersistence();
        _document = session.Document;
        Snapshot = _document.Copy();
        _currentPath = currentPath;
        _originalPath = currentPath?.Invoke();
        _selection = includeSelection ? session.SelectedLayerIds.ToArray() : null;
    }

    public void EnsureUnchanged(string message, bool checkPath = true)
    {
        _session.EnsureReadyForPersistence();
        if (!ReferenceEquals(_document, _session.Document) || !_session.Document.StateEquals(Snapshot) ||
            (checkPath && _currentPath is not null &&
                !string.Equals(_originalPath, _currentPath(), StringComparison.OrdinalIgnoreCase)) ||
            (_selection is not null && !_selection.SequenceEqual(_session.SelectedLayerIds)))
            throw new InvalidOperationException(message);
    }
}
