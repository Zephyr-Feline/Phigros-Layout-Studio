using Editor.Core;

namespace Editor.Rendering;

/// <summary>One preview's derived pixels. Nothing here enters the document or its history.</summary>
public sealed class BackgroundRenderCache
{
    private readonly record struct Artwork(RectD Bounds, string Hash, double Opacity,
        ImageFitMode Fit, RectD Crop);
    private (int Width, int Height, uint Color, BackgroundBlendSettings Settings)? _settings;
    private Artwork[] _artwork = [];
    private AssetImageDecoder? _decoder;
    private AssetBlob? _image;
    private readonly Dictionary<(string Hash, ImageFitMode Fit, RectD Crop), byte[]> _samples = new();

    public AssetBlob? Get(EditorDocument document, AssetImageDecoder decoder)
    {
        document.Validate();
        if (!document.BackgroundBlend.Enabled || document.FixedBackgroundAssetId is not null)
        {
            Clear();
            return null;
        }
        var layers = BackgroundComposer.ContributingLayers(document);
        if (layers.Length == 0)
        {
            Clear();
            return null;
        }
        var settings = (document.CanvasWidth, document.CanvasHeight, document.BackgroundArgb, document.BackgroundBlend);
        var artwork = layers.Select(layer => new Artwork(LayerGeometry.VisualBounds(layer),
            document.Assets[layer.AssetId!.Value].Sha256, layer.Opacity,
            layer.EffectiveImageFit, layer.ImageCrop)).ToArray();
        if (ReferenceEquals(decoder, _decoder) && _settings == settings && artwork.SequenceEqual(_artwork))
            return _image;
        if (!ReferenceEquals(decoder, _decoder)) _samples.Clear();
        var used = artwork.Select(art => (art.Hash, art.Fit, art.Crop)).ToHashSet();
        foreach (var key in _samples.Keys.Where(key => !used.Contains(key)).ToArray()) _samples.Remove(key);
        byte[] Sample(AssetBlob asset, ImageFitMode fit, RectD crop)
        {
            var key = (asset.Sha256, fit, crop);
            if (!_samples.TryGetValue(key, out var pixels))
                _samples[key] = pixels = BackgroundComposer.Sample(asset, decoder, fit, crop);
            return pixels;
        }
        var image = new AssetBlob(BackgroundComposer.Generate(document, decoder, Sample));
        _settings = settings;
        _artwork = artwork;
        _decoder = decoder;
        return _image = image;
    }

    public void Clear()
    {
        _settings = null;
        _artwork = [];
        _decoder = null;
        _image = null;
        _samples.Clear();
    }
}
