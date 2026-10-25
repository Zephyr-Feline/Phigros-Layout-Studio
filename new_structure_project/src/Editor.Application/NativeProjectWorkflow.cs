using Editor.Core;
using Editor.Formats;

namespace Editor.Application;

/// <summary>Coordinates candidate validation, active session replacement and native savepoints.</summary>
public sealed class NativeProjectWorkflow
{
    private readonly Action<EditorDocument> _validateImages;
    private readonly string _applicationVersion;
    private string? _currentTarget;

    public EditSession Session { get; }
    public string? CurrentPath { get; private set; }
    public bool IsDirty => Session.IsDirty;

    public NativeProjectWorkflow(EditSession session, Action<EditorDocument> validateImages,
        string applicationVersion)
    {
        Session = session;
        _validateImages = validateImages ?? throw new ArgumentNullException(nameof(validateImages));
        _applicationVersion = applicationVersion;
    }

    public void New()
    {
        Session.ReplaceDocument(new EditorDocument());
        CurrentPath = null;
        _currentTarget = null;
    }

    public void Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var target = FilePathIdentity.Resolve(fullPath);
        var candidate = NativePackageStore.Read(fullPath);
        _validateImages(candidate);
        FilePathIdentity.EnsureTarget(fullPath, target);
        Session.ReplaceDocument(candidate);
        CurrentPath = fullPath;
        _currentTarget = target;
    }

    public void LoadOtherFormat(EditorDocument document)
    {
        Session.ReplaceDocument(document);
        CurrentPath = null;
        _currentTarget = null;
    }

    /// <summary>
    /// Adopts a document produced by a compatibility conversion: no native path yet, and the session
    /// starts unsaved because the user has not written a <c>.ppgproj</c> of their own.
    /// </summary>
    public void ImportConverted(EditorDocument document)
    {
        Session.ReplaceDocument(document, saved: false);
        CurrentPath = null;
        _currentTarget = null;
    }

    public void MarkOtherFormatSaved()
    {
        CurrentPath = null;
        _currentTarget = null;
        Session.MarkSaved();
    }

    public void Save()
    {
        if (CurrentPath is null) throw new InvalidOperationException("Choose a native project path first.");
        SaveTo(CurrentPath, _currentTarget!);
    }

    public void SaveAs(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var target = FilePathIdentity.Resolve(fullPath);
        SaveTo(fullPath, target);
        CurrentPath = fullPath;
        _currentTarget = target;
    }

    /// <summary>Only save the project for which the asynchronous path request was opened.</summary>
    public async Task<bool> SaveAsAsync(Func<Task<string?>> choosePath, ProtectedSourcePaths protectedPaths)
    {
        ArgumentNullException.ThrowIfNull(choosePath);
        ArgumentNullException.ThrowIfNull(protectedPaths);
        var pending = new PendingDocumentOperation(Session, () => CurrentPath);
        var chosenPath = await choosePath();
        if (chosenPath is null) return false;
        pending.EnsureUnchanged("Project changed while choosing a save path. Please save again.");
        var destination = Path.GetFullPath(NativeProjectFile.EnsureExtension(chosenPath));
        protectedPaths.EnsureNotProtected(destination);
        SaveAs(destination);
        return true;
    }

    public async Task<bool> SaveTemporaryAsync(Func<Task<string?>> choosePath, ProtectedSourcePaths protectedPaths)
    {
        ArgumentNullException.ThrowIfNull(choosePath);
        ArgumentNullException.ThrowIfNull(protectedPaths);
        var pending = new PendingDocumentOperation(Session, () => CurrentPath);
        var chosen = await choosePath();
        if (chosen is null) return false;
        pending.EnsureUnchanged("Project changed while choosing a temporary save path. Please save again.");
        var path = Path.GetFullPath(chosen);
        protectedPaths.EnsureNotProtected(path);
        if (CurrentPath is not null && FilePathIdentity.AreSame(path, CurrentPath))
            throw new InvalidOperationException("A temporary JSON file cannot replace the current native project.");
        var checkDestination = FilePathIdentity.PrepareTargetCheck(path);
        _validateImages(Session.Document);
        checkDestination();
        protectedPaths.EnsureNotProtected(path);
        if (CurrentPath is not null && FilePathIdentity.AreSame(path, CurrentPath))
            throw new InvalidOperationException("A temporary JSON file cannot replace the current native project.");
        NativeDocumentStore.Save(path, Session.Document);
        MarkOtherFormatSaved();
        return true;
    }

    private void SaveTo(string path, string target)
    {
        Session.EnsureReadyForPersistence();
        _validateImages(Session.Document);
        FilePathIdentity.EnsureTarget(path, target);
        NativePackageStore.Save(path, Session.Document, _applicationVersion);
        Session.MarkSaved();
    }
}
