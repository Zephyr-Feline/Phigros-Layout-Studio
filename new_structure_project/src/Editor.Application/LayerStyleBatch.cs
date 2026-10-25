using Editor.Core;

namespace Editor.Application;

/// <summary>
/// An explicit style patch. Null means "leave this field unchanged"; a batch operation never
/// copies values from a primary layer into the rest of the selection.
/// </summary>
public sealed record LayerStylePatch(
    string? FontFamily = null,
    double? FontSize = null,
    uint? ColorArgb = null,
    bool? Bold = null,
    bool? Italic = null,
    HorizontalAlignment? Alignment = null,
    VerticalAlignment? VerticalAlignment = null,
    double? LineSpacing = null,
    string? Difficulty = null,
    int? Level = null)
{
    public bool IsEmpty => FontFamily is null && FontSize is null && ColorArgb is null &&
        Bold is null && Italic is null && Alignment is null && VerticalAlignment is null &&
        LineSpacing is null && Difficulty is null && Level is null;

    public void Validate(LayerKind kind)
    {
        if (kind is not (LayerKind.Text or LayerKind.Badge))
            throw new InvalidOperationException("Batch styles support text layers or difficulty badges only.");
        if (kind == LayerKind.Text && (Difficulty is not null || Level is not null))
            throw new InvalidOperationException("Difficulty fields cannot be applied to text layers.");
        if (kind == LayerKind.Badge && (FontFamily is not null || FontSize is not null ||
            ColorArgb is not null || Bold is not null || Italic is not null || Alignment is not null ||
            VerticalAlignment is not null || LineSpacing is not null))
            throw new InvalidOperationException("Text style fields cannot be applied to difficulty badges.");
        if (FontFamily is not null &&
            (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 200 || FontFamily.Any(char.IsControl)))
            throw new InvalidDataException("Font family must be a nonempty name of at most 200 characters.");
        if (FontSize is double fontSize && (!double.IsFinite(fontSize) || fontSize is < 1 or > 1000))
            throw new InvalidDataException("Font size must be between 1 and 1000.");
        if (LineSpacing is double spacing && (!double.IsFinite(spacing) || spacing is < 0.5 or > 4))
            throw new InvalidDataException("Line spacing must be between 0.5 and 4.");
        if (Alignment is HorizontalAlignment alignment && !Enum.IsDefined(alignment))
            throw new InvalidDataException("Horizontal alignment is invalid.");
        if (VerticalAlignment is VerticalAlignment vertical && !Enum.IsDefined(vertical))
            throw new InvalidDataException("Vertical alignment is invalid.");
        if (Difficulty is not null && Difficulty is not ("EZ" or "HD" or "IN" or "AT"))
            throw new InvalidDataException("Difficulty must be EZ, HD, IN or AT.");
        if (Level is int level && level < 1)
            throw new InvalidDataException("Difficulty level must be a positive integer.");
    }

    public void ApplyTo(Layer layer)
    {
        if (FontFamily is not null) layer.FontFamily = FontFamily;
        if (FontSize is double fontSize) layer.FontSize = fontSize;
        if (ColorArgb is uint color) layer.ColorArgb = color;
        if (Bold is bool bold) layer.Bold = bold;
        if (Italic is bool italic) layer.Italic = italic;
        if (Alignment is HorizontalAlignment horizontal) layer.Alignment = horizontal;
        if (VerticalAlignment is VerticalAlignment vertical) layer.VerticalAlignment = vertical;
        if (LineSpacing is double spacing) layer.LineSpacing = spacing;
        if (Difficulty is not null) layer.Difficulty = Difficulty;
        if (Level is int level) layer.Level = level;
    }
}
