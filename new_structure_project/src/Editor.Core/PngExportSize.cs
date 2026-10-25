namespace Editor.Core;

/// <summary>Transient raster size; never changes the document's logical canvas.</summary>
public readonly record struct PngExportSize(int Width, int Height, double Scale, double OffsetY)
{
    public const int MaxDimension = 16384;
    public const long MaxPixels = 80_000_000;

    public static PngExportSize Create(EditorDocument document, int? outputWidth = null,
        PngExportRegion? outputRegion = null)
    {
        document.Validate();
        outputRegion?.Validate(document);
        var logicalWidth = outputRegion?.Width ?? document.CanvasWidth;
        var logicalHeight = outputRegion?.Height ?? document.CanvasHeight;
        var width = outputWidth ?? (outputRegion is { }
            ? checked((int)Math.Round(logicalWidth, MidpointRounding.ToEven))
            : document.CanvasWidth);
        if (width is < 1 or > MaxDimension)
            throw new InvalidDataException($"PNG width must be between 1 and {MaxDimension} pixels.");
        var scale = width / logicalWidth;
        var idealHeight = logicalHeight * width / logicalWidth;
        var height = (int)Math.Round(idealHeight, MidpointRounding.ToEven);
        if (height is < 1 or > MaxDimension)
            throw new InvalidDataException($"PNG height must be between 1 and {MaxDimension} pixels; choose another width.");
        if ((long)width * height > MaxPixels)
            throw new InvalidDataException("PNG output exceeds the 80 million pixel limit.");
        // One uniform scale preserves badge/text/artwork proportions. Integer raster rounding
        // is split across the top/bottom edges (at most a quarter pixel per edge).
        return new(width, height, scale, (height - idealHeight) / 2);
    }
}
