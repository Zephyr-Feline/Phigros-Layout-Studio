using Editor.Core;

namespace Editor.Formats.Compatibility;

/// <summary>
/// Classic (<c>.phipreview</c>) reference geometry, transcribed from the V1 implementation
/// (<c>Renderer.AspectCorrection</c>, <c>BadgeRect</c>, <c>AuthorRect</c>, <c>Clamp</c>) so the
/// migration keeps the same numbers the legacy renderer produced. Purely arithmetic.
/// </summary>
public static class ClassicGeometry
{
    public const double ReferenceWidth = 1920;
    public const double ReferenceHeight = 1080;
    public const double BadgeWidth = 128;
    public const double BadgeHeight = 108;
    public const double AuthorHeight = 40;
    public const double AuthorFontSize = 25;
    public const double ShadowOffset = 3;

    /// <summary>V1: <c>1920 * height / (1080 * width)</c> for the final bitmap size.</summary>
    public static double AspectCorrection(double width, double height)
    {
        RequirePositive(width, nameof(width));
        RequirePositive(height, nameof(height));
        return ReferenceWidth * height / (ReferenceHeight * width);
    }

    /// <summary>Uniform final pixel scale of a classic export: <c>height / 1080</c>.</summary>
    public static double FinalScale(double height)
    {
        RequirePositive(height, nameof(height));
        return height / ReferenceHeight;
    }

    /// <summary>Reference-space value converted back to final bitmap pixels (inverse of the mapping).</summary>
    public static double ToCanvasX(double referenceX, double width) => referenceX * width / ReferenceWidth;

    public static double ToCanvasY(double referenceY, double height) => referenceY * FinalScale(height);

    /// <summary>Final bitmap X converted back into the 1920-wide reference space.</summary>
    public static double ToReferenceX(double canvasX, double width)
    {
        RequirePositive(width, nameof(width));
        return canvasX * ReferenceWidth / width;
    }

    public static double ToReferenceY(double canvasY, double height)
    {
        RequirePositive(height, nameof(height));
        return canvasY * ReferenceHeight / height;
    }

    /// <summary>Default classic export height for a width: ToEven rounding of the aspect product.</summary>
    public static int ExportHeight(int backgroundWidth, int backgroundHeight, int exportWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backgroundWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backgroundHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exportWidth);
        return (int)Math.Round((double)backgroundHeight * exportWidth / backgroundWidth,
            MidpointRounding.ToEven);
    }

    /// <summary>
    /// V1 <c>Clamp</c> in reference space; the legacy renderer clamps a copy before drawing, so the
    /// stored values stay untouched and the clamp only affects the drawn geometry.
    /// </summary>
    public static ClassicLabelGeometry Clamp(ClassicLabelGeometry label, double correction,
        out bool clamped)
    {
        var authorWidth = Math.Min(label.AuthorWidth,
            ReferenceWidth / (label.Scale * correction));
        var badgeX = Math.Clamp(label.BadgeX, 0,
            ReferenceWidth - (BadgeWidth * label.Scale + ShadowOffset * label.Scale) * correction);
        var badgeY = Math.Clamp(label.BadgeY, 0,
            ReferenceHeight - BadgeHeight * label.Scale - ShadowOffset * label.Scale);
        var authorX = Math.Clamp(label.AuthorX, 0,
            ReferenceWidth - authorWidth * label.Scale * correction);
        var authorY = Math.Clamp(label.AuthorY, 0,
            ReferenceHeight - AuthorHeight * label.Scale);
        var result = label with
        {
            BadgeX = badgeX, BadgeY = badgeY, AuthorX = authorX, AuthorY = authorY,
            AuthorWidth = authorWidth
        };
        clamped = result.AuthorWidth != label.AuthorWidth || result.BadgeX != label.BadgeX ||
            result.BadgeY != label.BadgeY || result.AuthorX != label.AuthorX || result.AuthorY != label.AuthorY;
        return result;
    }

    /// <summary>
    /// Maps one classic label to canvas-pixel rectangles for an export bitmap of
    /// <paramref name="width"/> x <paramref name="height"/>.
    /// </summary>
    public static ClassicLabelPlacement Place(ClassicLabelGeometry label, double width, double height)
    {
        var correction = AspectCorrection(width, height);
        var scale = FinalScale(height);
        var clampedLabel = Clamp(label, correction, out var clamped);
        var badge = new RectD(
            ToCanvasX(clampedLabel.BadgeX, width),
            ToCanvasY(clampedLabel.BadgeY, height),
            BadgeWidth * clampedLabel.Scale * scale,
            BadgeHeight * clampedLabel.Scale * scale);
        var author = new RectD(
            ToCanvasX(clampedLabel.AuthorX, width),
            ToCanvasY(clampedLabel.AuthorY, height),
            clampedLabel.AuthorWidth * clampedLabel.Scale * scale,
            AuthorHeight * clampedLabel.Scale * scale);
        return new ClassicLabelPlacement(badge, author, AuthorFontSize * clampedLabel.Scale * scale,
            correction, scale, clamped);
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, "Classic geometry needs finite positive sizes.");
    }
}

/// <summary>Reference-space classic label numbers used by the mapping.</summary>
public readonly record struct ClassicLabelGeometry(double BadgeX, double BadgeY, double AuthorX,
    double AuthorY, double Scale, double AuthorWidth);

/// <summary>Canvas-pixel placement of one classic label.</summary>
public readonly record struct ClassicLabelPlacement(RectD Badge, RectD Author, double AuthorFontSize,
    double Correction, double Scale, bool Clamped);
