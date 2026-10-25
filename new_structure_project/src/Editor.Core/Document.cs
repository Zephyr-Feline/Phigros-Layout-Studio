namespace Editor.Core;

public enum LayerKind { Image, Text, Badge, Shape }
public enum HorizontalAlignment { Left, Center, Right }
public enum VerticalAlignment { Top, Center, Bottom }
public enum ImageFitMode { Stretch, Cover, Contain }
public enum SongRole { None, Artwork, Title, Artist, Charter, Badge, Plate }
public enum BackgroundContributionMode { Automatic, Selected }

/// <summary>Persisted background choices. Generated pixels and render caches are transient.</summary>
public sealed record BackgroundBlendSettings(bool Enabled = false, int BlurRadius = 6,
    double Darkness = 105.0 / 255, bool IncludeFlatImages = false,
    BackgroundContributionMode ContributionMode = BackgroundContributionMode.Automatic,
    Guid[]? SelectedArtworkLayerIds = null, double Range = 1,
    double Transition = 0.5)
{
    /// <summary>List-valued selection needs content equality for history and cache decisions.</summary>
    public bool ContentEquals(BackgroundBlendSettings other) =>
        Enabled == other.Enabled && BlurRadius == other.BlurRadius &&
        Darkness.Equals(other.Darkness) && IncludeFlatImages == other.IncludeFlatImages &&
        ContributionMode == other.ContributionMode && Range.Equals(other.Range) &&
        Transition.Equals(other.Transition) &&
        (SelectedArtworkLayerIds ?? []).SequenceEqual(other.SelectedArtworkLayerIds ?? []);
}

public sealed class Layer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LayerKind Kind { get; set; }
    public string Name { get; set; } = "Layer";
    public RectD Bounds { get; set; } = new(0, 0, 100, 100);
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public Guid? GroupId { get; set; }
    /// <summary>Explicit persisted role inside a song group; None keeps custom groups unchanged.</summary>
    public SongRole SongRole { get; set; }
    public bool KeepAspect { get; set; }
    public bool Slanted { get; set; }
    /// <summary>Container opacity multiplied with text/image/color alpha by the shared painter.</summary>
    public double Opacity { get; set; } = 1;
    /// <summary>How the normalized source crop is fitted into the logical image box.</summary>
    /// <summary>Null means legacy behavior: Cover for slanted images, Stretch otherwise.</summary>
    public ImageFitMode? ImageFit { get; set; }
    /// <summary>Normalized source sampling rectangle; (0,0,1,1) is the complete image.</summary>
    public RectD ImageCrop { get; set; } = new(0, 0, 1, 1);
    public ImageFitMode EffectiveImageFit =>
        ImageFit ?? (Slanted ? ImageFitMode.Cover : ImageFitMode.Stretch);
    /// <summary>Clockwise rotation around the centre of the unrotated logical Bounds.</summary>
    public double RotationDegrees { get; set; }
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 36;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public double LineSpacing { get; set; } = 1;
    public HorizontalAlignment Alignment { get; set; }
    public VerticalAlignment VerticalAlignment { get; set; }
    public uint ColorArgb { get; set; } = 0xFFFFFFFF;
    public string Difficulty { get; set; } = "IN";
    public int Level { get; set; } = 15;
    public Guid? AssetId { get; set; }

    public Layer Copy() => (Layer)MemberwiseClone();

    public bool StateEquals(Layer other) => Id == other.Id && Kind == other.Kind &&
        Name == other.Name && Bounds == other.Bounds && Visible == other.Visible &&
        Locked == other.Locked && GroupId == other.GroupId && SongRole == other.SongRole &&
        KeepAspect == other.KeepAspect &&
        Slanted == other.Slanted && Opacity == other.Opacity &&
        EffectiveImageFit == other.EffectiveImageFit &&
        ImageCrop == other.ImageCrop && RotationDegrees == other.RotationDegrees &&
        Text == other.Text && FontFamily == other.FontFamily && FontSize == other.FontSize &&
        Bold == other.Bold && Italic == other.Italic && LineSpacing == other.LineSpacing &&
        Alignment == other.Alignment && VerticalAlignment == other.VerticalAlignment &&
        ColorArgb == other.ColorArgb &&
        Difficulty == other.Difficulty && Level == other.Level && AssetId == other.AssetId;
}

public sealed class EditorDocument
{
    public const double BadgeRatio = 128.0 / 108.0;
    public int CanvasWidth { get; set; } = 2740;
    public int CanvasHeight { get; set; } = 1080;
    public uint BackgroundArgb { get; set; } = 0xFF233B47;
    public BackgroundBlendSettings BackgroundBlend { get; set; } = new();
    public Guid? FixedBackgroundAssetId { get; set; }
    public List<Layer> Layers { get; set; } = [];
    public Dictionary<Guid, AssetBlob> Assets { get; set; } = [];
    public Dictionary<string, string> ExtensionData { get; set; } = new(StringComparer.Ordinal);

    public EditorDocument Copy() => new()
    {
        CanvasWidth = CanvasWidth,
        CanvasHeight = CanvasHeight,
        BackgroundArgb = BackgroundArgb,
        BackgroundBlend = BackgroundBlend,
        FixedBackgroundAssetId = FixedBackgroundAssetId,
        Layers = Layers.Select(layer => layer.Copy()).ToList(),
        Assets = Assets.ToDictionary(pair => pair.Key, pair => pair.Value),
        ExtensionData = new Dictionary<string, string>(ExtensionData, StringComparer.Ordinal)
    };

    public bool StateEquals(EditorDocument other) =>
        CanvasWidth == other.CanvasWidth && CanvasHeight == other.CanvasHeight &&
        BackgroundArgb == other.BackgroundArgb && FixedBackgroundAssetId == other.FixedBackgroundAssetId &&
        BackgroundBlend.ContentEquals(other.BackgroundBlend) &&
        Layers.Count == other.Layers.Count && Layers.Zip(other.Layers).All(pair => pair.First.StateEquals(pair.Second)) &&
        Assets.Count == other.Assets.Count && Assets.All(pair => other.Assets.TryGetValue(pair.Key, out var value) &&
            (ReferenceEquals(pair.Value, value) || pair.Value.ContentEquals(value))) &&
        ExtensionData.Count == other.ExtensionData.Count && ExtensionData.All(pair =>
            other.ExtensionData.TryGetValue(pair.Key, out var value) && pair.Value == value);

    public AssetBlob AddAsset(Guid id, byte[] bytes) => AddAsset(id, new AssetBlob(bytes));

    public AssetBlob AddAsset(Guid id, AssetBlob incoming)
    {
        if (id == Guid.Empty) throw new ArgumentException("Asset ID must be nonempty.", nameof(id));
        ArgumentNullException.ThrowIfNull(incoming);
        var shared = Assets.Values.FirstOrDefault(asset => asset.ContentEquals(incoming)) ?? incoming;
        Assets.Add(id, shared);
        return shared;
    }

    public void RemoveUnreferencedAssets()
    {
        var used = Layers.Where(layer => layer.Kind == LayerKind.Image && layer.AssetId.HasValue)
            .Select(layer => layer.AssetId!.Value).ToHashSet();
        if (FixedBackgroundAssetId.HasValue) used.Add(FixedBackgroundAssetId.Value);
        foreach (var id in Assets.Keys.Where(id => !used.Contains(id)).ToArray()) Assets.Remove(id);
    }

    public void Validate()
    {
        if (Layers is null || Assets is null || ExtensionData is null)
            throw new InvalidDataException("Document layers and assets are required.");
        if (BackgroundBlend is null || BackgroundBlend.BlurRadius is < 0 or > 20 ||
            !double.IsFinite(BackgroundBlend.Darkness) || BackgroundBlend.Darkness is < 0 or > 1 ||
            !Enum.IsDefined(BackgroundBlend.ContributionMode) ||
            !double.IsFinite(BackgroundBlend.Range) || BackgroundBlend.Range is < 0.25 or > 2 ||
            !double.IsFinite(BackgroundBlend.Transition) || BackgroundBlend.Transition is < 0 or > 1)
            throw new InvalidDataException("Background blend settings are outside their allowed range.");
        if (BackgroundBlend.SelectedArtworkLayerIds is { } selectedArtwork)
        {
            if (selectedArtwork.Length > 200 || selectedArtwork.Any(id => id == Guid.Empty) ||
                selectedArtwork.Distinct().Count() != selectedArtwork.Length)
                throw new InvalidDataException("Background artwork selection contains invalid or duplicate IDs.");
        }
        if (CanvasWidth is < 320 or > 7680 || CanvasHeight is < 180 or > 4320)
            throw new InvalidDataException("Canvas dimensions are outside the P1 range.");
        if (Layers.Count > 200 || Assets.Count > 200)
            throw new InvalidDataException("Too many layers or assets.");
        if (Assets.Any(asset => asset.Key == Guid.Empty || asset.Value is null || asset.Value.Length == 0))
            throw new InvalidDataException("Asset IDs and bytes must be nonempty.");
        if (ExtensionData.Count > 64 || ExtensionData.Any(pair =>
            string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 ||
            pair.Value is null || pair.Value.Length > 4096))
            throw new InvalidDataException("Invalid native extension data.");
        if (Layers.Any(layer => layer is null || !Enum.IsDefined(layer.Kind) || layer.GroupId == Guid.Empty ||
            layer.Name is null || layer.Text is null || layer.FontFamily is null || layer.Difficulty is null ||
            layer.Name.Length > 200 || layer.Text.Length > 4000 || layer.FontFamily.Length > 200 ||
            layer.Difficulty.Length > 16 || !Enum.IsDefined(layer.Alignment) ||
            !Enum.IsDefined(layer.VerticalAlignment) ||
            !double.IsFinite(layer.FontSize) || layer.FontSize is < 1 or > 1000 ||
            !double.IsFinite(layer.LineSpacing) || layer.LineSpacing is < 0.5 or > 4 ||
            !double.IsFinite(layer.Opacity) || layer.Opacity is < 0 or > 1 ||
            !double.IsFinite(layer.ImageCrop.X) || !double.IsFinite(layer.ImageCrop.Y) ||
            !double.IsFinite(layer.ImageCrop.Width) || !double.IsFinite(layer.ImageCrop.Height) ||
            layer.ImageCrop.X < 0 || layer.ImageCrop.Y < 0 ||
            layer.ImageCrop.Width <= 0 || layer.ImageCrop.Height <= 0 ||
            layer.ImageCrop.X + layer.ImageCrop.Width > 1 + 1e-9 ||
            layer.ImageCrop.Y + layer.ImageCrop.Height > 1 + 1e-9 ||
            (layer.ImageFit is ImageFitMode fit && !Enum.IsDefined(fit)) ||
            !Enum.IsDefined(layer.SongRole) ||
            !double.IsFinite(layer.RotationDegrees) || layer.RotationDegrees is < -180 or > 180 ||
            layer.Level < 1 || (layer.Kind != LayerKind.Image && layer.AssetId is not null) ||
            (layer.Slanted && layer.Kind is not (LayerKind.Image or LayerKind.Shape))))
            throw new InvalidDataException("Document contains an invalid layer type or null field.");
        if (Layers.Select(layer => layer.Id).Distinct().Count() != Layers.Count ||
            Layers.Any(layer => layer.Id == Guid.Empty))
            throw new InvalidDataException("Layer IDs must be stable, nonempty, and unique.");
        foreach (var group in Layers.Where(layer => layer.SongRole != SongRole.None)
            .GroupBy(layer => layer.GroupId))
        {
            if (group.Key is null)
                throw new InvalidDataException("Song roles require a group.");
            if (group.GroupBy(layer => layer.SongRole).Any(role => role.Count() > 1))
                throw new InvalidDataException("A song group cannot assign one role to multiple layers.");
            foreach (var layer in group)
            {
                var valid = layer.SongRole switch
                {
                    SongRole.Artwork => layer.Kind == LayerKind.Image,
                    SongRole.Title or SongRole.Artist or SongRole.Charter => layer.Kind == LayerKind.Text,
                    SongRole.Badge => layer.Kind == LayerKind.Badge,
                    SongRole.Plate => layer.Kind == LayerKind.Shape,
                    _ => true
                };
                if (!valid) throw new InvalidDataException("Song role does not match the layer kind.");
            }
        }
        foreach (var layer in Layers)
        {
            var b = layer.Bounds;
            if (!double.IsFinite(b.X) || !double.IsFinite(b.Y) || !double.IsFinite(b.Width) ||
                !double.IsFinite(b.Height) || b.Width < 1 || b.Height < 1 ||
                b.Width > 20000 || b.Height > 20000 || Math.Abs(b.X) > 20000 || Math.Abs(b.Y) > 20000)
                throw new InvalidDataException($"Invalid bounds for layer {layer.Id}.");
            if (layer.Kind == LayerKind.Badge &&
                (!layer.KeepAspect || b.Width < 8 || Math.Abs(b.Width / b.Height - BadgeRatio) > 0.000001 ||
                 layer.Level < 1 || layer.Difficulty is not ("EZ" or "HD" or "IN" or "AT")))
                throw new InvalidDataException($"Invalid badge {layer.Id}.");
            if (layer.Kind == LayerKind.Text &&
                (string.IsNullOrWhiteSpace(layer.FontFamily) || layer.FontSize is < 1 or > 1000 ||
                 !double.IsFinite(layer.FontSize) || layer.LineSpacing is < 0.5 or > 4 ||
                 !double.IsFinite(layer.LineSpacing)))
                throw new InvalidDataException($"Invalid text layer {layer.Id}.");
            if (layer.Kind == LayerKind.Image &&
                (layer.AssetId is null || !Assets.ContainsKey(layer.AssetId.Value)))
                throw new InvalidDataException($"Missing image asset for layer {layer.Id}.");
        }
        if (FixedBackgroundAssetId is Guid backgroundId && !Assets.ContainsKey(backgroundId))
            throw new InvalidDataException("Missing fixed background asset.");
    }

    public Layer Find(Guid id) => Layers.FirstOrDefault(layer => layer.Id == id)
        ?? throw new KeyNotFoundException($"Layer {id} not found.");

    public static Layer NewBadge(PointD position, double width = 128) => new()
    {
        Kind = LayerKind.Badge,
        Name = "Badge",
        Bounds = new RectD(position.X, position.Y, width, width / BadgeRatio),
        KeepAspect = true
    };
}
