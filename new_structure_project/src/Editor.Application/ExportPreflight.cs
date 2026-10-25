using Editor.Core;

namespace Editor.Application;

public enum ExportWarningKind
{
    HiddenLayer,
    OutsideCanvas,
    ClippedByCanvas,
    MissingLocalFont,
    LowResolutionArtwork
}

public enum ExportQualityPolicy
{
    Notify,
    BlockLowResolution
}

public sealed record ExportWarning(ExportWarningKind Kind, Guid LayerId, string LayerName, string Message);

/// <summary>Advisories in native canvas units. Never changes geometry, assets or history.</summary>
public static class ExportPreflight
{
    public static IReadOnlyList<ExportWarning> Inspect(EditorDocument document,
        Func<string, bool> isLocalFontMissing,
        int? outputWidth = null,
        Func<AssetBlob, (int Width, int Height)>? readImageDimensions = null,
        bool transparentBackground = false,
        PngExportRegion? outputRegion = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(isLocalFontMissing);
        document.Validate();
        PngExportSize? output = readImageDimensions is null ? null :
            PngExportSize.Create(document, outputWidth, outputRegion);
        var viewportLeft = outputRegion?.X ?? 0;
        var viewportTop = outputRegion?.Y ?? 0;
        var viewportWidth = outputRegion?.Width ?? document.CanvasWidth;
        var viewportHeight = outputRegion?.Height ?? document.CanvasHeight;
        var normalizedLeft = viewportLeft / document.CanvasWidth;
        var normalizedTop = viewportTop / document.CanvasHeight;
        var normalizedRight = (viewportLeft + viewportWidth) / document.CanvasWidth;
        var normalizedBottom = (viewportTop + viewportHeight) / document.CanvasHeight;
        var dimensions = readImageDimensions is null ? null :
            new Dictionary<Guid, (int Width, int Height)>();
        var warnings = new List<ExportWarning>();
        foreach (var layer in document.Layers)
        {
            void Add(ExportWarningKind kind, string message) =>
                warnings.Add(new(kind, layer.Id, layer.Name, message));
            if (!layer.Visible)
            {
                Add(ExportWarningKind.HiddenLayer, "Hidden; omitted from PNG.");
                continue;
            }
            var b = CanvasBoundaryPrecision.Normalize(LayerGeometry.VisualBounds(layer),
                document.CanvasWidth, document.CanvasHeight);
            if (b.X + b.Width <= normalizedLeft || b.Y + b.Height <= normalizedTop ||
                b.X >= normalizedRight || b.Y >= normalizedBottom)
                Add(ExportWarningKind.OutsideCanvas, "Entirely outside the canvas; foreground omitted from PNG.");
            else if (b.X < normalizedLeft || b.Y < normalizedTop ||
                b.X + b.Width > normalizedRight ||
                b.Y + b.Height > normalizedBottom)
                Add(ExportWarningKind.ClippedByCanvas, "Partly outside the canvas; PNG clips it at the edge.");
            if (layer.Kind == LayerKind.Text && layer.Text.Length > 0 && isLocalFontMissing(layer.FontFamily))
                Add(ExportWarningKind.MissingLocalFont, "Local font unavailable; PNG uses the preview fallback font.");
            if (output is { } size && layer.Kind == LayerKind.Image &&
                layer.AssetId is Guid assetId &&
                TryReadDimensions(document, assetId, readImageDimensions!, dimensions!,
                    out var sourceWidth, out var sourceHeight))
            {
                var bounds = layer.Bounds;
                var (sampledWidth, sampledHeight) = RasterCropSize(sourceWidth, sourceHeight,
                    layer.ImageCrop);
                var displayedWidth = bounds.Width * size.Scale;
                var displayedHeight = bounds.Height * size.Scale;
                if (layer.EffectiveImageFit == ImageFitMode.Contain)
                {
                    var fit = Math.Min(displayedWidth / sampledWidth, displayedHeight / sampledHeight);
                    displayedWidth = sampledWidth * fit;
                    displayedHeight = sampledHeight * fit;
                }
                // Stretch checks each axis. Cover's maximum fit scale exceeds one whenever
                // either target axis exceeds the sampled source; rotation changes neither.
                if (sampledWidth + 0.5 < displayedWidth ||
                    sampledHeight + 0.5 < displayedHeight)
                    Add(ExportWarningKind.LowResolutionArtwork,
                        UpscaleMessage(sampledWidth, sampledHeight,
                            displayedWidth, displayedHeight));
            }
        }
        if (!transparentBackground && output is { } outputSize &&
            readImageDimensions is not null &&
            document.FixedBackgroundAssetId is Guid backgroundId &&
            TryReadDimensions(document, backgroundId, readImageDimensions, dimensions!,
                out var backgroundWidth, out var backgroundHeight))
        {
            // Cropping samples the background already fitted to the whole canvas. Compare
            // against that canvas at the uniform render scale, not the final cropped bitmap.
            var displayedWidth = document.CanvasWidth * outputSize.Scale;
            var displayedHeight = document.CanvasHeight * outputSize.Scale;
            if (backgroundWidth + 0.5 < displayedWidth ||
                backgroundHeight + 0.5 < displayedHeight)
                warnings.Add(new(ExportWarningKind.LowResolutionArtwork, Guid.Empty,
                    "Fixed background", UpscaleMessage(backgroundWidth, backgroundHeight,
                        displayedWidth, displayedHeight)));
        }
        return warnings.AsReadOnly();
    }

    private static (int Width, int Height) RasterCropSize(int width, int height, RectD crop)
    {
        // Keep paired with ScenePainter.SourceRect: round the sampling rectangle outwards,
        // clamp it to the encoded image, and retain at least one source pixel on each axis.
        var left = Math.Clamp((int)Math.Floor(crop.X * width), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(crop.Y * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((crop.X + crop.Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((crop.Y + crop.Height) * height), top + 1, height);
        return (right - left, bottom - top);
    }

    private static bool TryReadDimensions(EditorDocument document, Guid assetId,
        Func<AssetBlob, (int Width, int Height)> read,
        Dictionary<Guid, (int Width, int Height)> cache,
        out int width, out int height)
    {
        width = 0;
        height = 0;
        if (cache.TryGetValue(assetId, out var cached))
        {
            width = cached.Width;
            height = cached.Height;
            return true;
        }
        if (!document.Assets.TryGetValue(assetId, out var asset)) return false;
        try
        {
            var size = read(asset);
            if (size.Width <= 0 || size.Height <= 0) return false;
            width = size.Width;
            height = size.Height;
            cache[assetId] = size;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static string UpscaleMessage(int sourceWidth, int sourceHeight,
        double displayedWidth, double displayedHeight) =>
        $"Artwork is upscaled at this output size; source {sourceWidth}×{sourceHeight} px, " +
        $"displayed {Math.Max(1, (int)Math.Round(displayedWidth))}×" +
        $"{Math.Max(1, (int)Math.Round(displayedHeight))} px.";
}
