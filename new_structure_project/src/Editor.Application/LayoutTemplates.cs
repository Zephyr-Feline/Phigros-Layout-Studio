using Editor.Core;

namespace Editor.Application;

public static class LayoutTemplates
{
    // Slots are fractions of the current persisted native canvas, not screen coordinates.
    public static IReadOnlyList<LayoutTemplate> All { get; } =
    [
        new("three-columns", "Three columns (3)",
        [new(.04, .16, .28, .70), new(.36, .16, .28, .70), new(.68, .16, .28, .70)]),
        new("five-point", "Five point (5)",
        [new(.04, .15, .25, .34), new(.04, .56, .25, .34),
         new(.375, .355, .25, .34), new(.71, .15, .25, .34), new(.71, .56, .25, .34)])
    ];

    internal static void Apply(EditorDocument document, IEnumerable<Guid> selectedIds, string templateId)
    {
        var template = All.FirstOrDefault(item => item.Id == templateId)
            ?? throw new ArgumentException("Unknown layout template.", nameof(templateId));
        Apply(document, selectedIds, template);
    }

    public static LayoutTemplate Capture(EditorDocument document, IEnumerable<Guid> selectedIds, string name)
    {
        var units = LayoutSelection.Units(document, selectedIds);
        var slots = units.Select(unit => MoveSnapping.BoundsOf(unit.Select(LayerGeometry.VisualBounds)))
            .Select(box => CanvasBoundaryPrecision.Normalize(box, document.CanvasWidth, document.CanvasHeight)).ToArray();
        var template = new LayoutTemplate(Guid.NewGuid().ToString("N"), name.Trim(), Array.AsReadOnly(slots));
        template.Validate();
        return template;
    }

    internal static void Apply(EditorDocument document, IEnumerable<Guid> selectedIds, LayoutTemplate template)
    {
        template.Validate();
        var units = LayoutSelection.Units(document, selectedIds);
        if (units.Count != template.Slots.Count)
            throw new InvalidOperationException($"{template.Name} requires exactly {template.Slots.Count} selected layers or groups.");
        if (units.SelectMany(unit => unit).Any(layer => layer.Locked))
            throw new InvalidOperationException("Unlock every member of the selected groups before arranging them.");

        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
            var origin = MoveSnapping.BoundsOf(unit.Select(LayerGeometry.VisualBounds));
            var normalized = template.Slots[index];
            var slot = new RectD(normalized.X * document.CanvasWidth,
                normalized.Y * document.CanvasHeight,
                normalized.Width * document.CanvasWidth,
                normalized.Height * document.CanvasHeight);
            var scale = Math.Min(1, Math.Min(slot.Width / origin.Width, slot.Height / origin.Height));
            var minimum = unit.Max(layer => Math.Max(
                (layer.Kind == LayerKind.Badge ? 8.0 : 1.0) / layer.Bounds.Width,
                1.0 / layer.Bounds.Height));
            if (scale < minimum)
                throw new InvalidOperationException($"{template.Name} cannot fit a selected group without making a layer too small.");
            // As in arrangement, sub-nanounit arithmetic residue is not a new placement.
            // Keep the original values when a unit already fits and is centred in its slot.
            const double tolerance = 1e-9;
            if (origin.Width <= slot.Width + tolerance && origin.Height <= slot.Height + tolerance &&
                Math.Abs(origin.X + origin.Width / 2 - slot.X - slot.Width / 2) < tolerance &&
                Math.Abs(origin.Y + origin.Height / 2 - slot.Y - slot.Height / 2) < tolerance)
                continue;
            // The legal minimum ratio may round down before multiplication (e.g. 49 * 1/49).
            // Advance the shared scale, rather than clipping each member's size independently.
            if (unit.Any(layer => layer.Bounds.Width * scale < (layer.Kind == LayerKind.Badge ? 8 : 1) ||
                layer.Bounds.Height * scale < 1)) scale = Math.BitIncrement(scale);
            var left = slot.X + (slot.Width - origin.Width * scale) / 2;
            var top = slot.Y + (slot.Height - origin.Height * scale) / 2;
            foreach (var layer in unit)
            {
                var source = layer.Bounds;
                layer.Bounds = new RectD(left + (source.X - origin.X) * scale,
                    top + (source.Y - origin.Y) * scale,
                    source.Width * scale, source.Height * scale);
                if (layer.Kind == LayerKind.Text)
                    layer.FontSize = Math.Clamp(layer.FontSize * scale, 1, 1000);
            }
        }
    }
}
