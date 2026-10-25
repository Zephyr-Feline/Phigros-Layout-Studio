using Editor.Core;

namespace Editor.Application;

public sealed record BatchImageImportProgress(int Completed, int Total, string FileName);

/// <summary>
/// Reads every selected local image before one model commit. Cancellation, a bad file or a limit
/// failure leaves the document, selection, history and source registry untouched.
/// </summary>
public sealed class BatchImageImportWorkflow(
    EditSession session,
    Func<string, Task<LoadedImage>> loadImage,
    Action<EditorDocument> validateImages,
    ProtectedSourcePaths protectedPaths,
    Func<string?> currentProjectPath)
{
    public async Task<int> ImportAsync(Func<Task<IReadOnlyList<string>>> choosePaths,
        IProgress<BatchImageImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choosePaths);
        var pending = new PendingDocumentOperation(session, currentProjectPath);
        var before = session.Document.Copy();
        var chosen = await choosePaths();
        cancellationToken.ThrowIfCancellationRequested();
        if (chosen.Count == 0) return 0;
        EnsureUnchanged();
        if (before.Layers.Count + chosen.Count > 200)
            throw new InvalidDataException("Import would exceed the 200 layer limit.");

        var paths = chosen.Select(Path.GetFullPath).ToArray();
        var checkSources = paths.Select(FilePathIdentity.PrepareTargetCheck).ToArray();
        var protectSources = paths.Select(protectedPaths.PrepareAdd).ToArray();
        var loaded = new List<LoadedImage>(paths.Length);
        var retained = before.Assets.Values.DistinctBy(asset => asset.Sha256)
            .ToDictionary(asset => asset.Sha256, StringComparer.Ordinal);
        var retainedBytes = AssetResourceBudget.UniqueBytes(before.Assets.Values);
        var allowedBytes = Math.Max(session.MaxDocumentAssetBytes, retainedBytes);
        for (var index = 0; index < paths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkSources[index]();
            var image = await loadImage(paths[index]);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureUnchanged();
            checkSources[index]();
            if (image.Image is null || image.Image.Length == 0 || image.Width <= 0 || image.Height <= 0)
                throw new InvalidDataException("The image loader returned invalid image dimensions or bytes.");
            if (retained.TryGetValue(image.Image.Sha256, out var existing))
            {
                if (!existing.ContentEquals(image.Image))
                    throw new InvalidDataException("Asset content-hash collision.");
                image = image with { Image = existing };
            }
            else
            {
                // Admit each unique payload immediately, before retaining or reading later files.
                retainedBytes = AssetResourceBudget.AddUniqueBytes(retainedBytes, image.Image.Length, allowedBytes);
                retained.Add(image.Image.Sha256, image.Image);
            }
            loaded.Add(image);
            progress?.Report(new(index + 1, paths.Length, Path.GetFileName(paths[index])));
        }

        // Asset IDs are per layer, but identical payloads share one immutable AssetBlob.
        var addedPayloads = retained.Count - before.Assets.Values.Select(asset => asset.Sha256).Distinct().Count();
        if (before.Assets.Count + addedPayloads > 200)
            throw new InvalidDataException("Too many layers or assets.");
        validateImages(before);
        EnsureUnchanged();
        foreach (var checkSource in checkSources) checkSource();

        var entries = new (Layer Layer, AssetBlob Image)[loaded.Count];
        for (var index = 0; index < loaded.Count; index++)
        {
            var image = loaded[index];
            var scale = Math.Min(1, 600.0 / Math.Max(image.Width, image.Height));
            // Predictable stagger in native canvas units; no song layout is implied.
            var offset = index * 12.0;
            var layer = new Layer
            {
                Kind = LayerKind.Image,
                Name = ImageLayerNames.FromPath(paths[index]),
                AssetId = Guid.NewGuid(),
                Bounds = new RectD(90 + offset, 90 + offset,
                    Math.Max(1, image.Width * scale), Math.Max(1, image.Height * scale))
            };
            entries[index] = (layer, image.Image);
        }
        session.AddImagePayloadLayers(entries);
        foreach (var protectSource in protectSources) protectSource();
        return entries.Length;

        void EnsureUnchanged() =>
            pending.EnsureUnchanged("Project changed while reading images. Please import again.");
    }
}
