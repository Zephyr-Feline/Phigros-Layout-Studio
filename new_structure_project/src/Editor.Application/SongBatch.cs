using Editor.Core;

namespace Editor.Application;

public sealed record SongArtwork(string Name, AssetBlob Image);

/// <summary>Existing six-piece song preset, arranged with the existing canvas-relative templates.</summary>
public static class SongBatch
{
    public static EditorDocument Create(int width, int height, IReadOnlyList<SongArtwork> artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        if (artwork.Count is not (3 or 5))
            throw new InvalidOperationException("Choose exactly 3 or 5 artwork images.");
        var (document, images) = CreateCards(width, height, artwork);
        LayoutTemplates.Apply(document, images, artwork.Count == 3 ? "three-columns" : "five-point");
        document.Validate();
        return document;
    }

    internal static (EditorDocument Document, Guid[] ImageIds) CreateCards(
        int width, int height, IReadOnlyList<SongArtwork> artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        if (artwork.Count == 0) throw new ArgumentException("At least one artwork is required.", nameof(artwork));
        var document = new EditorDocument { CanvasWidth = width, CanvasHeight = height };
        var images = new List<Guid>();
        foreach (var item in artwork)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Image);
            var title = string.IsNullOrWhiteSpace(item.Name) ? "New song" : item.Name.Trim();
            if (title.Length > 180) title = title[..180];
            var asset = document.Assets.FirstOrDefault(pair => pair.Value.ContentEquals(item.Image)).Key;
            if (asset == Guid.Empty) { asset = Guid.NewGuid(); document.AddAsset(asset, item.Image); }
            var group = Guid.NewGuid();
            var image = new Layer { Kind = LayerKind.Image, Name = title + " · artwork", AssetId = asset,
                GroupId = group, SongRole = SongRole.Artwork,
                Bounds = new(0, 0, 520, 250), Slanted = true, KeepAspect = true };
            var companions = LayerPresets.SongCompanions(image, group);
            foreach (var layer in companions) layer.Name = layer.Name.Replace("New song", title, StringComparison.Ordinal);
            companions[1].Text = title;
            document.Layers.Add(companions[0]); document.Layers.Add(image);
            document.Layers.AddRange(companions.Skip(1)); images.Add(image.Id);
        }
        return (document, images.ToArray());
    }
}
