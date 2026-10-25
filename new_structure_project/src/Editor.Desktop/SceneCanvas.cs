using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Editor.Application;
using Editor.Core;
using Editor.Rendering;

namespace Editor.Desktop;

public sealed class SceneCanvas : Control
{
    // Covers 100% for the supported 7680 x 4320 document range in the minimum canvas viewport.
    private const double MinimumRelativeZoom = 0.02;
    private const double MaximumRelativeZoom = 16;
    private const double ZoomStep = 1.25;
    private const double SelectionPaddingDips = 32;
    private readonly EditSession _session;
    private readonly BackgroundRenderCache _backgroundCache = new();
    private readonly DecodedImageCache _imageCache = new();
    private PointD _pressLogical;
    private Point _pressView;
    private PointD _panAtPress;
    private bool _resizing;
    private bool _rotating;
    private ResizeHandle _resizeHandle = ResizeHandle.BottomRight;
    private double _resizeStartRotation;
    private double _rotationAtPress;
    private PointD _rotationPivot;
    private bool _editing;
    private bool _panning;
    private bool _selecting;
    private RectD? _selectionBox;
    private Guid[] _selectionAtPress = [];
    private bool _addToSelection;
    private IPointer? _capturedPointer;
    private RectD _moveStartBounds;
    private MoveAxis _moveAxis;
    private double? _verticalGuide;
    private double? _horizontalGuide;
    private readonly ManualGuideSet _manualGuides = new();
    private bool _guideDragging;
    private int _guideDragIndex = -1;
    private double? _guideDragPosition;
    private bool _guideDraftActive;
    private bool _guideDraftVertical;
    private double? _guideDraftPosition;
    private int _guideCanvasWidth = -1;
    private int _guideCanvasHeight = -1;
    private double _zoom = 1;
    private PointD _pan;
    private EditorDocument? _interactionDocument;
    private sealed record InteractionView(int CanvasWidth, int CanvasHeight, double Width, double Height,
        double Zoom, PointD Pan);
    private InteractionView? _interactionView;
    private Point? _hoverPosition;
    private KeyModifiers _hoverModifiers;
    private readonly Dictionary<StandardCursorType, Cursor> _cursors = new();
    private bool _feedbackDetached;

    public event Action? Changed;
    public event Action<string>? Error;
    public event Action<Guid>? TextEditRequested;
    public event Action? ViewChanged;
    public bool MoveWholeGroup { get; set; } = true;
    public ManualGuideSet ManualGuides => _manualGuides;
    public EditorDocument Document => _session.Document;
    public ViewportTransform CurrentViewTransform => ViewTransform();
    public double ZoomPercent => Bounds.Width > 0 && Bounds.Height > 0
        ? ViewTransform().Scale * 100
        : 0;
    private bool _showBackgroundContributors;
    public bool ShowBackgroundContributors
    {
        get => _showBackgroundContributors;
        set
        {
            if (_showBackgroundContributors == value) return;
            _showBackgroundContributors = value;
            InvalidateVisual();
        }
    }
    private bool _showSafeArea;
    public bool ShowSafeArea
    {
        get => _showSafeArea;
        set
        {
            if (_showSafeArea == value) return;
            _showSafeArea = value;
            InvalidateVisual();
        }
    }
    private double _safeAreaMarginPercent = 10;
    public double SafeAreaMarginPercent
    {
        get => _safeAreaMarginPercent;
        set
        {
            var clamped = double.IsFinite(value) ? Math.Clamp(value, 0, 20) : 10;
            if (Math.Abs(_safeAreaMarginPercent - clamped) < 1e-9) return;
            _safeAreaMarginPercent = clamped;
            InvalidateVisual();
        }
    }
    public bool IsInteracting => _editing || _panning || _selecting || _guideDragging ||
        _guideDraftActive;

    public SceneCanvas(EditSession session)
    {
        _session = session;
        _session.DocumentReplaced += OnDocumentReplaced;
        SizeChanged += (_, _) => ViewChanged?.Invoke();
        Focusable = true;
        ClipToBounds = true;
        AutomationProperties.SetName(this, "画布");
        AutomationProperties.SetHelpText(this,
            "方向键微调所选图层，Shift 加大步进；F6 切换编辑区域焦点。");
        // Anchor hints to the canvas rather than the pointer beside the property panel.
        ToolTip.SetPlacement(this, PlacementMode.TopEdgeAlignedLeft);
        SetPointerFeedback(StandardCursorType.Arrow, "拖动空白处框选；中键平移；滚轮缩放。");
        AutomationProperties.SetAutomationId(this, "SceneCanvas");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SceneCanvasPeer(this);

    public override void Render(DrawingContext context)
    {
        ValidateInteraction();
        context.FillRectangle(Brushes.DimGray, new Rect(Bounds.Size));
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var transform = ViewTransform();
        using (context.PushClip(new Rect(Bounds.Size)))
        using (context.PushTransform(Matrix.CreateScale(transform.Scale, transform.Scale) *
                                     Matrix.CreateTranslation(transform.Origin.X, transform.Origin.Y)))
            ScenePainter.Paint(context, _session.Document, NativeImaging.Decoder,
                backgroundCache: _backgroundCache, imageCache: _imageCache);

        if (ShowBackgroundContributors &&
            _session.Document.FixedBackgroundAssetId is null &&
            _session.Document.BackgroundBlend.Enabled)
        {
            var contributorPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 180, 70)), 1.5);
            foreach (var layer in BackgroundComposer.ContributingLayers(_session.Document))
            {
                var box = transform.ToView(LayerGeometry.VisualBounds(layer));
                context.DrawRectangle(null, contributorPen,
                    new Rect(box.X, box.Y, box.Width, box.Height));
            }
        }

        if (ShowSafeArea && _safeAreaMarginPercent > 0)
            DrawSafeArea(context, transform);
        DrawManualGuides(context, transform);
        var guidePen = new Pen(Brushes.Cyan, 1);
        if (_selectionBox is RectD region)
        {
            var box = transform.ToView(region);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 200, 255)), guidePen,
                new Rect(box.X, box.Y, box.Width, box.Height));
        }
        if (_verticalGuide is double x)
            context.DrawLine(guidePen, ToView(x, 0), ToView(x, _session.Document.CanvasHeight));
        if (_horizontalGuide is double y)
            context.DrawLine(guidePen, ToView(0, y), ToView(_session.Document.CanvasWidth, y));

        foreach (var id in _session.SelectedLayerIds)
        {
            var layer = _session.Document.Find(id);
            if (!layer.Visible) continue;
            DrawSelection(context, layer.Bounds, layer.RotationDegrees,
                new Pen(layer.Locked ? Brushes.Gold : Brushes.White, 2));
        }
        if (ResizeSelection() is { } resize)
        {
            if (resize.TargetCount > 1)
            {
                // Cyan shows the complete linked target set, including unselected group members.
                var targets = _editing ? _session.ActiveGestureIds :
                    _session.GestureTargets(resize.Id, includeGroup: MoveWholeGroup);
                foreach (var id in targets.Where(id => !_session.SelectedLayerIds.Contains(id)))
                {
                    var layer = _session.Document.Find(id);
                    if (layer.Visible)
                        DrawSelection(context, layer.Bounds, layer.RotationDegrees, new Pen(Brushes.Cyan, 1));
                }
                DrawSelection(context, resize.Bounds, resize.RotationDegrees, new Pen(Brushes.Cyan, 1));
            }
            foreach (var handle in Enum.GetValues<ResizeHandle>())
            {
                var point = transform.ToView(LayerGeometry.HandlePoint(resize.Bounds, handle, resize.RotationDegrees));
                context.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1),
                    new Rect(point.X - 4, point.Y - 4, 8, 8));
            }
            var top = transform.ToView(LayerGeometry.HandlePoint(resize.Bounds, ResizeHandle.Top, resize.RotationDegrees));
            var rotation = RotationHandle(resize.Bounds, resize.RotationDegrees);
            context.DrawLine(new Pen(Brushes.Cyan, 1), new(top.X, top.Y), rotation);
            context.DrawEllipse(Brushes.Cyan, new Pen(Brushes.Black, 1), rotation, 5, 5);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        ValidateInteraction();
        if (IsInteracting) return;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var position = e.GetPosition(this);
        _hoverPosition = position;
        _hoverModifiers = e.KeyModifiers;
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsMiddleButtonPressed)
        {
            Focus();
            _panning = true;
            _pressView = position;
            _panAtPress = _pan;
            CaptureInteractionView();
            _capturedPointer = e.Pointer;
            e.Pointer.Capture(this);
            UpdatePointerFeedback();
            return;
        }
        if (!properties.IsLeftButtonPressed || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        Focus();
        var transform = ViewTransform();
        var logical = transform.ToLogical(new PointD(position.X, position.Y));
        var multiSelect = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && ResizeSelection() is { } resize)
        {
            var handleHit = HitHandle(position, resize.Bounds, resize.RotationDegrees);
            if (handleHit is { Rotating: true })
            {
                Begin(resize.Id, logical, resizing: false, e);
                _rotating = true;
                _rotationPivot = LayerGeometry.Centre(_moveStartBounds);
                _rotationAtPress = Math.Atan2(logical.Y - _rotationPivot.Y, logical.X - _rotationPivot.X);
                UpdatePointerFeedback();
                e.Handled = true;
                return;
            }
            if (handleHit is { } hitHandle)
            {
                Begin(resize.Id, logical, resizing: true, e);
                _resizeHandle = hitHandle.Handle;
                _resizeStartRotation = resize.RotationDegrees;
                _moveStartBounds = resize.Bounds;
                UpdatePointerFeedback();
                e.Handled = true;
                return;
            }
        }
        if (!multiSelect && HitManualGuide(position) is { } manualGuide)
        {
            _guideDragging = true;
            _guideDragIndex = manualGuide.Index;
            _guideDragPosition = manualGuide.Guide.Position;
            ClearSnapGuides();
            CaptureInteractionView();
            _capturedPointer = e.Pointer;
            e.Pointer.Capture(this);
            UpdatePointerFeedback();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        var selectionBefore = _session.SelectedLayerIds.ToArray();
        var hit = _session.Document.Layers.AsEnumerable().Reverse()
            .FirstOrDefault(layer => layer.Visible && !layer.Locked &&
                LayerGeometry.Contains(layer, logical));
        if (hit is { Kind: LayerKind.Text } && e.ClickCount == 2 && !multiSelect)
        {
            _session.SelectedLayerId = hit.Id;
            NotifySelectionChanged(selectionBefore);
            InvalidateVisual();
            TextEditRequested?.Invoke(hit.Id);
            e.Handled = true;
            return;
        }
        if (hit is null)
        {
            _selectionAtPress = _session.SelectedLayerIds.ToArray();
            _addToSelection = multiSelect;
            _pressLogical = logical;
            _selecting = true;
            _selectionBox = null;
            CaptureInteractionView();
            if (!multiSelect) _session.SetSelection([]);
            _capturedPointer = e.Pointer;
            e.Pointer.Capture(this);
        }
        else if (multiSelect)
        {
            var selected = _session.SelectedLayerIds.ToList();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && selected.Contains(hit.Id))
                selected.Remove(hit.Id);
            else if (!selected.Contains(hit.Id)) selected.Insert(0, hit.Id);
            _session.SetSelection(selected);
        }
        else if (!_session.SelectedLayerIds.Contains(hit.Id)) _session.SelectedLayerId = hit.Id;
        NotifySelectionChanged(selectionBefore);
        InvalidateVisual();
        if (hit is not null && !multiSelect) Begin(hit.Id, logical, resizing: false, e);
        UpdatePointerFeedback();
        e.Handled = true;
    }

    private void Begin(Guid id, PointD logical, bool resizing, PointerPressedEventArgs e)
    {
        _session.BeginGesture(id, includeGroup: MoveWholeGroup, includeSelection: true);
        _pressLogical = logical;
        _resizing = resizing;
        _moveStartBounds = _session.ActiveGestureBounds;
        _moveAxis = MoveAxis.None;
        ClearSnapGuides();
        _editing = true;
        CaptureInteractionView();
        _capturedPointer = e.Pointer;
        e.Pointer.Capture(this);
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!ValidateInteraction()) return;
        var position = e.GetPosition(this);
        _hoverPosition = position;
        _hoverModifiers = e.KeyModifiers;
        UpdatePointerFeedback();
        if (_selecting)
        {
            var end = ViewTransform().ToLogical(new PointD(position.X, position.Y));
            _selectionBox = new RectD(Math.Min(_pressLogical.X, end.X), Math.Min(_pressLogical.Y, end.Y),
                Math.Abs(end.X - _pressLogical.X), Math.Abs(end.Y - _pressLogical.Y));
            var selectionBefore = _session.SelectedLayerIds.ToArray();
            _session.SelectCanvasRegion(_selectionBox.Value, _addToSelection ? _selectionAtPress : null);
            NotifySelectionChanged(selectionBefore);
            InvalidateVisual();
            return;
        }
        if (_panning)
        {
            _pan = new PointD(_panAtPress.X + position.X - _pressView.X,
                _panAtPress.Y + position.Y - _pressView.Y);
            ViewChanged?.Invoke();
            InvalidateVisual();
            return;
        }
        var transform = ViewTransform();
        var logical = transform.ToLogical(new PointD(position.X, position.Y));
        if (_guideDragging)
        {
            UpdateManualGuideDrag(logical);
            InvalidateVisual();
            return;
        }
        if (!_editing) return;
        var dx = logical.X - _pressLogical.X;
        var dy = logical.Y - _pressLogical.Y;
        if (_rotating)
        {
            ClearSnapGuides();
            var degrees = LayerGeometry.NormalizeDegrees((Math.Atan2(logical.Y - _rotationPivot.Y,
                logical.X - _rotationPivot.X) - _rotationAtPress) * 180 / Math.PI);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) degrees = Math.Round(degrees / 15) * 15;
            _session.RotateBy(degrees);
        }
        else if (_resizing)
        {
            var forceUniform = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            // A stationary handle click must retain its original bounds even when an
            // unchanged edge is close to a snap guide. This also restores an exact return.
            if (dx == 0 && dy == 0)
            {
                ClearSnapGuides();
                _session.ResizeFromHandle(0, 0, _resizeHandle, forceUniform);
                InvalidateVisual();
                return;
            }
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) || _resizeStartRotation != 0)
            {
                ClearSnapGuides();
                _session.ResizeFromHandle(dx, dy, _resizeHandle, forceUniform);
            }
            else
            {
                var renderScale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
                var tolerance = transform.ScreenPixelsToLogical(
                    MoveSnapping.ScreenPixelThreshold, renderScale);
                var targets = _session.ActiveGestureIds;
                var uniform = forceUniform || targets.Count > 1 || targets.Any(id =>
                    _session.Document.Find(id) is { KeepAspect: true } or { Kind: LayerKind.Badge });
                var snap = ResizeSnapping.Solve(_session.Document, targets,
                    _moveStartBounds, dx, dy, tolerance, tolerance, uniform, _resizeHandle,
                    BuildSnapGuideSet());
                _session.ResizeFromHandle(snap.Delta.X, snap.Delta.Y, _resizeHandle, forceUniform);
                var applied = targets.Count == 1 ? _session.Document.Find(targets[0]).Bounds :
                    MoveSnapping.BoundsOf(targets.Select(id => LayerGeometry.VisualBounds(_session.Document.Find(id))));
                var edge = LayerGeometry.HandlePoint(applied, _resizeHandle);
                _verticalGuide = snap.VerticalGuide is double guideX &&
                    Math.Abs(edge.X - guideX) < 1e-6 ? guideX : null;
                _horizontalGuide = snap.HorizontalGuide is double guideY &&
                    Math.Abs(edge.Y - guideY) < 1e-6 ? guideY : null;
            }
        }
        else
        {
            var atPressPosition = dx == 0 && dy == 0;
            var renderScale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var locked = MoveDirectionLock.Apply(dx, dy,
                e.KeyModifiers.HasFlag(KeyModifiers.Shift), _moveAxis,
                transform.ScreenPixelsToLogical(4, renderScale));
            _moveAxis = locked.Axis;
            // Capture can deliver a move at the unchanged press position. A selection click
            // (or a drag returned exactly to its start) must not become a snapped edit.
            // Still update the axis above so releasing Shift here clears the previous lock.
            if (atPressPosition)
            {
                ClearSnapGuides();
                _session.MoveBy(0, 0);
                InvalidateVisual();
                return;
            }
            dx = locked.Delta.X;
            dy = locked.Delta.Y;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                ClearSnapGuides();
                _session.MoveBy(dx, dy);
                InvalidateVisual();
                return;
            }
            var tolerance = transform.ScreenPixelsToLogical(MoveSnapping.ScreenPixelThreshold, renderScale);
            var snap = MoveSnapping.Solve(_session.Document, _session.ActiveGestureIds,
                _moveStartBounds, dx, dy, tolerance, tolerance, BuildSnapGuideSet());
            var horizontalOnly = _moveAxis == MoveAxis.Horizontal;
            var verticalOnly = _moveAxis == MoveAxis.Vertical;
            var applied = _session.MoveBy(verticalOnly ? 0 : snap.Delta.X,
                horizontalOnly ? 0 : snap.Delta.Y);
            _verticalGuide = verticalOnly ? null :
                Math.Abs(applied.X - snap.Delta.X) < 1e-9 ? snap.VerticalGuide : null;
            _horizontalGuide = horizontalOnly ? null :
                Math.Abs(applied.Y - snap.Delta.Y) < 1e-9 ? snap.HorizontalGuide : null;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!ValidateInteraction() || !IsInteracting) return;
        // Releasing a secondary button must not end a left-drag or a middle-button pan.
        var properties = e.GetCurrentPoint(this).Properties;
        if ((_editing || _selecting) && properties.IsLeftButtonPressed ||
            _panning && properties.IsMiddleButtonPressed) return;
        if (_guideDragging)
        {
            if (properties.IsLeftButtonPressed) return;
            CommitManualGuideDrag();
            EndInteraction();
            _hoverPosition = e.GetPosition(this);
            _hoverModifiers = e.KeyModifiers;
            UpdatePointerFeedback();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        var changed = false;
        if (_editing)
        {
            try { changed = _session.CommitGesture(); }
            catch (Exception error)
            {
                _session.CancelGesture();
                Error?.Invoke(error.Message);
            }
        }
        EndInteraction();
        _hoverPosition = e.GetPosition(this);
        _hoverModifiers = e.KeyModifiers;
        UpdatePointerFeedback();
        if (changed) Changed?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelInteraction();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverPosition = null;
        ToolTip.SetIsOpen(this, false);
        UpdatePointerFeedback();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!ValidateInteraction()) { e.Handled = true; return; }
        _hoverModifiers = e.KeyModifiers;
        UpdatePointerFeedback();
        if (e.Key == Key.Escape && IsInteracting)
        {
            CancelInteraction();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && _hoverPosition is Point hover &&
            HitManualGuide(hover) is { } manualGuide)
        {
            _manualGuides.RemoveAt(manualGuide.Index);
            UpdatePointerFeedback();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.Handled || IsInteracting || _session.SelectedLayerIds.Count == 0 ||
            e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        var moved = e.Key switch
        {
            Key.Left => _session.NudgeSelection(-step, 0, MoveWholeGroup),
            Key.Right => _session.NudgeSelection(step, 0, MoveWholeGroup),
            Key.Up => _session.NudgeSelection(0, -step, MoveWholeGroup),
            Key.Down => _session.NudgeSelection(0, step, MoveWholeGroup),
            _ => false
        };
        if (e.Key == Key.Delete)
        {
            _session.DeleteLayers(_session.SelectedLayerIds.ToArray());
            moved = true;
        }
        if (!moved && e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        ClearSnapGuides();
        Changed?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _hoverModifiers = e.KeyModifiers;
        UpdatePointerFeedback();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!ValidateInteraction()) return;
        if (IsInteracting || Bounds.Width <= 0 || Bounds.Height <= 0 || e.Delta.Y == 0) return;
        var pointer = e.GetPosition(this);
        _hoverPosition = pointer;
        _hoverModifiers = e.KeyModifiers;
        ZoomBy(Math.Pow(1.1, e.Delta.Y), pointer);
        e.Handled = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // A capture-cancel or pointer-exit callback during detach must not recreate native cursors.
        _feedbackDetached = true;
        CancelInteraction();
        _hoverPosition = null;
        ToolTip.SetIsOpen(this, false);
        Cursor = null;
        foreach (var cursor in _cursors.Values) cursor.Dispose();
        _cursors.Clear();
        _imageCache.Clear();
        _backgroundCache.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private sealed class SceneCanvasPeer(SceneCanvas owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Pane;
        protected override string GetClassNameCore() => "SceneCanvas";
        protected override string GetNameCore() =>
            AutomationProperties.GetName(owner) is { Length: > 0 } name ? name : "画布";
        protected override string GetHelpTextCore() =>
            AutomationProperties.GetHelpText(owner) ??
            "方向键微调所选图层，Shift 加大步进；F6 切换编辑区域焦点。";
        protected override bool IsKeyboardFocusableCore() => owner.Focusable;
        protected override bool HasKeyboardFocusCore() => owner.IsFocused;
        protected override void SetFocusCore() => owner.Focus();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _feedbackDetached = false;
        // Cursor=null inherits the normal pointer until the next input lazily rebuilds feedback.
    }

    public void Refresh()
    {
        ValidateInteraction();
        if (_guideCanvasWidth != _session.Document.CanvasWidth ||
            _guideCanvasHeight != _session.Document.CanvasHeight)
        {
            _guideCanvasWidth = _session.Document.CanvasWidth;
            _guideCanvasHeight = _session.Document.CanvasHeight;
            _manualGuides.Prune(_guideCanvasWidth, _guideCanvasHeight);
            ViewChanged?.Invoke();
        }
        UpdatePointerFeedback();
        InvalidateVisual();
    }

    public void FitView()
    {
        ValidateInteraction();
        if (IsInteracting) return;
        _zoom = 1;
        _pan = default;
        NotifyViewChanged();
    }

    public bool ZoomIn() => ZoomBy(ZoomStep, ViewCentre());

    public bool ZoomOut() => ZoomBy(1 / ZoomStep, ViewCentre());

    public bool ZoomBy(double factor, Point? anchor = null)
    {
        ValidateInteraction();
        if (IsInteracting || Bounds.Width <= 0 || Bounds.Height <= 0 ||
            !double.IsFinite(factor) || factor <= 0)
            return false;
        return SetRelativeZoom(_zoom * factor, anchor ?? ViewCentre());
    }

    public bool ZoomToActualSize()
    {
        ValidateInteraction();
        if (IsInteracting || Bounds.Width <= 0 || Bounds.Height <= 0) return false;
        var fitScale = BaseFitScale();
        return fitScale > 0 && SetRelativeZoom(1 / fitScale, ViewCentre());
    }

    public bool ZoomToSelection()
    {
        ValidateInteraction();
        if (IsInteracting || Bounds.Width <= 0 || Bounds.Height <= 0 ||
            _session.SelectedLayerIds.Count == 0)
            return false;
        var bounds = MoveSnapping.BoundsOf(_session.SelectedLayerIds
            .Select(id => LayerGeometry.VisualBounds(_session.Document.Find(id))));
        if (!double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
            bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        var padding = Math.Min(SelectionPaddingDips, Math.Min(Bounds.Width, Bounds.Height) / 4);
        var availableWidth = Math.Max(1, Bounds.Width - padding * 2);
        var availableHeight = Math.Max(1, Bounds.Height - padding * 2);
        var fitScale = BaseFitScale();
        if (fitScale <= 0) return false;
        var targetScale = Math.Min(availableWidth / bounds.Width, availableHeight / bounds.Height);
        _zoom = Math.Clamp(targetScale / fitScale, MinimumRelativeZoom, MaximumRelativeZoom);

        var centered = new ViewportTransform(_session.Document.CanvasWidth,
            _session.Document.CanvasHeight, Bounds.Width, Bounds.Height, _zoom);
        var centre = LayerGeometry.Centre(bounds);
        var mapped = centered.ToView(centre);
        _pan = new PointD(
            Bounds.Width / 2 - mapped.X,
            Bounds.Height / 2 - mapped.Y);
        NotifyViewChanged();
        return true;
    }

    /// <summary>
    /// Starts a ruler drag. Coordinates are supplied in document space by the ruler while the
    /// pointer moves; the guide is only committed when released inside the canvas bounds.
    /// </summary>
    public bool BeginManualGuideDraft(bool vertical)
    {
        ValidateInteraction();
        if (IsInteracting || Bounds.Width <= 0 || Bounds.Height <= 0) return false;
        _guideDraftActive = true;
        _guideDraftVertical = vertical;
        _guideDraftPosition = null;
        CaptureInteractionView();
        InvalidateVisual();
        return true;
    }

    public void UpdateManualGuideDraft(PointD logical)
    {
        if (!_guideDraftActive) return;
        var limit = _guideDraftVertical ? _session.Document.CanvasWidth :
            _session.Document.CanvasHeight;
        var value = _guideDraftVertical ? logical.X : logical.Y;
        _guideDraftPosition = double.IsFinite(value) && value >= 0 && value <= limit
            ? value : null;
        InvalidateVisual();
    }

    public bool CommitManualGuideDraft(PointD logical)
    {
        if (!_guideDraftActive) return false;
        UpdateManualGuideDraft(logical);
        var added = _guideDraftPosition is double position &&
            _manualGuides.Add(_guideDraftVertical, position,
                _session.Document.CanvasWidth, _session.Document.CanvasHeight) ==
            ManualGuideAddResult.Added;
        CancelManualGuideDraft();
        return added;
    }

    public void CancelManualGuideDraft(bool invalidate = true)
    {
        if (!_guideDraftActive) return;
        _guideDraftActive = false;
        _guideDraftPosition = null;
        if (!_editing && !_panning && !_selecting && !_guideDragging)
        {
            _interactionDocument = null;
            _interactionView = null;
        }
        UpdatePointerFeedback();
        if (invalidate) InvalidateVisual();
    }

    public int ClearManualGuides()
    {
        if (_guideDragging) EndInteraction();
        CancelManualGuideDraft();
        return _manualGuides.Clear();
    }

    private void DrawManualGuides(DrawingContext context, ViewportTransform transform)
    {
        if (_manualGuides.Count == 0 && _guideDraftPosition is null &&
            !(_guideDragging && _guideDragPosition is not null))
            return;
        var page = transform.ToView(new RectD(0, 0,
            _session.Document.CanvasWidth, _session.Document.CanvasHeight));
        var brush = new SolidColorBrush(Color.FromArgb(235, 255, 170, 60));
        var pen = new Pen(brush, 1.2);
        for (var index = 0; index < _manualGuides.Count; index++)
        {
            if (_guideDragging && index == _guideDragIndex) continue;
            DrawManualGuide(context, transform, page, _manualGuides.Items[index], pen, brush);
        }
        if (_guideDragging && _guideDragPosition is double dragged &&
            _guideDragIndex >= 0 && _guideDragIndex < _manualGuides.Count)
        {
            DrawManualGuide(context, transform, page,
                new ManualGuide(_manualGuides.Items[_guideDragIndex].Vertical, dragged), pen, brush);
        }
        if (_guideDraftPosition is double draft)
            DrawManualGuide(context, transform, page,
                new ManualGuide(_guideDraftVertical, draft), pen, brush);
    }

    private void DrawSafeArea(DrawingContext context, ViewportTransform transform)
    {
        var page = transform.ToView(new RectD(0, 0,
            _session.Document.CanvasWidth, _session.Document.CanvasHeight));
        var inset = Math.Min(page.Width, page.Height) * _safeAreaMarginPercent / 100;
        if (inset <= 0 || page.Width <= inset * 2 || page.Height <= inset * 2) return;
        context.DrawRectangle(null,
            new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 210, 70)), 1.2),
            new Rect(page.X + inset, page.Y + inset,
                page.Width - inset * 2, page.Height - inset * 2));
    }

    private static void DrawManualGuide(DrawingContext context, ViewportTransform transform,
        RectD page, ManualGuide guide, Pen pen, IBrush brush)
    {
        if (guide.Vertical)
        {
            var point = transform.ToView(new PointD(guide.Position, 0));
            context.DrawLine(pen, new(point.X, page.Y), new(point.X, page.Y + page.Height));
            context.FillRectangle(brush, new Rect(point.X - 3, page.Y, 6, 3));
        }
        else
        {
            var point = transform.ToView(new PointD(0, guide.Position));
            context.DrawLine(pen, new(page.X, point.Y), new(page.X + page.Width, point.Y));
            context.FillRectangle(brush, new Rect(page.X, point.Y - 3, 3, 6));
        }
    }

    private (int Index, ManualGuide Guide)? HitManualGuide(Point position)
    {
        if (_manualGuides.Count == 0) return null;
        var transform = ViewTransform();
        var page = transform.ToView(new RectD(0, 0,
            _session.Document.CanvasWidth, _session.Document.CanvasHeight));
        var pageRect = new Rect(page.X, page.Y, page.Width, page.Height);
        if (!pageRect.Contains(position)) return null;
        var tolerance = 6 / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        var bestDistance = double.PositiveInfinity;
        var bestIndex = -1;
        for (var index = 0; index < _manualGuides.Count; index++)
        {
            var guide = _manualGuides.Items[index];
            var view = guide.Vertical
                ? transform.ToView(new PointD(guide.Position, 0)).X
                : transform.ToView(new PointD(0, guide.Position)).Y;
            var distance = Math.Abs((guide.Vertical ? position.X : position.Y) - view);
            if (distance <= tolerance && distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }
        return bestIndex < 0 ? null : (bestIndex, _manualGuides.Items[bestIndex]);
    }

    private void UpdateManualGuideDrag(PointD logical)
    {
        if (!_guideDragging || _guideDragIndex < 0 || _guideDragIndex >= _manualGuides.Count)
            return;
        var guide = _manualGuides.Items[_guideDragIndex];
        var limit = guide.Vertical ? _session.Document.CanvasWidth :
            _session.Document.CanvasHeight;
        var value = guide.Vertical ? logical.X : logical.Y;
        _guideDragPosition = double.IsFinite(value) && value >= 0 && value <= limit
            ? value : null;
    }

    private void CommitManualGuideDrag()
    {
        if (!_guideDragging) return;
        if (_guideDragIndex >= 0 && _guideDragIndex < _manualGuides.Count)
        {
            if (_guideDragPosition is double position)
                _manualGuides.Move(_guideDragIndex, position,
                    _session.Document.CanvasWidth, _session.Document.CanvasHeight);
            else
                _manualGuides.RemoveAt(_guideDragIndex);
        }
        _guideDragPosition = null;
    }

    private (Guid Id, RectD Bounds, int TargetCount, double RotationDegrees)? ResizeSelection()
    {
        if (_editing && _session.ActiveGestureIds.Count > 0)
            return SelectionGeometry(_session.ActiveGestureIds[0], _session.ActiveGestureIds);
        var primary = _session.SelectedLayerIds.FirstOrDefault(id =>
            _session.Document.Find(id) is { Visible: true, Locked: false });
        if (primary == Guid.Empty) return null;
        var targets = _session.GestureTargets(primary, includeGroup: MoveWholeGroup);
        return SelectionGeometry(primary, targets);
    }

    private (Guid Id, RectD Bounds, int TargetCount, double RotationDegrees) SelectionGeometry(
        Guid primary, IReadOnlyList<Guid> targets)
    {
        var layer = _session.Document.Find(primary);
        return targets.Count == 1 ? (primary, layer.Bounds, 1, layer.RotationDegrees) :
            (primary, MoveSnapping.BoundsOf(targets.Select(id =>
                LayerGeometry.VisualBounds(_session.Document.Find(id)))), targets.Count, 0);
    }

    private void DrawSelection(DrawingContext context, RectD box, double angle, Pen pen)
    {
        var corners = LayerGeometry.Corners(box, angle).Select(ViewTransform().ToView).ToArray();
        for (var index = 0; index < corners.Length; index++)
        {
            var from = corners[index]; var to = corners[(index + 1) % corners.Length];
            context.DrawLine(pen, new(from.X, from.Y), new(to.X, to.Y));
        }
    }

    private Point RotationHandle(RectD box, double angle)
    {
        var transform = ViewTransform();
        var point = LayerGeometry.Rotate(new PointD(box.X + box.Width / 2,
            box.Y - 24 / transform.Scale), LayerGeometry.Centre(box), angle);
        var view = transform.ToView(point);
        return new(view.X, view.Y);
    }

    private (bool Rotating, ResizeHandle Handle)? HitHandle(Point position, RectD box, double angle)
    {
        // Input and feedback share the same physical-pixel tolerance and hit priority.
        var tolerance = 8 / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        if (Near(position, RotationHandle(box, angle), tolerance)) return (true, default);
        foreach (var handle in Enum.GetValues<ResizeHandle>())
        {
            var point = ViewTransform().ToView(LayerGeometry.HandlePoint(box, handle, angle));
            if (Near(position, new(point.X, point.Y), tolerance)) return (false, handle);
        }
        return null;
    }

    private void UpdatePointerFeedback()
    {
        if (_panning)
        {
            SetPointerFeedback(StandardCursorType.Hand, "平移画布；Esc 取消。");
            return;
        }
        if (_selecting)
        {
            SetPointerFeedback(StandardCursorType.Cross, "框选图层；Ctrl / Shift 添加选择；Esc 取消。");
            return;
        }
        if (_guideDragging)
        {
            var vertical = _guideDragIndex >= 0 && _guideDragIndex < _manualGuides.Count &&
                _manualGuides.Items[_guideDragIndex].Vertical;
            SetPointerFeedback(vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth,
                "拖动辅助线；拖回标尺或画布外可删除；Esc 取消。");
            return;
        }
        if (_editing)
        {
            if (_rotating)
                SetPointerFeedback(StandardCursorType.Hand, "旋转选中图层；Shift 按 15° 对齐；Esc 取消。");
            else if (_resizing)
                SetResizeFeedback(_resizeHandle, _resizeStartRotation, _session.ActiveGestureIds.Count);
            else SetMoveFeedback(_session.ActiveGestureIds.Count);
            return;
        }
        if (_hoverPosition is not Point position || Bounds.Width <= 0 || Bounds.Height <= 0 ||
            !new Rect(Bounds.Size).Contains(position))
        {
            SetPointerFeedback(StandardCursorType.Arrow, "拖动空白处框选；中键平移；滚轮缩放。");
            return;
        }
        if (!_hoverModifiers.HasFlag(KeyModifiers.Control) && ResizeSelection() is { } resize &&
            HitHandle(position, resize.Bounds, resize.RotationDegrees) is { } hit)
        {
            if (hit.Rotating)
                SetPointerFeedback(StandardCursorType.Hand, "拖动圆点旋转；Shift 按 15° 对齐。");
            else SetResizeFeedback(hit.Handle, resize.RotationDegrees, resize.TargetCount);
            return;
        }
        if (!_hoverModifiers.HasFlag(KeyModifiers.Control) &&
            HitManualGuide(position) is { } manualGuide)
        {
            SetPointerFeedback(manualGuide.Guide.Vertical
                    ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth,
                "拖动辅助线；拖回标尺或画布外可删除。");
            return;
        }
        var logical = ViewTransform().ToLogical(new(position.X, position.Y));
        var layer = _session.Document.Layers.AsEnumerable().Reverse().FirstOrDefault(layer =>
            layer.Visible && !layer.Locked && LayerGeometry.Contains(layer, logical));
        if (layer is not null)
        {
            var count = _session.GestureTargets(layer.Id, includeGroup: MoveWholeGroup).Count;
            if (count == 1 && layer.Kind == LayerKind.Text)
                SetPointerFeedback(StandardCursorType.SizeAll, "拖动移动文字；双击编辑文字；Shift 锁定方向；Alt 关闭吸附。");
            else SetMoveFeedback(count);
            return;
        }
        var locked = _session.Document.Layers.Any(layer => layer.Visible && layer.Locked &&
            LayerGeometry.Contains(layer, logical));
        SetPointerFeedback(locked ? StandardCursorType.No : StandardCursorType.Arrow,
            locked ? "图层已锁定，解锁后可移动或缩放。" : "拖动空白处框选；中键平移；滚轮缩放。");
    }

    private void SetMoveFeedback(int count) => SetPointerFeedback(StandardCursorType.SizeAll,
        (count > 1 ? $"移动 {count} 个联动图层；" : "拖动移动图层；") +
        "Shift 锁定方向；Alt 关闭吸附；Esc 取消。");

    private void SetResizeFeedback(ResizeHandle handle, double angle, int count)
    {
        var direction = handle switch
        {
            ResizeHandle.TopLeft => "左上角", ResizeHandle.Top => "上边",
            ResizeHandle.TopRight => "右上角", ResizeHandle.Right => "右边",
            ResizeHandle.BottomRight => "右下角", ResizeHandle.Bottom => "下边",
            ResizeHandle.BottomLeft => "左下角", _ => "左边"
        };
        // A cursor represents an undirected screen axis, quantized to Windows' four resize axes.
        var baseAngle = handle switch
        {
            ResizeHandle.Top or ResizeHandle.Bottom => 90,
            ResizeHandle.TopLeft or ResizeHandle.BottomRight => 45,
            ResizeHandle.TopRight or ResizeHandle.BottomLeft => 135,
            _ => 0
        };
        var axis = ((int)Math.Floor((baseAngle + angle) / 45 + 0.5) % 4 + 4) % 4;
        var cursor = axis switch
        {
            0 => StandardCursorType.SizeWestEast, 1 => StandardCursorType.TopLeftCorner,
            2 => StandardCursorType.SizeNorthSouth, _ => StandardCursorType.TopRightCorner
        };
        var subject = count > 1 ? $"等比缩放 {count} 个联动图层" : $"拖动{direction}缩放（方向随图层旋转）";
        SetPointerFeedback(cursor, subject + "；Shift 临时等比；Alt 关闭吸附；Esc 取消。");
    }

    private void SetPointerFeedback(StandardCursorType type, string hint)
    {
        if (_feedbackDetached) return;
        if (!_cursors.TryGetValue(type, out var cursor))
            _cursors[type] = cursor = new Cursor(type);
        if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
        if (!Equals(ToolTip.GetTip(this), hint)) ToolTip.SetTip(this, hint);
        if (IsInteracting || _hoverPosition is null) ToolTip.SetIsOpen(this, false);
    }

    private static bool Near(Point first, Point second, double tolerance) =>
        Math.Abs(first.X - second.X) <= tolerance && Math.Abs(first.Y - second.Y) <= tolerance;

    private void ClearSnapGuides()
    {
        _verticalGuide = null;
        _horizontalGuide = null;
    }

    private ManualGuideSet BuildSnapGuideSet()
    {
        var guides = new ManualGuideSet();
        foreach (var guide in _manualGuides.Items)
            guides.Add(guide.Vertical, guide.Position,
                _session.Document.CanvasWidth, _session.Document.CanvasHeight);
        if (ShowSafeArea && _safeAreaMarginPercent > 0)
        {
            var width = _session.Document.CanvasWidth;
            var height = _session.Document.CanvasHeight;
            var margin = Math.Min(width, height) * _safeAreaMarginPercent / 100;
            if (margin > 0 && margin * 2 < Math.Min(width, height))
            {
                guides.Add(true, margin, width, height);
                guides.Add(true, width - margin, width, height);
                guides.Add(false, margin, width, height);
                guides.Add(false, height - margin, width, height);
            }
        }
        return guides;
    }

    private Point ToView(double x, double y)
    {
        var point = ViewTransform().ToView(new PointD(x, y));
        return new Point(point.X, point.Y);
    }

    private void CaptureInteractionView()
    {
        _interactionDocument = _session.Document;
        _interactionView = new(_session.Document.CanvasWidth, _session.Document.CanvasHeight,
            Bounds.Width, Bounds.Height, _zoom, _pan);
    }

    private bool ValidateInteraction()
    {
        if (_guideDraftActive)
        {
            if (ReferenceEquals(_interactionDocument, _session.Document) &&
                _interactionView is { } draftView &&
                draftView.CanvasWidth == _session.Document.CanvasWidth &&
                draftView.CanvasHeight == _session.Document.CanvasHeight) return true;
            CancelManualGuideDraft(invalidate: false);
            return false;
        }
        if (!IsInteracting) return true;
        if (ReferenceEquals(_interactionDocument, _session.Document) && _interactionView is { } view &&
            view.CanvasWidth == _session.Document.CanvasWidth && view.CanvasHeight == _session.Document.CanvasHeight &&
            (!_editing || _session.ActiveGestureIds.Count > 0)) return true;
        CancelInteraction(invalidate: false);
        return false;
    }

    private void CancelInteraction(bool invalidate = true)
    {
        if (_guideDraftActive) CancelManualGuideDraft(invalidate);
        if (!IsInteracting) return;
        var sameDocument = ReferenceEquals(_interactionDocument, _session.Document);
        var contextChanged = !sameDocument || _interactionView is not { } view ||
            view.CanvasWidth != _session.Document.CanvasWidth || view.CanvasHeight != _session.Document.CanvasHeight;
        var selectionBefore = _session.SelectedLayerIds.ToArray();
        if (_panning) _pan = _panAtPress;
        if (sameDocument && _selecting)
            _session.SetSelection(_selectionAtPress.Where(id => _session.Document.Layers.Any(layer => layer.Id == id)));
        if (sameDocument && _editing) _session.CancelGesture();
        EndInteraction(invalidate);
        // Gesture previews never refresh the host fields. Restoring their start model, or
        // cancelling a pan, must not overwrite inputs that the user has not applied.
        if (contextChanged) Changed?.Invoke();
        else NotifySelectionChanged(selectionBefore);
    }

    private void NotifySelectionChanged(IReadOnlyList<Guid> before)
    {
        if (!before.SequenceEqual(_session.SelectedLayerIds)) Changed?.Invoke();
    }

    private void EndInteraction(bool invalidate = true)
    {
        var pointer = _capturedPointer;
        _editing = false;
        _resizing = false;
        _rotating = false;
        _moveAxis = MoveAxis.None;
        _panning = false;
        _selecting = false;
        _selectionBox = null;
        _selectionAtPress = [];
        _guideDragging = false;
        _guideDragIndex = -1;
        _guideDragPosition = null;
        _interactionDocument = null;
        _interactionView = null;
        _capturedPointer = null;
        ClearSnapGuides();
        // A lost-capture notification must not take capture away from its new owner.
        if (ReferenceEquals(pointer?.Captured, this)) pointer.Capture(null);
        UpdatePointerFeedback();
        if (invalidate) InvalidateVisual();
    }

    private void OnDocumentReplaced()
    {
        CancelManualGuideDraft();
        _manualGuides.Clear();
        NotifyViewChanged();
    }

    private bool SetRelativeZoom(double relativeZoom, Point anchor)
    {
        var clamped = Math.Clamp(relativeZoom, MinimumRelativeZoom, MaximumRelativeZoom);
        if (!double.IsFinite(clamped) || Math.Abs(clamped - _zoom) < 1e-9) return false;
        var before = ViewTransform();
        var logical = before.ToLogical(new PointD(anchor.X, anchor.Y));
        _zoom = clamped;
        var after = ViewTransform();
        var mapped = after.ToView(logical);
        _pan = new PointD(
            _pan.X + anchor.X - mapped.X,
            _pan.Y + anchor.Y - mapped.Y);
        NotifyViewChanged();
        return true;
    }

    private Point ViewCentre() => new(Bounds.Width / 2, Bounds.Height / 2);

    private double BaseFitScale() => Math.Min(
        Bounds.Width / _session.Document.CanvasWidth,
        Bounds.Height / _session.Document.CanvasHeight);

    private void NotifyViewChanged()
    {
        ViewChanged?.Invoke();
        UpdatePointerFeedback();
        InvalidateVisual();
    }

    private ViewportTransform ViewTransform() => IsInteracting && _interactionView is { } view
        ? new(view.CanvasWidth, view.CanvasHeight, view.Width, view.Height, view.Zoom, _panning ? _pan : view.Pan)
        : new(_session.Document.CanvasWidth, _session.Document.CanvasHeight, Bounds.Width, Bounds.Height, _zoom, _pan);
}
