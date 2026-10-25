using Editor.Core;
using Editor.Formats;

namespace Editor.Application;

/// <summary>Save selected content as an independent native package without marking the editor saved.</summary>
public sealed class SelectionProjectWorkflow(EditSession session, Action<EditorDocument> validateImages,
    ProtectedSourcePaths protectedPaths, Func<string?> currentProjectPath, string applicationVersion)
{
    public async Task<bool> SaveAsync(bool includeGroups, Func<Task<string?>> choosePath)
    {
        ArgumentNullException.ThrowIfNull(choosePath);
        session.EnsureReadyForPersistence();
        var document = session.Document;
        var snapshot = document.Copy();
        var selection = session.SelectedLayerIds.ToArray();
        var originalPath = currentProjectPath();
        var candidate = SelectionProject.Create(snapshot, selection, includeGroups);
        validateImages(candidate);
        var path = await choosePath();
        if (path is null) return false;
        session.EnsureReadyForPersistence();
        if (!ReferenceEquals(document, session.Document) || !session.Document.StateEquals(snapshot) ||
            !selection.SequenceEqual(session.SelectedLayerIds) ||
            !string.Equals(originalPath, currentProjectPath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Project or selection changed during save. Please save the selection again.");
        var destination = Path.GetFullPath(NativeProjectFile.EnsureExtension(path));
        protectedPaths.EnsureNotProtected(destination);
        if (originalPath is not null &&
            FilePathIdentity.AreSame(destination, originalPath))
            throw new InvalidOperationException("Choose another path; the selection must not replace the current project.");
        NativePackageStore.Save(destination, candidate, applicationVersion);
        return true;
    }
}
