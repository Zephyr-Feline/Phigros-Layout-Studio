using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Editor.Core;

namespace Editor.Desktop;

public enum CanvasRulerOrientation
{
    Horizontal,
    Vertical
}

/// <summary>
/// Presentation-only ruler beside the canvas. Dragging from it creates a session guide; the
/// ruler itself never changes the document, history, native package or exported PNG.
/// </summary>
public sealed class CanvasRuler : Control
{
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(39, 39, 39));
    private static readonly IBrush PageBand = new SolidColorBrush(Color.FromRgb(52, 52, 52));
    private static readonly IBrush TickBrush = new SolidColorBrush(Color.FromRgb(165, 165, 165));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220));
    private static readonly IBrush GuideBrush = new SolidColorBrush(Color.FromRgb(255, 170, 60));
    private static readonly IPen TickPen = new Pen(TickBrush, 1);
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromRgb(80, 80, 80)), 1);
    private static readonly Typeface LabelTypeface = new("Microsoft YaHei UI");
    private readonly SceneCanvas _canvas;
    private bool _dragging;
    private IPointer? _pointer;

    public CanvasRulerOrientation Orientation { get; }

    public CanvasRuler(SceneCanvas canvas, CanvasRulerOrientation orientation)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        Orientation = orientation;
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(orientation == CanvasRulerOrientation.Horizontal
            ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast);
        ToolTip.SetTip(this, orientation == CanvasRulerOrientation.Horizontal
            ? "从顶部标尺向画布拖动可创建水平辅助线。"
            : "从左侧标尺向画布拖动可创建垂直辅助线。");
        var name = orientation == CanvasRulerOrientation.Horizontal ? "水平标尺" : "垂直标尺";
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetAutomationId(this,
            orientation == CanvasRulerOrientation.Horizontal ? "HorizontalRuler" : "VerticalRuler");
        AutomationProperties.SetHelpText(this,
            orientation == CanvasRulerOrientation.Horizontal
                ? "从顶部标尺拖入画布可创建水平辅助线。"
                : "从左侧标尺拖入画布可创建垂直辅助线。");
        _canvas.ViewChanged += InvalidateVisual;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CanvasRulerPeer(this);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background, bounds);
        if (Bounds.Width <= 0 || Bounds.Height <= 0 ||
            _canvas.Bounds.Width <= 0 || _canvas.Bounds.Height <= 0) return;

        var document = _canvas.Document;
        var view = _canvas.CurrentViewTransform;
        var horizontal = Orientation == CanvasRulerOrientation.Horizontal;
        var documentLength = horizontal ? document.CanvasWidth : document.CanvasHeight;
        var viewLength = horizontal ? Bounds.Width : Bounds.Height;
        var origin = horizontal ? view.Origin.X : view.Origin.Y;
        var pageStart = Math.Max(0, origin);
        var pageEnd = Math.Min(viewLength, origin + documentLength * view.Scale);
        if (pageEnd > pageStart)
            context.FillRectangle(PageBand, horizontal
                ? new Rect(pageStart, 0, pageEnd - pageStart, Bounds.Height)
                : new Rect(0, pageStart, Bounds.Width, pageEnd - pageStart));

        var majorStep = ChooseMajorStep(view.Scale);
        var minorStep = majorStep / 5;
        var showMinor = minorStep * view.Scale >= 5;
        var step = showMinor ? minorStep : majorStep;
        var firstIndex = (int)Math.Ceiling((pageStart - origin) / view.Scale / step - 1e-9);
        var lastIndex = (int)Math.Floor((pageEnd - origin) / view.Scale / step + 1e-9);
        for (var index = firstIndex; index <= lastIndex; index++)
        {
            var value = index * step;
            if (value < 0 || value > documentLength) continue;
            var major = !showMinor || index % 5 == 0;
            var position = origin + value * view.Scale;
            DrawTick(context, position, major);
            if (major) DrawLabel(context, position, value);
        }

        foreach (var guide in _canvas.ManualGuides.Items)
        {
            if (guide.Vertical != horizontal) continue;
            if (guide.Position < 0 || guide.Position > documentLength) continue;
            var position = origin + guide.Position * view.Scale;
            context.FillRectangle(GuideBrush, horizontal
                ? new Rect(position - 3, Bounds.Height - 7, 6, 7)
                : new Rect(Bounds.Width - 7, position - 3, 7, 6));
        }

        context.DrawLine(BorderPen, horizontal
            ? new Point(0, Bounds.Height - 0.5) : new Point(Bounds.Width - 0.5, 0),
            horizontal ? new Point(Bounds.Width, Bounds.Height - 0.5)
                : new Point(Bounds.Width - 0.5, Bounds.Height));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_dragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (!_canvas.BeginManualGuideDraft(
                Orientation == CanvasRulerOrientation.Vertical)) return;
        _dragging = true;
        _pointer = e.Pointer;
        e.Pointer.Capture(this);
        UpdateDraft(e);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        UpdateDraft(e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging || e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _canvas.CommitManualGuideDraft(ToLogical(e.GetPosition(_canvas)));
        EndDrag(cancel: false);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag(cancel: true);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || !_dragging) return;
        EndDrag(cancel: true);
        e.Handled = true;
    }

    private void UpdateDraft(PointerEventArgs e) =>
        _canvas.UpdateManualGuideDraft(ToLogical(e.GetPosition(_canvas)));

    private PointD ToLogical(Point canvasPoint) =>
        _canvas.CurrentViewTransform.ToLogical(new PointD(canvasPoint.X, canvasPoint.Y));

    private void EndDrag(bool cancel)
    {
        if (!_dragging) return;
        _dragging = false;
        if (cancel) _canvas.CancelManualGuideDraft();
        var pointer = _pointer;
        _pointer = null;
        if (ReferenceEquals(pointer?.Captured, this)) pointer.Capture(null);
    }

    private void DrawTick(DrawingContext context, double position, bool major)
    {
        var length = major ? 10.0 : 5.0;
        if (Orientation == CanvasRulerOrientation.Horizontal)
            context.DrawLine(TickPen, new Point(position, Bounds.Height),
                new Point(position, Bounds.Height - length));
        else
            context.DrawLine(TickPen, new Point(Bounds.Width, position),
                new Point(Bounds.Width - length, position));
    }

    private void DrawLabel(DrawingContext context, double position, double value)
    {
        var text = value.ToString("0", CultureInfo.InvariantCulture);
        using var layout = new TextLayout(text, LabelTypeface, 9, LabelBrush,
            textWrapping: TextWrapping.NoWrap);
        if (Orientation == CanvasRulerOrientation.Horizontal)
            layout.Draw(context, new Point(position + 2, 0));
        else
            layout.Draw(context, new Point(1, position - layout.Height / 2));
    }

    private sealed class CanvasRulerPeer(CanvasRuler owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Pane;
        protected override string GetClassNameCore() => "CanvasRuler";
        protected override string GetNameCore() =>
            AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : "标尺";
        protected override string GetHelpTextCore() =>
            AutomationProperties.GetHelpText(owner) ?? "从标尺拖入画布可创建辅助线。";
        protected override bool IsKeyboardFocusableCore() => owner.Focusable;
        protected override bool HasKeyboardFocusCore() => owner.IsFocused;
        protected override void SetFocusCore() => owner.Focus();
    }

    private static double ChooseMajorStep(double scale)
    {
        double[] candidates =
            [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000];
        foreach (var candidate in candidates)
            if (candidate * scale >= 72) return candidate;
        return Math.Ceiling(72 / scale / 10000) * 10000;
    }
}
