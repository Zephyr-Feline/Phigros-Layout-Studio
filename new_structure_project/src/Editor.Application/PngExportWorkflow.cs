using Editor.Core;

namespace Editor.Application;

/// <summary>Validates one committed document and writes a complete PNG without changing edit state.</summary>
public sealed class PngExportWorkflow
{
    public const long MaxOutputPixels = PngExportSize.MaxPixels;

    private readonly EditSession _session;
    private readonly Action<EditorDocument> _validateImages;
    private readonly Func<EditorDocument, bool, int?, byte[]> _render;
    private readonly Func<EditorDocument, bool, int?, PngExportRegion?, byte[]>? _regionRender;
    private readonly Action<string, byte[]> _atomicWrite;
    private readonly ProtectedSourcePaths _sources;
    private readonly Func<string?> _currentProjectPath;
    private readonly Func<AssetBlob, (int Width, int Height)>? _readImageDimensions;

    public PngExportWorkflow(EditSession session, Action<EditorDocument> validateImages,
        Func<EditorDocument, bool, int?, byte[]> render, Action<string, byte[]> atomicWrite,
        ProtectedSourcePaths sources, Func<string?> currentProjectPath,
        Func<AssetBlob, (int Width, int Height)>? readImageDimensions = null,
        Func<EditorDocument, bool, int?, PngExportRegion?, byte[]>? regionRender = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _validateImages = validateImages ?? throw new ArgumentNullException(nameof(validateImages));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _regionRender = regionRender;
        _atomicWrite = atomicWrite ?? throw new ArgumentNullException(nameof(atomicWrite));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _currentProjectPath = currentProjectPath ?? throw new ArgumentNullException(nameof(currentProjectPath));
        _readImageDimensions = readImageDimensions;
    }

    public void Export(string path, bool transparentBackground = false, int? outputWidth = null,
        PngExportRegion? outputRegion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _session.EnsureReadyForPersistence();
        var document = _session.Document;
        var destination = Path.GetFullPath(path);
        var checkDestination = FilePathIdentity.PrepareTargetCheck(destination);
        _ = PngExportSize.Create(document, outputWidth, outputRegion);
        _validateImages(document);
        checkDestination();
        _sources.EnsureNotProtected(destination);
        var projectPath = _currentProjectPath();
        if (projectPath is not null &&
            FilePathIdentity.AreSame(destination, projectPath))
            throw new InvalidOperationException("Choose another output path; this file is the current project.");

        var bytes = outputRegion is null
            ? _render(document, transparentBackground, outputWidth)
            : _regionRender?.Invoke(document, transparentBackground, outputWidth, outputRegion)
                ?? throw new InvalidOperationException(
                    "This export workflow was not configured for a cropped PNG region.");
        if (bytes is null || bytes.Length == 0)
            throw new InvalidDataException("PNG renderer produced no output.");
        checkDestination();
        _sources.EnsureNotProtected(destination);
        projectPath = _currentProjectPath();
        if (projectPath is not null && FilePathIdentity.AreSame(destination, projectPath))
            throw new InvalidOperationException("Choose another output path; this file is the current project.");
        _atomicWrite(destination, bytes);
    }

    /// <summary>Review before choosing a destination; cancellation never renders or writes.</summary>
    public async Task<bool> ExportWithReviewAsync(bool transparentBackground,
        Func<string, bool> isLocalFontMissing,
        Func<IReadOnlyList<ExportWarning>, Task<bool>> review,
        Func<Task<string?>> choosePath, Func<Task<int?>>? chooseWidth = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(choosePath);
        var pending = new PendingDocumentOperation(_session, _currentProjectPath);
        var snapshot = pending.Snapshot;
        var warnings = ExportPreflight.Inspect(snapshot, isLocalFontMissing,
            readImageDimensions: _readImageDimensions,
            transparentBackground: transparentBackground);
        _validateImages(snapshot);
        if (warnings.Count > 0 && !await review(warnings)) return false;
        EnsureUnchanged();
        int? outputWidth = null;
        if (chooseWidth is not null)
        {
            outputWidth = await chooseWidth();
            if (outputWidth is null) return false;
            EnsureUnchanged();
        }
        _ = PngExportSize.Create(snapshot, outputWidth);
        var path = await choosePath();
        if (path is null) return false;
        EnsureUnchanged();
        Export(path, transparentBackground, outputWidth);
        return true;

        void EnsureUnchanged() => pending.EnsureUnchanged("Document changed during export review. Please export again.");
    }
}
