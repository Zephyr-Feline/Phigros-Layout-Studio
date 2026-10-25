using Editor.Core;

namespace Editor.Application;

/// <summary>Normalizes derived bounds for capture/preflight only; saved layer geometry is never changed.</summary>
internal static class CanvasBoundaryPrecision
{
    // Match the existing template upper-edge validation tolerance, in normalized canvas units.
    // Rotated bounds aligned to an edge can retain a few floating-point ulps around 0 or 1.
    private const double NormalizedTolerance = 1e-12;

    public static RectD Normalize(RectD bounds, int canvasWidth, int canvasHeight)
    {
        var x = bounds.X / canvasWidth;
        var y = bounds.Y / canvasHeight;
        var width = bounds.Width / canvasWidth;
        var height = bounds.Height / canvasHeight;
        var right = x + width;
        var bottom = y + height;
        var leftEdge = Snap(x);
        var topEdge = Snap(y);
        var rightEdge = Snap(right);
        var bottomEdge = Snap(bottom);
        // Keep the previous capture arithmetic for interior slots. Adjust size only when an edge snapped.
        return new(leftEdge, topEdge,
            leftEdge == x && rightEdge == right ? width : rightEdge - leftEdge,
            topEdge == y && bottomEdge == bottom ? height : bottomEdge - topEdge);
    }

    private static double Snap(double value) => Math.Abs(value) <= NormalizedTolerance ? 0 :
        Math.Abs(value - 1) <= NormalizedTolerance ? 1 : value;
}
