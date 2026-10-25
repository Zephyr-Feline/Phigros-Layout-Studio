using System;
using System.Drawing;

namespace PhigrosEnhancer
{
    // Pure, stateless mapping between one editor subsystem's persisted logical space
    // and a screen-space view rectangle.
    //
    // Used by classic preview/input/export and free-layout preview/input.
    // FreePainter exports directly in persisted canvas pixels (identity mapping);
    // ResizeCanvas retains its separate content-fit algorithm.
    internal sealed class LogicalViewportTransform
    {
        readonly float logicalWidth;
        readonly float logicalHeight;
        readonly RectangleF view;

        public LogicalViewportTransform(float logicalWidth, float logicalHeight, RectangleF view)
        {
            if (!IsPositive(logicalWidth)) throw new ArgumentOutOfRangeException("logicalWidth");
            if (!IsPositive(logicalHeight)) throw new ArgumentOutOfRangeException("logicalHeight");
            if (!IsPositive(view.Width)) throw new ArgumentOutOfRangeException("view");
            if (!IsPositive(view.Height)) throw new ArgumentOutOfRangeException("view");
            if (!IsFinite(view.X) || !IsFinite(view.Y)) throw new ArgumentOutOfRangeException("view");
            this.logicalWidth = logicalWidth;
            this.logicalHeight = logicalHeight;
            this.view = view;
        }
        static bool IsPositive(float value)
        {
            return value > 0f && IsFinite(value);
        }
        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
        public float LogicalWidth { get { return logicalWidth; } }
        public float LogicalHeight { get { return logicalHeight; } }
        public RectangleF View { get { return view; } }
        public float HorizontalScale { get { return view.Width / logicalWidth; } }
        public float VerticalScale { get { return view.Height / logicalHeight; } }

        public PointF LogicalToScreen(PointF logical)
        {
            return new PointF(view.X + logical.X * view.Width / logicalWidth,
                view.Y + logical.Y * view.Height / logicalHeight);
        }
        public PointF ScreenToLogical(PointF screen)
        {
            return new PointF((screen.X - view.X) * logicalWidth / view.Width,
                (screen.Y - view.Y) * logicalHeight / view.Height);
        }
        public SizeF LogicalDeltaToScreen(SizeF logicalDelta)
        {
            return new SizeF(logicalDelta.Width * view.Width / logicalWidth,
                logicalDelta.Height * view.Height / logicalHeight);
        }
        public SizeF ScreenDeltaToLogical(SizeF screenDelta)
        {
            return new SizeF(screenDelta.Width * logicalWidth / view.Width,
                screenDelta.Height * logicalHeight / view.Height);
        }
    }

    // Classic-mode pure geometry. The formula mirrors the current implementation in
    // Renderer.AspectCorrection; Renderer keeps its own copy until a later migration.
    internal static class ClassicGeometry
    {
        public const float ReferenceWidth = 1920f;
        public const float ReferenceHeight = 1080f;

        public static float AspectCorrection(float width, float height)
        {
            return ReferenceWidth * height / (ReferenceHeight * width);
        }
    }
}
