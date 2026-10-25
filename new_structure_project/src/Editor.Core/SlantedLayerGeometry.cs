namespace Editor.Core;

/// <summary>The free-layout parallelogram shared by paint geometry and logical hit testing.</summary>
public static class SlantedLayerGeometry
{
    public static double Inset(RectD bounds) =>
        Math.Min(bounds.Height * 0.268, bounds.Width * 0.4);

    public static PointD[] Polygon(RectD bounds)
    {
        var dx = Inset(bounds);
        return
        [
            new(bounds.X + dx, bounds.Y),
            new(bounds.X + bounds.Width, bounds.Y),
            new(bounds.X + bounds.Width - dx, bounds.Y + bounds.Height),
            new(bounds.X, bounds.Y + bounds.Height)
        ];
    }

    public static bool Contains(RectD bounds, PointD point, bool slanted)
    {
        if (!bounds.Contains(point)) return false;
        if (!slanted) return true;
        var fraction = (point.Y - bounds.Y) / bounds.Height;
        var dx = Inset(bounds);
        return point.X >= bounds.X + dx * (1 - fraction) &&
            point.X <= bounds.X + bounds.Width - dx * fraction;
    }
}
