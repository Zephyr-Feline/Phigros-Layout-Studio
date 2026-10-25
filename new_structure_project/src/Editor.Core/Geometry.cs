namespace Editor.Core;

public readonly record struct PointD(double X, double Y);

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public bool Contains(PointD point) =>
        point.X >= X && point.Y >= Y && point.X <= X + Width && point.Y <= Y + Height;
}

/// <summary>Paired document-unit and view-DIP conversions. Physical pixels are never persisted.</summary>
public readonly struct ViewportTransform
{
    public double Scale { get; }
    public PointD Origin { get; }

    public ViewportTransform(double documentWidth, double documentHeight,
        double viewWidth, double viewHeight, double zoom = 1, PointD pan = default)
    {
        if (!Positive(documentWidth) || !Positive(documentHeight) ||
            !Positive(viewWidth) || !Positive(viewHeight) || !Positive(zoom))
            throw new ArgumentOutOfRangeException(nameof(documentWidth), "Viewport dimensions and zoom must be finite and positive.");

        Scale = Math.Min(viewWidth / documentWidth, viewHeight / documentHeight) * zoom;
        Origin = new PointD(
            (viewWidth - documentWidth * Scale) / 2 + pan.X,
            (viewHeight - documentHeight * Scale) / 2 + pan.Y);
    }

    public PointD ToView(PointD logical) => new(Origin.X + logical.X * Scale, Origin.Y + logical.Y * Scale);
    public PointD ToLogical(PointD view) => new((view.X - Origin.X) / Scale, (view.Y - Origin.Y) / Scale);
    public RectD ToView(RectD logical) => new(Origin.X + logical.X * Scale, Origin.Y + logical.Y * Scale,
        logical.Width * Scale, logical.Height * Scale);
    public double ScreenPixelsToLogical(double pixels, double renderScale) => pixels / (Scale * renderScale);

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}
