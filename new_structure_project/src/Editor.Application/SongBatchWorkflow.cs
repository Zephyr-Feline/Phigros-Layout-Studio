using Editor.Core;

namespace Editor.Application;

/// <summary>Read/validate the entire batch before committing one import to the current session.</summary>
public sealed class SongBatchWorkflow(EditSession session, Func<string, Task<AssetBlob>> loadImage,
    Action<EditorDocument> validateImages, ProtectedSourcePaths protectedPaths)
{
    public async Task<int> ImportAsync(Func<Task<IReadOnlyList<string>>> choosePaths)
    {
        ArgumentNullException.ThrowIfNull(choosePaths);
        session.EnsureReadyForPersistence();
        var original = session.Document;
        var before = original.Copy();
        var chosen = await choosePaths();
        if (chosen.Count == 0) return 0;
        EnsureUnchanged();
        if (chosen.Count is not (3 or 5))
            throw new InvalidOperationException("Choose exactly 3 or 5 artwork images.");
        if (before.Layers.Count + chosen.Count * 6 > 200)
            throw new InvalidDataException("The song batch would exceed the 200 layer limit.");
        var paths = chosen.Select(Path.GetFullPath).ToArray();
        var checkSources = paths.Select(FilePathIdentity.PrepareTargetCheck).ToArray();
        var protectSources = paths.Select(protectedPaths.PrepareAdd).ToArray();
        var artwork = new List<SongArtwork>();
        var retained = before.Assets.Values.DistinctBy(asset => asset.Sha256)
            .ToDictionary(asset => asset.Sha256, StringComparer.Ordinal);
        var retainedBytes = AssetResourceBudget.UniqueBytes(before.Assets.Values);
        var allowedBytes = Math.Max(session.MaxDocumentAssetBytes, retainedBytes);
        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            checkSources[i]();
            var image = await loadImage(path);
            EnsureUnchanged();
            checkSources[i]();
            if (image is null || image.Length == 0)
                throw new InvalidDataException("The image loader returned invalid image bytes.");
            if (retained.TryGetValue(image.Sha256, out var existing))
            {
                if (!existing.ContentEquals(image))
                    throw new InvalidDataException("Asset content-hash collision.");
                image = existing;
            }
            else
            {
                // Reject growth before retaining this payload or loading the next artwork.
                retainedBytes = AssetResourceBudget.AddUniqueBytes(retainedBytes, image.Length, allowedBytes);
                retained.Add(image.Sha256, image);
            }
            artwork.Add(new(Path.GetFileNameWithoutExtension(path), image));
        }
        var candidate = SongBatch.Create(before.CanvasWidth, before.CanvasHeight, artwork);
        validateImages(candidate);
        EnsureUnchanged();
        foreach (var checkSource in checkSources) checkSource();
        session.ImportProjectLayers(candidate);
        foreach (var protectSource in protectSources) protectSource();
        return artwork.Count;

        void EnsureUnchanged()
        {
            session.EnsureReadyForPersistence();
            if (!ReferenceEquals(original, session.Document) || !session.Document.StateEquals(before))
                throw new InvalidOperationException("Project changed while reading artwork. Please build the songs again.");
        }
    }
}
