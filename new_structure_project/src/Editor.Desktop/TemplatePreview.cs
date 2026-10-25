using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Editor.Core;

namespace Editor.Desktop;

/// <summary>Draws only the normalized slot rectangles; no artwork or project content is read.</summary>
public sealed class TemplatePreview : Control
{
    public static readonly StyledProperty<LayoutTemplate?> TemplateProperty =
        AvaloniaProperty.Register<TemplatePreview, LayoutTemplate?>(nameof(Template));

    static TemplatePreview()
    {
        AffectsRender<TemplatePreview>(TemplateProperty);
    }

    public LayoutTemplate? Template
    {
        get => GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var outer = new Rect(Bounds.Size);
        if (outer.Width <= 2 || outer.Height <= 2) return;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(28, 46, 56)), outer);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(96, 120, 132)), 1), outer);
        if (Template is null || Template.Slots.Count == 0) return;
        const double margin = 8;
        var width = Math.Max(1, outer.Width - margin * 2);
        var height = Math.Max(1, outer.Height - margin * 2);
        var fill = new SolidColorBrush(Color.FromArgb(150, 255, 196, 74));
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 225, 150)), 1);
        foreach (var slot in Template.Slots)
        {
            var box = new Rect(margin + slot.X * width, margin + slot.Y * height,
                Math.Max(1, slot.Width * width), Math.Max(1, slot.Height * height));
            context.DrawRectangle(fill, stroke, box);
        }
    }
}
