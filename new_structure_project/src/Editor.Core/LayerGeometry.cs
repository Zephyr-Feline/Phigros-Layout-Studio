namespace Editor.Core;

public enum ResizeHandle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>Logical canvas geometry. Bounds remains the unrotated rectangle in saved projects.</summary>
public static class LayerGeometry
{
    public static PointD Centre(RectD box) => new(box.X + box.Width / 2, box.Y + box.Height / 2);

    public static PointD Rotate(PointD point, PointD pivot, double degrees)
    {
        if (degrees == 0) return point;
        var angle = degrees * Math.PI / 180;
        var cos = Math.Cos(angle); var sin = Math.Sin(angle);
        var x = point.X - pivot.X; var y = point.Y - pivot.Y;
        return new(pivot.X + x * cos - y * sin, pivot.Y + x * sin + y * cos);
    }

    public static PointD[] Corners(RectD box, double degrees = 0)
    {
        var pivot = Centre(box);
        return new PointD[]
        {
            new(box.X, box.Y), new(box.X + box.Width, box.Y),
            new(box.X + box.Width, box.Y + box.Height), new(box.X, box.Y + box.Height)
        }.Select(point => Rotate(point, pivot, degrees)).ToArray();
    }

    public static RectD VisualBounds(Layer layer)
    {
        if (layer.RotationDegrees == 0) return layer.Bounds;
        var points = Corners(layer.Bounds, layer.RotationDegrees);
        var x = points.Min(point => point.X); var y = points.Min(point => point.Y);
        return new(x, y, points.Max(point => point.X) - x, points.Max(point => point.Y) - y);
    }

    public static bool Contains(Layer layer, PointD canvasPoint) =>
        SlantedLayerGeometry.Contains(layer.Bounds,
            Rotate(canvasPoint, Centre(layer.Bounds), -layer.RotationDegrees), layer.Slanted);

    public static PointD HandlePoint(RectD box, ResizeHandle handle, double degrees = 0)
    {
        var (x, y) = HandleFraction(handle);
        return Rotate(new(box.X + box.Width * x, box.Y + box.Height * y), Centre(box), degrees);
    }

    public static (double X, double Y) HandleFraction(ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft => (0, 0), ResizeHandle.Top => (0.5, 0),
        ResizeHandle.TopRight => (1, 0), ResizeHandle.Right => (1, 0.5),
        ResizeHandle.BottomRight => (1, 1), ResizeHandle.Bottom => (0.5, 1),
        ResizeHandle.BottomLeft => (0, 1), ResizeHandle.Left => (0, 0.5),
        _ => throw new ArgumentOutOfRangeException(nameof(handle))
    };

    public static double NormalizeDegrees(double degrees)
    {
        if (!double.IsFinite(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees));
        var result = degrees % 360;
        if (result > 180) result -= 360;
        if (result < -180) result += 360;
        return result == 0 ? 0 : result;
    }
}
