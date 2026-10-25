using Editor.Core;

namespace Editor.Application;

/// <summary>
/// Reads every grid artwork before one song-card batch commit. Existing content is retained;
/// the grid is appended through the same group/asset transaction as the three/five presets.
/// </summary>
public sealed class SongGridWorkflow(
    EditSession session,
    Func<string, Task<LoadedImage>> loadImage,
    Action<EditorDocument> validateImages,
    ProtectedSourcePaths protectedPaths,
    Func<string?> currentProjectPath)
{
    public async Task<int> ImportAsync(IReadOnlyList<string> chosenPaths, SongGridSettings settings,
        IProgress<BatchImageImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chosenPaths);
        ArgumentNullException.ThrowIfNull(settings);
        var pending = new PendingDocumentOperation(session, currentProjectPath);
        var before = session.Document.Copy();
        if (chosenPaths.Count == 0) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        settings.Validate(before.CanvasWidth, before.CanvasHeight);
        if (chosenPaths.Count != settings.Count)
            throw new InvalidOperationException(
                $"网格需要 {settings.Count} 张曲绘，当前选择了 {chosenPaths.Count} 张。");
        if (before.Layers.Count + settings.Count * 6 > 200)
            throw new InvalidDataException("The song grid would exceed the 200 layer limit.");
        var paths = chosenPaths.Select(Path.GetFullPath).ToArray();
        var checks = paths.Select(FilePathIdentity.PrepareTargetCheck).ToArray();
        var protections = paths.Select(protectedPaths.PrepareAdd).ToArray();
        var artwork = new List<SongArtwork>(paths.Length);
        var retained = before.Assets.Values.DistinctBy(asset => asset.Sha256)
            .ToDictionary(asset => asset.Sha256, StringComparer.Ordinal);
        var retainedBytes = AssetResourceBudget.UniqueBytes(before.Assets.Values);
        var allowedBytes = Math.Max(session.MaxDocumentAssetBytes, retainedBytes);
        for (var index = 0; index < paths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureUnchanged();
            checks[index]();
            var image = await loadImage(paths[index]);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureUnchanged();
            checks[index]();
            if (image.Image is null || image.Image.Length == 0 || image.Width <= 0 || image.Height <= 0)
                throw new InvalidDataException(
                    $"第 {index + 1} 张曲绘“{Path.GetFileName(paths[index])}”的尺寸或数据无效。");
            var payload = image.Image;
            if (retained.TryGetValue(payload.Sha256, out var existing))
            {
                if (!existing.ContentEquals(payload))
                    throw new InvalidDataException("Asset content-hash collision.");
                payload = existing;
            }
            else
            {
                // Reject growth before retaining this payload or loading the next artwork.
                retainedBytes = AssetResourceBudget.AddUniqueBytes(retainedBytes, payload.Length, allowedBytes);
                retained.Add(payload.Sha256, payload);
            }
            artwork.Add(new(Path.GetFileNameWithoutExtension(paths[index]), payload));
            progress?.Report(new(index + 1, paths.Length, Path.GetFileName(paths[index])));
        }
        var candidate = SongGrid.Create(before.CanvasWidth, before.CanvasHeight, artwork, settings);
        validateImages(candidate);
        EnsureUnchanged();
        foreach (var check in checks) check();
        session.ImportProjectLayers(candidate);
        foreach (var protect in protections) protect();
        return artwork.Count;

        void EnsureUnchanged() =>
            pending.EnsureUnchanged("Project changed while reading grid artwork. Please build the grid again.");
    }
}
