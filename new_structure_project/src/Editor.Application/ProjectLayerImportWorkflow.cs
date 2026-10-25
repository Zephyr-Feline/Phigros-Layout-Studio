using Editor.Core;
using Editor.Formats;

namespace Editor.Application;

/// <summary>Read native project layers into the active composition without replacing its save target.</summary>
public sealed class ProjectLayerImportWorkflow(EditSession session, Action<EditorDocument> validateImages,
    ProtectedSourcePaths protectedPaths, Func<string?> currentProjectPath)
{
    public async Task<int> ImportAsync(Func<Task<string?>> choosePath)
    {
        ArgumentNullException.ThrowIfNull(choosePath);
        session.EnsureReadyForPersistence();
        var document = session.Document;
        var snapshot = document.Copy();
        var originalPath = currentProjectPath();
        var path = await choosePath();
        if (path is null) return 0;
        session.EnsureReadyForPersistence();
        if (!ReferenceEquals(session.Document, document) || !session.Document.StateEquals(snapshot) ||
            !string.Equals(originalPath, currentProjectPath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Project changed during import. Please import again.");
        var fullPath = Path.GetFullPath(path);
        if (originalPath is not null &&
            FilePathIdentity.AreSame(fullPath, originalPath))
            throw new InvalidOperationException("Choose another project; use Duplicate for layers in the current project.");
        var checkSource = FilePathIdentity.PrepareTargetCheck(fullPath);
        var protectSource = protectedPaths.PrepareAdd(fullPath);
        var source = NativePackageStore.Read(fullPath);
        validateImages(source);
        checkSource();
        var imported = session.ImportProjectLayers(source);
        protectSource();
        return imported.Count;
    }
}
