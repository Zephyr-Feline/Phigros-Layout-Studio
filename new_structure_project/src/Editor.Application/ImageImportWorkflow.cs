using Editor.Core;

namespace Editor.Application;

public enum ImageImportAction { Add, Replace, Background }
/// <summary>Image bytes already validated and decoded at the platform boundary.</summary>
public sealed record LoadedImage(AssetBlob Image, int Width, int Height);

/// <summary>Complete a validated local image request against its original document only.</summary>
public sealed class ImageImportWorkflow(EditSession session, Func<string, Task<LoadedImage>> loadImage,
    ProtectedSourcePaths protectedPaths, Func<string?> currentProjectPath)
{
    public async Task<bool> ImportAsync(ImageImportAction action, Func<Task<string?>> choosePath)
    {
        ArgumentNullException.ThrowIfNull(choosePath);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        var pending = new PendingDocumentOperation(session, currentProjectPath,
            includeSelection: action == ImageImportAction.Replace);
        var id = session.SelectedLayerId;
        if (action == ImageImportAction.Replace &&
            (id is null || session.Document.Find(id.Value).Kind != LayerKind.Image))
            throw new InvalidOperationException("Select an image layer to replace.");
        var chosen = await choosePath();
        if (chosen is null) return false;
        Check();
        var path = Path.GetFullPath(chosen);
        var checkSource = FilePathIdentity.PrepareTargetCheck(path);
        var protectSource = protectedPaths.PrepareAdd(path);
        var loaded = await loadImage(path);
        Check();
        if (loaded.Image is null || loaded.Image.Length == 0 || loaded.Width <= 0 || loaded.Height <= 0)
            throw new InvalidDataException("The image loader returned invalid image dimensions or bytes.");
        checkSource();
        switch (action)
        {
            case ImageImportAction.Add:
                // Preserve the existing desktop import placement in native canvas units.
                var scale = Math.Min(1, 600.0 / Math.Max(loaded.Width, loaded.Height));
                var name = ImageLayerNames.FromPath(path);
                session.AddImageLayer(new Layer { Kind = LayerKind.Image, Name = name,
                    AssetId = Guid.NewGuid(), Bounds = new RectD(90, 90,
                        Math.Max(1, loaded.Width * scale), Math.Max(1, loaded.Height * scale)) }, loaded.Image);
                break;
            case ImageImportAction.Replace:
                session.ReplaceImageLayer(id!.Value, loaded.Image);
                break;
            case ImageImportAction.Background:
                session.SetFixedBackground(loaded.Image);
                break;
        }
        protectSource();
        return true;

        void Check() => pending.EnsureUnchanged("Project or image selection changed while reading an image. Please import again.");
    }
}
