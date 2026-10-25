namespace Editor.Core;

/// <summary>Snaps the moving right/bottom resize handle while the opposite corner stays fixed.</summary>
public readonly record struct ResizeSnapResult(PointD Delta, double? VerticalGuide, double? HorizontalGuide);

public static class ResizeSnapping
{
    public static ResizeSnapResult Solve(EditorDocument document, IReadOnlyCollection<Guid> resizingIds,
        RectD startBounds, double deltaWidth, double deltaHeight, double toleranceX,
        double toleranceY, bool uniform, ResizeHandle handle = ResizeHandle.BottomRight,
        ManualGuideSet? manualGuides = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(resizingIds);
        if (!double.IsFinite(startBounds.X) || !double.IsFinite(startBounds.Y) ||
            !double.IsFinite(startBounds.Width) || !double.IsFinite(startBounds.Height) ||
            startBounds.Width <= 0 || startBounds.Height <= 0 ||
            !double.IsFinite(deltaWidth) || !double.IsFinite(deltaHeight) ||
            !double.IsFinite(toleranceX) || !double.IsFinite(toleranceY) ||
            toleranceX < 0 || toleranceY < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaWidth),
                "Bounds, resize deltas and tolerances must be finite; sizes must be positive.");

        var guides = SnapGuides.Collect(document, resizingIds, manualGuides);
        var (hx, hy) = LayerGeometry.HandleFraction(handle);
        var changesX = hx != 0.5; var changesY = hy != 0.5;
        var anchorX = startBounds.X + startBounds.Width * (1 - hx);
        var anchorY = startBounds.Y + startBounds.Height * (1 - hy);
        var signX = hx == 0 ? -1 : 1; var signY = hy == 0 ? -1 : 1;
        if (uniform)
        {
            // EditSession.ResizeBy uses the larger requested scale for multi-selection and
            // aspect-locked layers. Snap in that one-dimensional scale space, never request
            // independent X/Y corrections that the editor cannot actually apply.
            var sx = 1 + signX * deltaWidth / startBounds.Width;
            var sy = 1 + signY * deltaHeight / startBounds.Height;
            var rawScale = Math.Max(0.05, changesX && changesY ? Math.Max(sx, sy) : changesX ? sx : sy);
            var bestDistance = double.PositiveInfinity;
            double? bestScale = null;
            SnapGuide? bestGuide = null;
            foreach (var guide in guides)
            {
                if (guide.Vertical ? !changesX : !changesY) continue;
                var size = guide.Vertical ? startBounds.Width : startBounds.Height;
                var anchor = guide.Vertical ? anchorX : anchorY;
                var sign = guide.Vertical ? signX : signY;
                var candidateScale = (guide.Position - anchor) / (size * sign);
                if (candidateScale < 0.05) continue;
                var scaleChange = Math.Abs(candidateScale - rawScale);
                var xShift = changesX ? scaleChange * startBounds.Width : 0;
                var yShift = changesY ? scaleChange * startBounds.Height : 0;
                var distance = Math.Max(xShift, yShift);
                if (Within(xShift, toleranceX) && Within(yShift, toleranceY) &&
                    distance < bestDistance)
                {
                    bestDistance = distance;
                    bestScale = candidateScale;
                    bestGuide = guide;
                }
            }
            if (bestScale is not double scale || bestGuide is not SnapGuide selected)
                return new ResizeSnapResult(new PointD(deltaWidth, deltaHeight), null, null);
            return new ResizeSnapResult(new PointD(
                changesX ? signX * (scale - 1) * startBounds.Width : 0,
                changesY ? signY * (scale - 1) * startBounds.Height : 0),
                selected.Vertical ? selected.Position : null,
                selected.Vertical ? null : selected.Position);
        }

        var edgeX = startBounds.X + startBounds.Width * hx + deltaWidth;
        var edgeY = startBounds.Y + startBounds.Height * hy + deltaHeight;
        var x = changesX ? SnapEdge(edgeX, guides.Where(guide => guide.Vertical), toleranceX) : (Correction: 0.0, Guide: (double?)null);
        var y = changesY ? SnapEdge(edgeY, guides.Where(guide => !guide.Vertical), toleranceY) : (Correction: 0.0, Guide: (double?)null);
        return new ResizeSnapResult(new PointD(changesX ? deltaWidth + x.Correction : 0,
            changesY ? deltaHeight + y.Correction : 0), x.Guide, y.Guide);
    }

    private static (double Correction, double? Guide) SnapEdge(double edge,
        IEnumerable<SnapGuide> guides, double tolerance)
    {
        var best = double.PositiveInfinity;
        var correction = 0.0;
        double? selected = null;
        foreach (var guide in guides)
        {
            var candidate = guide.Position - edge;
            var distance = Math.Abs(candidate);
            if (Within(distance, tolerance) && distance < best)
            {
                best = distance;
                correction = candidate;
                selected = guide.Position;
            }
        }
        return (correction, selected);
    }

    private static bool Within(double distance, double tolerance) =>
        distance <= tolerance + (tolerance == 0 ? 0 : 1e-9 * Math.Max(1, tolerance));
}
