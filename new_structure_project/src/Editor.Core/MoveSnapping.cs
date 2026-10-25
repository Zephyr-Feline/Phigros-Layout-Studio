namespace Editor.Core;

/// <summary>Move-only snapping. Ties prefer canvas, layers, then stationary group unions.</summary>
public readonly record struct MoveSnapResult(PointD Delta, double? VerticalGuide, double? HorizontalGuide);

public static class MoveSnapping
{
    public const double ScreenPixelThreshold = 8;

    public static RectD BoundsOf(IEnumerable<RectD> bounds)
    {
        var boxes = bounds.ToArray();
        if (boxes.Length == 0) throw new ArgumentException("At least one moving layer is required.", nameof(bounds));
        var left = boxes.Min(box => box.X);
        var top = boxes.Min(box => box.Y);
        var right = boxes.Max(box => box.X + box.Width);
        var bottom = boxes.Max(box => box.Y + box.Height);
        return new RectD(left, top, right - left, bottom - top);
    }

    public static MoveSnapResult Solve(EditorDocument document, IReadOnlyCollection<Guid> movingIds,
        RectD startBounds, double deltaX, double deltaY, double toleranceX, double toleranceY,
        ManualGuideSet? manualGuides = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(movingIds);
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY) ||
            !double.IsFinite(toleranceX) || !double.IsFinite(toleranceY) ||
            toleranceX < 0 || toleranceY < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaX), "Move and tolerance must be finite; tolerance must be nonnegative.");

        var guides = SnapGuides.Collect(document, movingIds, manualGuides);
        var guidesX = guides.Where(guide => guide.Vertical).Select(guide => guide.Position).ToList();
        var guidesY = guides.Where(guide => !guide.Vertical).Select(guide => guide.Position).ToList();

        var x = SnapAxis(startBounds.X + deltaX, startBounds.Width, deltaX, guidesX, toleranceX);
        var y = SnapAxis(startBounds.Y + deltaY, startBounds.Height, deltaY, guidesY, toleranceY);
        return new MoveSnapResult(new PointD(x.Delta, y.Delta), x.Guide, y.Guide);
    }

    private static (double Delta, double? Guide) SnapAxis(double start, double size, double rawDelta,
        List<double> guides, double tolerance)
    {
        var features = new[] { start, start + size / 2, start + size };
        var best = double.PositiveInfinity;
        var correction = 0.0;
        double? selectedGuide = null;
        var boundaryEpsilon = tolerance == 0 ? 0 : 1e-9 * Math.Max(1, tolerance);
        foreach (var guide in guides)
        foreach (var feature in features)
        {
            var candidate = guide - feature;
            var distance = Math.Abs(candidate);
            if (distance <= tolerance + boundaryEpsilon && distance < best)
            {
                best = distance;
                correction = candidate;
                selectedGuide = guide;
            }
        }
        return (rawDelta + correction, selectedGuide);
    }
}

internal readonly record struct SnapGuide(bool Vertical, double Position);

internal static class SnapGuides
{
    // Canvas first, then session manual guides, then stable layer order, then stationary group
    // unions in first-layer order. Locked visible layers remain targets; hidden/moving layers
    // do not.
    // Both move and resize use this same candidate set.
    public static List<SnapGuide> Collect(EditorDocument document, IReadOnlyCollection<Guid> movingIds,
        ManualGuideSet? manualGuides = null)
    {
        var excluded = movingIds.ToHashSet();
        var guides = new List<SnapGuide>
        {
            new(true, 0), new(true, document.CanvasWidth / 2.0), new(true, document.CanvasWidth),
            new(false, 0), new(false, document.CanvasHeight / 2.0), new(false, document.CanvasHeight)
        };
        if (manualGuides is not null)
            foreach (var guide in manualGuides.Items)
                guides.Add(new SnapGuide(guide.Vertical, guide.Position));
        foreach (var layer in document.Layers)
        {
            if (!layer.Visible || excluded.Contains(layer.Id)) continue;
            var box = LayerGeometry.VisualBounds(layer);
            guides.Add(new SnapGuide(true, box.X));
            guides.Add(new SnapGuide(true, box.X + box.Width / 2));
            guides.Add(new SnapGuide(true, box.X + box.Width));
            guides.Add(new SnapGuide(false, box.Y));
            guides.Add(new SnapGuide(false, box.Y + box.Height / 2));
            guides.Add(new SnapGuide(false, box.Y + box.Height));
        }
        var movingGroups = document.Layers.Where(layer => excluded.Contains(layer.Id))
            .Select(layer => layer.GroupId).OfType<Guid>().ToHashSet();
        var visitedGroups = new HashSet<Guid>();
        foreach (var layer in document.Layers)
        {
            if (layer.GroupId is not Guid group || !visitedGroups.Add(group) || movingGroups.Contains(group))
                continue;
            var members = document.Layers.Where(member => member.GroupId == group && member.Visible &&
                !excluded.Contains(member.Id)).ToArray();
            if (members.Length < 2) continue;
            var box = MoveSnapping.BoundsOf(members.Select(LayerGeometry.VisualBounds));
            guides.Add(new SnapGuide(true, box.X));
            guides.Add(new SnapGuide(true, box.X + box.Width / 2));
            guides.Add(new SnapGuide(true, box.X + box.Width));
            guides.Add(new SnapGuide(false, box.Y));
            guides.Add(new SnapGuide(false, box.Y + box.Height / 2));
            guides.Add(new SnapGuide(false, box.Y + box.Height));
        }
        return guides;
    }
}
