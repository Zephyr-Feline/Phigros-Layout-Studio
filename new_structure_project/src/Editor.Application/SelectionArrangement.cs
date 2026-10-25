using Editor.Core;

namespace Editor.Application;

public enum ArrangeAction { Left, CenterHorizontal, Right, Top, CenterVertical, Bottom, HorizontalGaps, VerticalGaps }
public enum ArrangeReference { SelectionBounds, Canvas }

/// <summary>Translation-only arrangement in native canvas units; called inside an edit transaction.</summary>
internal static class SelectionArrangement
{
    public static void Apply(EditorDocument document, IEnumerable<Guid> selectedIds,
        ArrangeAction action, ArrangeReference reference, bool includeGroups)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (!Enum.IsDefined(reference)) throw new ArgumentOutOfRangeException(nameof(reference));
        var units = LayoutSelection.Units(document, selectedIds, includeGroups);
        var distribute = action is ArrangeAction.HorizontalGaps or ArrangeAction.VerticalGaps;
        var minimum = distribute ? 3 : reference == ArrangeReference.Canvas ? 1 : 2;
        if (units.Count < minimum)
            throw new InvalidOperationException($"Select at least {minimum} independent layers or groups for this arrangement.");
        if (units.SelectMany(unit => unit).Any(layer => layer.Locked))
            throw new InvalidOperationException("Unlock every affected layer/group member before arranging.");

        var boxes = units.Select(unit => MoveSnapping.BoundsOf(unit.Select(LayerGeometry.VisualBounds))).ToArray();
        var target = reference == ArrangeReference.Canvas
            ? new RectD(0, 0, document.CanvasWidth, document.CanvasHeight)
            : MoveSnapping.BoundsOf(boxes);
        if (distribute)
        {
            var horizontal = action == ArrangeAction.HorizontalGaps;
            double Start(RectD b) => horizontal ? b.X : b.Y;
            double Size(RectD b) => horizontal ? b.Width : b.Height;
            // Stable document order breaks geometric ties, independent of click order.
            var order = Enumerable.Range(0, units.Count).OrderBy(i => Start(boxes[i]))
                .ThenBy(i => units[i].Min(layer => document.Layers.IndexOf(layer))).ToArray();
            var total = boxes.Sum(Size);
            var gap = (Size(target) - total) / (units.Count - 1);
            if (gap < -1e-9)
                throw new InvalidOperationException("Not enough room for non-overlapping equal gaps. Choose Canvas or spread the selection farther apart.");
            gap = Math.Max(0, gap);
            double used = 0;
            for (var rank = 0; rank < order.Length; rank++)
            {
                var i = order[rank];
                var destination = rank == order.Length - 1
                    ? Start(target) + Size(target) - Size(boxes[i])
                    : Start(target) + used + rank * gap;
                var delta = destination - Start(boxes[i]);
                Translate(units[i], horizontal ? delta : 0, horizontal ? 0 : delta);
                used += Size(boxes[i]);
            }
            return;
        }

        for (var i = 0; i < units.Count; i++)
        {
            var b = boxes[i];
            var delta = action switch
            {
                ArrangeAction.Left => new PointD(target.X - b.X, 0),
                ArrangeAction.CenterHorizontal => new(target.X + target.Width / 2 - (b.X + b.Width / 2), 0),
                ArrangeAction.Right => new(target.X + target.Width - (b.X + b.Width), 0),
                ArrangeAction.Top => new(0, target.Y - b.Y),
                ArrangeAction.CenterVertical => new(0, target.Y + target.Height / 2 - (b.Y + b.Height / 2)),
                ArrangeAction.Bottom => new(0, target.Y + target.Height - (b.Y + b.Height)),
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
            Translate(units[i], delta.X, delta.Y);
        }
    }

    private static void Translate(IEnumerable<Layer> layers, double dx, double dy)
    {
        // Avoid history entries caused only by floating-point residue on repeated Apply.
        if (Math.Abs(dx) < 1e-9) dx = 0;
        if (Math.Abs(dy) < 1e-9) dy = 0;
        foreach (var layer in layers)
            layer.Bounds = layer.Bounds with { X = layer.Bounds.X + dx, Y = layer.Bounds.Y + dy };
    }
}
