namespace Editor.Core;

/// <summary>
/// A transient logical rectangle rendered into a PNG. It is never stored in a project or a
/// reusable export preset and does not change the document's canvas dimensions.
/// </summary>
public readonly record struct PngExportRegion(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>
    /// Build the pixel-stable crop rectangle for selected layers. Rotation is represented by
    /// each layer's visual bounds; hidden and locked members still contribute geometry, just as
    /// they remain part of a selected song group. The result is clamped to the source canvas.
    /// </summary>
    public static PngExportRegion FromLayers(EditorDocument document, IEnumerable<Layer> layers)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(layers);
        document.Validate();
        var selected = layers.ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException("Select at least one layer to calculate a crop region.");
        var union = MoveSnapping.BoundsOf(selected.Select(LayerGeometry.VisualBounds));
        var left = Math.Clamp(Math.Floor(Snap(union.X)), 0, document.CanvasWidth);
        var top = Math.Clamp(Math.Floor(Snap(union.Y)), 0, document.CanvasHeight);
        var right = Math.Clamp(Math.Ceiling(Snap(union.X + union.Width)), 0, document.CanvasWidth);
        var bottom = Math.Clamp(Math.Ceiling(Snap(union.Y + union.Height)), 0, document.CanvasHeight);
        if (right <= left || bottom <= top)
            throw new InvalidOperationException(
                "The selected content is entirely outside the canvas and cannot be cropped.");
        return new(left, top, right - left, bottom - top);
    }

    public void Validate(EditorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!double.IsFinite(X) || !double.IsFinite(Y) ||
            !double.IsFinite(Width) || !double.IsFinite(Height) ||
            X < 0 || Y < 0 || Width <= 0 || Height <= 0 ||
            X + Width > document.CanvasWidth + 1e-9 ||
            Y + Height > document.CanvasHeight + 1e-9)
            throw new InvalidDataException(
                "PNG export region must be a positive rectangle inside the canvas.");
    }

    // Rotated bounds aligned to an integer edge can retain a few floating-point ulps.
    private static double Snap(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.ToEven);
        return Math.Abs(value - rounded) <= 1e-9 ? rounded : value;
    }
}
