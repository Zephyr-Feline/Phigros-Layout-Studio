namespace Editor.Application;

/// <summary>Pure edge-scroll policy for layer-row drag and drop.</summary>
public static class LayerListAutoScroll
{
    public const double EdgeSize = 24;
    public const double MaximumStep = 32;

    public static double NextOffset(double currentOffset, double maximumOffset,
        double pointerY, double viewportHeight)
    {
        if (!double.IsFinite(currentOffset) || !double.IsFinite(maximumOffset) ||
            !double.IsFinite(pointerY) || !double.IsFinite(viewportHeight) ||
            viewportHeight <= 0)
            return Math.Max(0, double.IsFinite(currentOffset) ? currentOffset : 0);

        var maximum = Math.Max(0, maximumOffset);
        var offset = Math.Clamp(currentOffset, 0, maximum);
        if (offset >= maximum && pointerY >= viewportHeight) return offset;

        var edge = Math.Min(EdgeSize, viewportHeight / 3);
        var y = Math.Clamp(pointerY, 0, viewportHeight);
        var delta = 0d;
        if (y < edge)
            delta = -MaximumStep * (1 - y / edge);
        else if (y > viewportHeight - edge)
            delta = MaximumStep * ((y - (viewportHeight - edge)) / edge);
        return Math.Clamp(offset + delta, 0, maximum);
    }
}
