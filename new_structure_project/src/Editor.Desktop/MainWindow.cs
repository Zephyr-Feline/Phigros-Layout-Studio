using System.Globalization;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia.Controls.Templates;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Formats.Compatibility;
using Editor.Rendering;

namespace Editor.Desktop;

public sealed class MainWindow : Window
{
    private const double RulerSize = 24;
    private readonly EditSession _session;
    private readonly NativeProjectWorkflow _native;
    private readonly PngExportWorkflow _export;
    private readonly LayerClipboard _layerClipboard;
    private readonly EditorPreferencesStore _preferencesStore;
    private EditorPreferences _preferences;
    private readonly string? _preferencesWarning;
    private readonly ExportPresetLibrary _exportPresetLibrary;
    private readonly string? _exportPresetWarning;
    private readonly ProtectedSourcePaths _importPaths = new();
    private readonly SceneCanvas _canvas;
    private readonly LocalFontLibrary _fonts;
    private readonly RecentProjectStore _recentProjects;
    private readonly AutoRecoveryService _recovery;
    private readonly Avalonia.Threading.DispatcherTimer _recoveryTimer;
    private readonly bool _autoRecoveryEnabled;
    private bool _recoveryCaptureInProgress;
    private bool _recoveryOfferShown;
    private MenuItem? _recentMenu;
    private readonly TextBlock _recoveryNotice = new()
        { Name = "RecoveryNotice", TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 120)), IsVisible = false };
    private readonly ComboBox _localFontChoices = new() { Name = "LocalFontChoices" };
    private readonly TextBlock _fontNotice = new() { Name = "FontNotice", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _conversionDetails = new()
    {
        TextWrapping = TextWrapping.Wrap, IsVisible = false, FontSize = 11, MaxHeight = 140
    };
    private readonly TextBlock _conversionHeader = new()
    {
        Text = "导入转换详情", IsVisible = false, FontWeight = FontWeight.SemiBold
    };
    private readonly ProjectOpenWorkflow _projects;
    private readonly Func<Task<string?>>? _nativeSavePathPicker;
    private readonly Func<Task<string?>>? _projectLayerPathPicker;
    private readonly Func<Task<string?>>? _selectionProjectPathPicker;
    private readonly Func<Task<IReadOnlyList<string>>>? _songImagePathsPicker;
    private readonly Func<Task<IReadOnlyList<string>>>? _batchImagePathsPicker;
    private readonly Func<Task<IReadOnlyList<string>>>? _songGridPathsPicker;
    private readonly Func<Task<IReadOnlyList<string>>>? _batchProjectPathsPicker;
    private readonly Func<Task<string?>>? _exportDirectoryPicker;
    private readonly Func<EditorFileAction, Task<string?>>? _filePathPicker;
    private readonly Func<string, Task<LoadedImage>> _imageLoader;
    private readonly CheckBox _showRulers = new()
        { Name = "ShowRulers", Content = "显示标尺", IsChecked = true };
    private readonly TextBox _guideX = new() { Name = "GuideX" };
    private readonly TextBox _guideY = new() { Name = "GuideY" };
    private readonly TextBlock _guideStatus = new()
        { Name = "GuideStatus", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private ColumnDefinition? _rulerColumn;
    private RowDefinition? _rulerRow;
    private Border? _rulerCorner;
    private CanvasRuler? _topRuler;
    private CanvasRuler? _leftRuler;
    private MenuItem? _toggleRulersMenu;
    private readonly TextBlock _zoomStatus = new()
    {
        Name = "ZoomStatus",
        Text = "缩放 --",
        MinWidth = 74,
        Margin = new Thickness(8, 0),
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };
    private bool _fileOperationPending;
    private bool _buildingSongBatch;
    private bool _importingImageBatch;
    private bool _buildingSongGrid;
    private bool _editingSongInfo;
    private bool _batchRendering;
    private bool _savingSelectionProject;
    private bool _importingProjectLayers;
    private bool _allowClose;
    private bool _closingPrompt;
    private bool _savingNative;
    private bool _exportingPng;
    private bool _editingText;
    private KeyEventArgs? _composingPropertyEnter;
    private readonly ListBox _layers = new() { Name = "LayerList" };
    private readonly Grid _layerListHost = new() { Name = "LayerListHost", ClipToBounds = true };
    private readonly Border _layerDropIndicator = new()
    {
        Name = "LayerDropIndicator",
        Height = 2,
        Background = new SolidColorBrush(Color.Parse("#FFB347")),
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        IsHitTestVisible = false,
        IsVisible = false
    };
    private readonly ObservableCollection<LayerListItem> _layerItems = [];
    private bool _updatingLayerList;
    private Control? _lastPropertyFocus;
    private Control? _lastRulerFocus;
    private readonly Expander _layerPropertiesExpander = new()
    {
        Name = "LayerProperties",
        Header = "所选图层属性",
        IsExpanded = true
    };
    private LayerPointerPress? _layerPointerPress;
    private IPointer? _layerDragPointer;
    private Guid[] _layerDragIds = [];
    private Guid? _layerDragTargetId;
    private bool _layerDragAbove;
    private readonly StackPanel _groupCardsHost = new() { Name = "GroupOverviewCards", Spacing = 4 };
    private Guid? _selectedGroupId;
    private readonly HashSet<Guid> _expandedGroups = [];
    private readonly Dictionary<Guid, Button> _groupHeaderButtons = [];
    private readonly Button _selectGroupButton = new()
        { Name = "SelectGroupOverview", Content = "选择整组", Focusable = false };
    private readonly Button _toggleGroupVisibilityButton = new()
        { Name = "ToggleGroupOverviewVisibility", Content = "隐藏整组", Focusable = false };
    private readonly Button _toggleGroupLockButton = new()
        { Name = "ToggleGroupOverviewLock", Content = "锁定整组", Focusable = false };
    private readonly Button _ungroupButton = new()
        { Name = "UngroupGroupOverview", Content = "取消整组编组", Focusable = false };
    private Expander? _groupOverviewExpander;
    private sealed record GroupListItem(Guid GroupId, string Label)
    {
        public override string ToString() => Label;
    }
    private readonly TextBox _layerSearch = new()
        { Name = "LayerSearch", PlaceholderText = "查找图层或文字", Height = 32, MinHeight = 32,
            Padding = new Thickness(8, 4), VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly TextBlock _layerMatchInfo = new()
        { Name = "LayerMatchInfo", Text = "输入名称或文字内容进行查找。", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly Button _previousLayerMatch = new()
        { Name = "PreviousLayerMatch", Content = "上一项", IsEnabled = false, Focusable = false,
            Height = 32, MinHeight = 32, Padding = new Thickness(5, 0), FontSize = 11,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly Button _nextLayerMatch = new()
        { Name = "NextLayerMatch", Content = "下一项", IsEnabled = false, Focusable = false,
            Height = 32, MinHeight = 32, Padding = new Thickness(5, 0), FontSize = 11,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly Button _selectAllLayerMatches = new()
        { Name = "SelectAllLayerMatches", Content = "选中全部匹配项", IsEnabled = false, Focusable = false };
    private readonly CheckBox _filterLayerMatches = new()
        { Name = "FilterLayerMatches", Content = "仅显示匹配项", IsEnabled = false };
    private readonly TextBlock _layerFilterNotice = new()
        { Name = "LayerFilterNotice", TextWrapping = TextWrapping.Wrap, FontSize = 11, IsVisible = false };
    private readonly TextBlock _layerSelectionSummary = new()
        { Name = "LayerSelectionSummary", Text = "未选择图层", TextWrapping = TextWrapping.Wrap,
            FontSize = 11, Margin = new Thickness(6, 0, 0, 0), TextAlignment = TextAlignment.Right,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly StackPanel _layerSearchFeedback = new() { Spacing = 2, IsVisible = false };
    private Guid[] _layerMatches = [];
    private int _layerMatchCursor = -1;
    private sealed record LayerListItem(Guid Id, string Label, bool Visible, bool Locked, bool IsMatch)
    {
        public override string ToString() => Label;
    }
    private enum FocusZone { Canvas, Layers, Properties, Rulers }
    private sealed record LayerPointerPress(IPointer Pointer, Point Origin, Guid[] SelectedIds);
    private readonly TextBox _text = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 100,
        Name = "TextContent", Text = "双击编辑文字", PlaceholderText = "输入文字，可换行"
    };
    private readonly TextBox _propertyName = new() { Name = "LayerName" };
    private readonly TextBox _propertyX = new() { Name = "LayerX" };
    private readonly TextBox _propertyY = new() { Name = "LayerY" };
    private readonly TextBox _propertyWidth = new() { Name = "LayerWidth" };
    private readonly TextBox _propertyHeight = new() { Name = "LayerHeight" };
    private readonly TextBox _propertyOpacity = new() { Name = "LayerOpacity" };
    private readonly TextBox _propertyRotation = new() { Name = "LayerRotation" };
    private readonly CheckBox _propertyKeepAspect = new()
        { Name = "LayerKeepAspect", Content = "保持宽高比" };
    private readonly CheckBox _propertySlanted = new()
        { Name = "LayerSlanted", Content = new TextBlock
            { Text = "斜切边缘（图片裁切填充）", TextWrapping = TextWrapping.Wrap } };
    private readonly Button _openImageCropButton = new()
        { Name = "OpenImageCrop", Content = "裁切…", Focusable = false, FontSize = 11 };
    private enum DimensionDriver { None, Width, Height }
    private DimensionDriver _dimensionDriver;
    private bool _refreshingPropertyDimensions;
    private readonly TextBox _propertyColor = new() { Name = "LayerColor" };
    private readonly TextBox _propertyFont = new() { Name = "LayerFontFamily" };
    private readonly TextBox _propertyFontSize = new() { Name = "LayerFontSize" };
    private readonly TextBox _propertyLineSpacing = new() { Name = "LayerLineSpacing" };
    private readonly TextBox _propertyLevel = new() { Name = "LayerLevel" };
    private readonly CheckBox _propertyBold = new() { Name = "LayerBold", Content = "粗体" };
    private readonly CheckBox _propertyItalic = new() { Name = "LayerItalic", Content = "斜体" };
    private readonly ComboBox _propertyAlignment = new() { Name = "LayerAlignment", ItemsSource = Enum.GetValues<HorizontalAlignment>() };
    private readonly ComboBox _propertyVerticalAlignment = new()
        { Name = "LayerVerticalAlignment", ItemsSource = Enum.GetValues<Editor.Core.VerticalAlignment>() };
    private readonly ComboBox _propertyDifficulty = new() { Name = "LayerDifficulty", ItemsSource = new[] { "EZ", "HD", "IN", "AT" } };
    private readonly StackPanel _textPropertyFields = new() { Spacing = 4 };
    private readonly StackPanel _badgePropertyFields = new() { Spacing = 4 };
    private readonly StackPanel _colorPropertyFields = new() { Spacing = 4 };
    private readonly StackPanel _propertyPanel = new() { Spacing = 4, IsVisible = false };
    private readonly CheckBox _moveWholeGroup = new() { Name = "EditWholeGroup", Content = "整组编辑" };
    private readonly Expander _batchStyleSection = new()
        { Name = "BatchStyleSection", Header = "批量样式（多选）", IsExpanded = false, IsVisible = false };
    private readonly TextBlock _batchTargetInfo = new()
        { Name = "BatchTargetInfo", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock _batchMessage = new()
        { Name = "BatchMessage", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly StackPanel _batchTextFields = new() { Spacing = 4 };
    private readonly StackPanel _batchBadgeFields = new() { Spacing = 4 };
    private readonly CheckBox _batchApplyFont = new() { Name = "BatchApplyFont", Content = "应用字体" };
    private readonly TextBox _batchFont = new() { Name = "BatchFont" };
    private readonly CheckBox _batchApplyFontSize = new() { Name = "BatchApplyFontSize", Content = "应用字号" };
    private readonly TextBox _batchFontSize = new() { Name = "BatchFontSize" };
    private readonly CheckBox _batchApplyColor = new() { Name = "BatchApplyColor", Content = "应用颜色" };
    private readonly TextBox _batchColor = new() { Name = "BatchColor" };
    private readonly CheckBox _batchApplyBold = new() { Name = "BatchApplyBold", Content = "应用粗体" };
    private readonly ComboBox _batchBold = new() { Name = "BatchBold", ItemsSource = new[] { true, false } };
    private readonly CheckBox _batchApplyItalic = new() { Name = "BatchApplyItalic", Content = "应用斜体" };
    private readonly ComboBox _batchItalic = new() { Name = "BatchItalic", ItemsSource = new[] { true, false } };
    private readonly CheckBox _batchApplyLineSpacing = new() { Name = "BatchApplyLineSpacing", Content = "应用行距" };
    private readonly TextBox _batchLineSpacing = new() { Name = "BatchLineSpacing" };
    private readonly CheckBox _batchApplyAlignment = new() { Name = "BatchApplyAlignment", Content = "应用水平对齐" };
    private readonly ComboBox _batchAlignment = new()
        { Name = "BatchAlignment", ItemsSource = Enum.GetValues<Editor.Core.HorizontalAlignment>() };
    private readonly CheckBox _batchApplyVertical = new() { Name = "BatchApplyVertical", Content = "应用垂直对齐" };
    private readonly ComboBox _batchVertical = new()
        { Name = "BatchVertical", ItemsSource = Enum.GetValues<Editor.Core.VerticalAlignment>() };
    private readonly CheckBox _batchApplyDifficulty = new() { Name = "BatchApplyDifficulty", Content = "应用难度类型" };
    private readonly ComboBox _batchDifficulty = new()
        { Name = "BatchDifficulty", ItemsSource = new[] { "EZ", "HD", "IN", "AT" } };
    private readonly CheckBox _batchApplyLevel = new() { Name = "BatchApplyLevel", Content = "应用难度等级" };
    private readonly TextBox _batchLevel = new() { Name = "BatchLevel" };
    private readonly Button _batchApplyButton = new()
        { Name = "ApplyBatchStyle", Content = "应用批量样式", IsEnabled = false };
    private BatchStyleContext? _batchStyleContext;
    private sealed record BatchStyleContext(EditorDocument Snapshot, Guid[] Ids, int Undo, int Redo);
    private readonly TextBox _canvasWidth = new() { Name = "CanvasWidth" };
    private readonly TextBox _canvasHeight = new() { Name = "CanvasHeight" };
    private readonly TextBox _backgroundColor = new() { Name = "BackgroundColor" };
    private readonly TextBlock _backgroundSource = new();
    private readonly CheckBox _automaticBackground = new()
        { Name = "AutomaticBackground", Content = "自动混合可见曲绘生成背景" };
    private readonly TextBox _backgroundBlur = new() { Name = "BackgroundBlur" };
    private readonly TextBox _backgroundDarkness = new() { Name = "BackgroundDarkness" };
    private readonly TextBox _backgroundRange = new() { Name = "BackgroundRange" };
    private readonly TextBox _backgroundTransition = new() { Name = "BackgroundTransition" };
    private readonly CheckBox _showBackgroundContributors = new()
        { Name = "ShowBackgroundContributors", Content = "在画布上高亮背景贡献曲绘" };
    private bool _updatingBackgroundFields;
    private readonly ComboBox _layoutTemplate = new()
    {
        Name = "LayoutTemplate", ItemsSource = LayoutTemplates.All, SelectedIndex = 0
    };
    private readonly TextBox _layoutName = new() { Name = "LayoutTemplateName", Text = "我的布局", MaxLength = 120 };
    private readonly TemplateLibrary _templateLibrary;
    private readonly SongStyleLibrary _songStyleLibrary;
    private readonly ListBox _templateLibraryList = new()
        { Name = "TemplateLibraryList", Height = 120, SelectionMode = SelectionMode.Single };
    private readonly ObservableCollection<TemplateLibraryEntry> _templateLibraryItems = [];
    private IReadOnlyList<TemplateLibraryEntry> _allTemplateEntries = [];
    private readonly TextBox _templateLibrarySearch = new()
        { Name = "TemplateLibrarySearch", PlaceholderText = "查找模板名称" };
    private readonly TextBlock _templateLibraryStatus = new()
        { Name = "TemplateLibraryStatus", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TemplatePreview _templatePreview = new() { Name = "TemplatePreview", Height = 90 };
    private readonly StackPanel _templateLibraryHost = new() { Spacing = 4 };
    private bool _templateLibraryPanelBuilt;
    private bool _editingSongStyle;
    private bool _editingBackgroundContribution;
    private bool _viewingResources;
    private MenuItem? _undoMenuItem;
    private MenuItem? _redoMenuItem;
    private Button? _undoButton;
    private readonly TextBlock _historySummary = new()
    {
        Name = "HistorySummary",
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11
    };
    private readonly TextBlock _status = new() { Name = "EditorStatus", Text = "就绪。白点缩放，上方蓝点旋转，双击文字编辑；拖动标尺创建辅助线；Shift 等比缩放，Alt 暂停吸附。", TextWrapping = TextWrapping.Wrap };

    public MainWindow(EditSession? session = null, LocalFontLibrary? fonts = null,
        NativeProjectWorkflow? nativeWorkflow = null, Func<Task<string?>>? nativeSavePathPicker = null,
        Func<Task<string?>>? projectLayerPathPicker = null, Func<Task<string?>>? selectionProjectPathPicker = null,
        Func<Task<IReadOnlyList<string>>>? songImagePathsPicker = null,
        Func<EditorFileAction, Task<string?>>? filePathPicker = null,
        Func<string, Task<LoadedImage>>? imageLoader = null,
        Func<Task<IReadOnlyList<string>>>? batchImagePathsPicker = null,
        Func<Task<string?>>? exportDirectoryPicker = null,
        TemplateLibrary? templateLibrary = null,
        RecentProjectStore? recentProjects = null,
        AutoRecoveryService? recovery = null,
        Func<Task<IReadOnlyList<string>>>? songGridPathsPicker = null,
        Func<Task<IReadOnlyList<string>>>? batchProjectPathsPicker = null,
        SongStyleLibrary? songStyleLibrary = null,
        LayerClipboard? layerClipboard = null,
        EditorPreferencesStore? preferencesStore = null,
        ExportPresetLibrary? exportPresets = null)
    {
        _session = session ?? nativeWorkflow?.Session ?? new EditSession(new EditorDocument());
        if (nativeWorkflow is not null && !ReferenceEquals(nativeWorkflow.Session, _session))
            throw new ArgumentException("The native workflow must use this editor session.", nameof(nativeWorkflow));
        _fonts = fonts ?? LocalFontLibrary.Default;
        _templateLibrary = templateLibrary ?? new TemplateLibrary(TemplateLibrary.DefaultDirectoryPath);
        _songStyleLibrary = songStyleLibrary ??
            new SongStyleLibrary(SongStyleLibrary.DefaultDirectoryPath);
        _layerClipboard = layerClipboard ?? LayerClipboard.Shared;
        _preferencesStore = preferencesStore ??
            new EditorPreferencesStore(EditorPreferencesStore.DefaultPath);
        var loadedPreferences = _preferencesStore.Load();
        _preferences = loadedPreferences.Preferences;
        _preferencesWarning = loadedPreferences.Warning;
        _showRulers.IsChecked = _preferences.ShowRulers;
        _exportPresetLibrary = exportPresets ??
            new ExportPresetLibrary(ExportPresetLibrary.DefaultPath);
        _exportPresetWarning = _exportPresetLibrary.Load().Warning;
        _recentProjects = recentProjects ?? new RecentProjectStore(RecentProjectStore.DefaultPath);
        _recovery = recovery ?? new AutoRecoveryService(_session,
            AutoRecoveryService.DefaultDirectoryPath, App.ProductVersion);
        _autoRecoveryEnabled = recovery is not null || !AutoRecoveryService.IsDisabledForVerification;
        _recoveryTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _recoveryTimer.Tick += async (_, _) => await CaptureRecoveryAsync();
        _native = nativeWorkflow ?? new NativeProjectWorkflow(_session, NativeImaging.ValidateImages, App.ProductVersion);
        _nativeSavePathPicker = nativeSavePathPicker;
        _projectLayerPathPicker = projectLayerPathPicker;
        _selectionProjectPathPicker = selectionProjectPathPicker;
        _songImagePathsPicker = songImagePathsPicker;
        _batchImagePathsPicker = batchImagePathsPicker;
        _songGridPathsPicker = songGridPathsPicker;
        _batchProjectPathsPicker = batchProjectPathsPicker;
        _exportDirectoryPicker = exportDirectoryPicker;
        _filePathPicker = filePathPicker;
        _imageLoader = imageLoader ?? NativeImaging.LoadImageAsync;
        _export = new PngExportWorkflow(_session, NativeImaging.ValidateImages,
            (document, transparent, width) => PngExporter.Render(document, transparent, NativeImaging.Decoder, width),
            NativeDocumentStore.WriteBytesAtomically, _importPaths, () => _native.CurrentPath,
            ImageAssetValidator.ReadEncodedDimensions);
        _projects = new ProjectOpenWorkflow(_native, NativeImaging.ValidateImages,
            NativeImaging.Compatibility, _importPaths);
        Title = App.ProductDisplayName;
        Width = 1200;
        Height = 760;
        MinWidth = 800;
        MinHeight = 500;
        _canvas = new SceneCanvas(_session);
        SetAccessibility(_canvas, "画布");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _canvas.Changed += Refresh;
        _canvas.Error += ShowStatusError;
        _canvas.ManualGuides.Changed += RefreshGuideStatus;
        _canvas.ViewChanged += UpdateZoomStatus;
        DragDrop.SetAllowDrop(_canvas, true);
        DragDrop.AddDragOverHandler(_canvas, OnCanvasDragOver);
        DragDrop.AddDropHandler(_canvas, OnCanvasDrop);
        _canvas.TextEditRequested += async id =>
        {
            try { await RunFileOperation(() => EditTextLayer(id)); }
            catch (Exception error) { ShowStatusError(error.Message); }
        };
        // The text box can finish preedit before a key bubbles to the window. Observe it
        // first, without handling the event or taking Enter away from the input method.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            _composingPropertyEnter = e.Key == Key.Enter && e.Source is TextBox field &&
                IsLayerProperty(field) && HasPreedit(field) ? e : null;
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Control || e.Source is TextBox ||
                !IsLayerListSource(e.Source) || e.Key is not (Key.Up or Key.Down) ||
                _canvas.IsInteracting || _session.ActiveGestureIds.Count > 0)
                return;
            var ids = _session.SelectedLayerIds.ToArray();
            if (ids.Length == 0) return;
            e.Handled = true;
            var before = _session.Document.Layers.Select(layer => layer.Id).ToArray();
            try
            {
                var action = e.Key == Key.Up ? LayerOrderAction.Forward : LayerOrderAction.Backward;
                RunLayerAction(() => _session.ReorderLayers(ids, action));
                var changed = !before.SequenceEqual(_session.Document.Layers.Select(layer => layer.Id));
                _status.Text = changed
                    ? $"已将 {ids.Length} 个图层{(e.Key == Key.Up ? "上移" : "下移")}一层；Ctrl+Z 可撤销。"
                    : "所选图层已到达层序边界。";
            }
            catch (Exception error) { ShowStatusError(error.Message); }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.A || e.KeyModifiers != KeyModifiers.Control ||
                !IsLayerFilterActive || !IsLayerListSource(e.Source)) return;
            e.Handled = true;
            _session.SetSelection(_layerItems.Where(item => item.Visible && !item.Locked)
                .Select(item => item.Id));
            Refresh();
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Control ||
                e.Source is TextBox ||
                (e.Source is Visual visual &&
                 visual.GetVisualAncestors().OfType<TextBox>().Any()) ||
                _canvas.IsInteracting || _session.ActiveGestureIds.Count > 0) return;
            if (e.Key == Key.C)
            {
                e.Handled = true;
                CopySelectedLayers();
            }
            else if (e.Key == Key.V)
            {
                e.Handled = true;
                PasteLayers();
            }
        }, RoutingStrategies.Tunnel);
        _propertyAlignment.ItemTemplate = new FuncDataTemplate<Editor.Core.HorizontalAlignment>((value, _) => new TextBlock
        { Text = value switch { Editor.Core.HorizontalAlignment.Left => "左对齐", Editor.Core.HorizontalAlignment.Center => "居中", _ => "右对齐" } });
        _propertyVerticalAlignment.ItemTemplate = new FuncDataTemplate<Editor.Core.VerticalAlignment>((value, _) => new TextBlock
        { Text = value switch { Editor.Core.VerticalAlignment.Top => "顶部", Editor.Core.VerticalAlignment.Center => "居中", _ => "底部" } });
        _layoutTemplate.ItemTemplate = new FuncDataTemplate<LayoutTemplate>((value, _) => new TextBlock
        { Text = value is null ? "" : UiText.Translate(value.Name) });
        _propertyWidth.PropertyChanged += (_, change) =>
        {
            if (!_refreshingPropertyDimensions && change.Property == TextBox.TextProperty)
                _dimensionDriver = DimensionDriver.Width;
        };
        _propertyHeight.PropertyChanged += (_, change) =>
        {
            if (!_refreshingPropertyDimensions && change.Property == TextBox.TextProperty)
                _dimensionDriver = DimensionDriver.Height;
        };

        var menu = BuildMenu();
        var toolbar = new WrapPanel { Name = "MainToolbar", Margin = new Thickness(8, 4) };
        AddAsyncButton(toolbar, "Open Project", OpenNative);
        AddAsyncButton(toolbar, "Save Project", async () => { await SaveNative(); });
        AddAsyncButton(toolbar, "Import Image", ImportImage);
        AddButton(toolbar, "Add Text", AddText);
        _undoButton = AddButton(toolbar, "Undo", UndoLastEdit, "Undo");
        AddButton(toolbar, "Fit view", _canvas.FitView, "FitView");
        toolbar.Children.Add(_zoomStatus);

        Closing += async (_, e) =>
        {
            if (_allowClose) return;
            if (_closingPrompt || _fileOperationPending)
            {
                e.Cancel = true;
                _status.Text = "请先完成当前文件操作，再关闭窗口。";
                return;
            }
            try { EnsureCanvasIdle(); _session.EnsureReadyForPersistence(); }
            catch (InvalidOperationException error)
            {
                e.Cancel = true;
                ShowStatusError(error.Message);
                return;
            }
            if (!_native.IsDirty)
            {
                _recovery.OnDocumentClean();
                return;
            }
            e.Cancel = true;
            await RunFileOperation(async () =>
            {
                _closingPrompt = true;
                try
                {
                    if (await ConfirmDocumentChange())
                    {
                        _recovery.OnDocumentReplaced();
                        _allowClose = true;
                        Close();
                    }
                }
                finally { _closingPrompt = false; }
            });
        };
        Closed += (_, _) => _recoveryTimer.Stop();

        var side = new StackPanel { Spacing = 4, Margin = new Thickness(3) };
        var layerHeading = new TextBlock
        {
            Text = "图层（上方显示在前）",
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap
        };
        ToolTip.SetTip(layerHeading, "拖动图层行或按 Ctrl+↑/↓ 调整层序。");
        side.Children.Add(layerHeading);
        // The search row shares this column; keep the primary property fields visible at 720p.
        _layers.Height = 155;
        ScrollViewer.SetHorizontalScrollBarVisibility(_layers, ScrollBarVisibility.Disabled);
        _layers.Focusable = true;
        SetAccessibility(_layers, "图层列表",
            "选择图层；方向键导航，Ctrl+向上或向下调整叠放顺序。");
        _layers.SelectionMode = SelectionMode.Multiple;
        _layers.ItemsSource = _layerItems;
        _layers.ItemTemplate = new FuncDataTemplate<LayerListItem>((item, _) => BuildLayerRow(item));
        _layers.AddHandler(InputElement.PointerPressedEvent, OnLayerListPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _layers.AddHandler(InputElement.PointerMovedEvent, OnLayerListPointerMoved,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _layers.AddHandler(InputElement.PointerReleasedEvent, OnLayerListPointerReleased,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _layers.AddHandler(InputElement.PointerCaptureLostEvent, OnLayerListPointerCaptureLost,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        _layers.DoubleTapped += async (_, e) =>
        {
            if (_updatingLayerList || _canvas.IsInteracting ||
                e.Source is not Visual source ||
                source.FindAncestorOfType<Button>(includeSelf: true) is not null ||
                source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext
                    is not LayerListItem item)
                return;
            e.Handled = true;
            _session.SelectedLayerId = item.Id;
            Refresh();
            try { await RunFileOperation(() => RenameLayerCoreAsync(item.Id)); }
            catch (Exception error) { ShowStatusError(error.Message); }
        };
        _layers.SelectionChanged += (_, _) =>
        {
            if (_updatingLayerList) return;
            if (_canvas.IsInteracting) { Refresh(); _status.Text = "请先完成当前拖动或缩放操作。"; return; }
            _session.SetSelection(_layers.SelectedItems?.OfType<LayerListItem>().Select(item => item.Id)
                ?? Enumerable.Empty<Guid>());
            UpdateLayerSelectionSummary();
            if (_session.SelectedLayerId is Guid id && _session.Document.Find(id) is { Kind: LayerKind.Text } layer)
                _text.Text = layer.Text;
            RefreshPropertyFields();
            _canvas.Refresh();
        };
        _layerListHost.Children.Add(_layers);
        _layerListHost.Children.Add(_layerDropIndicator);
        _layerSearch.TextChanged += (_, _) =>
        {
            _layerMatchCursor = -1;
            if (RefreshLayerList())
            {
                RefreshPropertyFields();
                _canvas.Refresh();
            }
        };
        _layerSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !HasPreedit(_layerSearch))
            {
                e.Handled = true;
                MoveLayerMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            }
        };
        _previousLayerMatch.Click += (_, _) => MoveLayerMatch(-1);
        _nextLayerMatch.Click += (_, _) => MoveLayerMatch(1);
        _selectAllLayerMatches.Click += (_, _) => SelectAllLayerMatches();
        SetAccessibility(_layerSearch, "查找图层名称或文字内容",
            "按图层名称或文字内容查找；Enter 定位下一项，Shift+Enter 定位上一项。");
        SetAccessibility(_previousLayerMatch, "上一个匹配图层",
            "定位到上一个匹配的图层。");
        SetAccessibility(_nextLayerMatch, "下一个匹配图层",
            "定位到下一个匹配的图层。");
        SetAccessibility(_selectAllLayerMatches, "选择全部匹配图层",
            "选择筛选结果中全部可见且未锁定的图层。");
        _filterLayerMatches.IsCheckedChanged += (_, _) =>
        {
            if (RefreshLayerList())
            {
                RefreshPropertyFields();
                _canvas.Refresh();
            }
        };
        var layerSearchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        layerSearchRow.Children.Add(_layerSearch);
        Grid.SetColumn(_previousLayerMatch, 1);
        Grid.SetColumn(_nextLayerMatch, 2);
        _previousLayerMatch.Margin = new Thickness(4, 0, 0, 0);
        _nextLayerMatch.Margin = new Thickness(4, 0, 0, 0);
        layerSearchRow.Children.Add(_previousLayerMatch);
        layerSearchRow.Children.Add(_nextLayerMatch);
        side.Children.Add(layerSearchRow);
        _layerSearchFeedback.Children.Add(_layerMatchInfo);
        var layerSearchActions = new WrapPanel
        {
            Children = { _filterLayerMatches, _selectAllLayerMatches }
        };
        _selectAllLayerMatches.Margin = new Thickness(6, 0, 0, 0);
        _layerSearchFeedback.Children.Add(layerSearchActions);
        _layerSearchFeedback.Children.Add(_layerFilterNotice);
        side.Children.Add(_layerSearchFeedback);
        side.Children.Add(_layerListHost);
        _selectGroupButton.Click += (_, _) => SelectGroupFromOverview();
        _toggleGroupVisibilityButton.Click += (_, _) => ToggleSelectedGroupVisibility();
        _toggleGroupLockButton.Click += (_, _) => ToggleSelectedGroupLock();
        _ungroupButton.Click += (_, _) => UngroupSelectedGroup();
        var groupActions = new WrapPanel();
        SetAccessibility(_selectGroupButton, "选择整组",
            "选择该编组的全部成员，包括隐藏和锁定成员。");
        SetAccessibility(_toggleGroupVisibilityButton, "显示或隐藏整组",
            "切换该编组全部成员的可见性。");
        SetAccessibility(_toggleGroupLockButton, "锁定或解锁整组",
            "切换该编组全部成员的锁定状态。");
        SetAccessibility(_ungroupButton, "取消整组编组",
            "取消该编组全部成员的分组关系。");
        groupActions.Children.Add(_selectGroupButton);
        groupActions.Children.Add(_toggleGroupVisibilityButton);
        groupActions.Children.Add(_toggleGroupLockButton);
        groupActions.Children.Add(_ungroupButton);
        var groupPanel = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new ScrollViewer { Height = 145, Content = _groupCardsHost },
                groupActions
            }
        };
        _groupOverviewExpander = new Expander
            { Name = "GroupOverview", Header = "编组概览", Content = groupPanel,
                IsExpanded = false, IsVisible = false };
        side.Children.Add(_groupOverviewExpander);
        _moveWholeGroup.IsChecked = _preferences.MoveWholeGroup;
        _canvas.MoveWholeGroup = _preferences.MoveWholeGroup;
        _canvas.ShowSafeArea = _preferences.ShowSafeArea;
        _canvas.SafeAreaMarginPercent = _preferences.SafeAreaMarginPercent;
        _moveWholeGroup.IsCheckedChanged += (_, _) =>
            _canvas.MoveWholeGroup = _moveWholeGroup.IsChecked == true;
        var layerEditRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        layerEditRow.Children.Add(_moveWholeGroup);
        Grid.SetColumn(_layerSelectionSummary, 1);
        layerEditRow.Children.Add(_layerSelectionSummary);
        side.Children.Add(layerEditRow);
        BuildPropertyPanel();
        _propertyPanel.AddHandler(InputElement.GotFocusEvent, (_, e) =>
        {
            // Remember mouse and Tab navigation as well as F6, without committing drafts.
            if (e.Source is Control field && field.Focusable)
                _lastPropertyFocus = field;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        SetAccessibility(_layerPropertiesExpander, "所选图层属性",
            "F6 可在此区域与画布、图层列表和标尺之间切换焦点。");
        _layerPropertiesExpander.Content = _propertyPanel;
        side.Children.Add(_layerPropertiesExpander);
        BuildBatchStylePanel();
        side.Children.Add(_batchStyleSection);
        var arrange = new StackPanel { Spacing = 4 };
        arrange.Children.Add(new TextBlock { Text = "对齐 / 分布", FontWeight = FontWeight.SemiBold });
        var arrangeAction = new ComboBox { Name = "ArrangeAction", SelectedIndex = 0,
            ItemsSource = new[] { "左对齐", "水平居中", "右对齐", "顶部对齐",
                "垂直居中", "底部对齐", "水平等间距", "垂直等间距" } };
        var arrangeReference = new ComboBox { Name = "ArrangeReference", SelectedIndex = 0,
            ItemsSource = new[] { "选中内容的范围", "画布" } };
        arrange.Children.Add(arrangeAction);
        arrange.Children.Add(arrangeReference);
        AddButton(arrange, "Apply arrangement", () =>
        {
            _session.ArrangeSelection((ArrangeAction)arrangeAction.SelectedIndex,
                (ArrangeReference)arrangeReference.SelectedIndex, _canvas.MoveWholeGroup);
            Refresh();
            _status.Text = "排列已应用。Ctrl+Z 可撤销整次操作。";
        }, "ApplyArrangement");
        arrange.Children.Add(new TextBlock { Text = "整组编辑时隐藏成员仍随组移动。排列前请解锁相关图层；等间距需要至少 3 个对象和足够空间。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        side.Children.Add(new Expander { Name = "ArrangementSettings", Header = "对齐与分布", Content = arrange });
        var guides = new StackPanel { Spacing = 4 };
        _showRulers.IsCheckedChanged += (_, _) => UpdateRulerVisibility();
        guides.Children.Add(_showRulers);
        AddPropertyField(guides, "Guide X (0–canvas width)", _guideX);
        AddButton(guides, "Add vertical guide", AddVerticalGuideFromField, "AddVerticalGuide");
        AddPropertyField(guides, "Guide Y (0–canvas height)", _guideY);
        AddButton(guides, "Add horizontal guide", AddHorizontalGuideFromField, "AddHorizontalGuide");
        AddButton(guides, "Add safe-area guides", AddSafeAreaGuides, "AddSafeAreaGuides");
        guides.Children.Add(_guideStatus);
        AddButton(guides, "Clear guides", ClearManualGuides, "ClearManualGuides");
        guides.Children.Add(new TextBlock
        {
            Text = "从顶部或左侧标尺拖入画布可创建辅助线；在画布上拖动可调整，拖回标尺或画布外可删除。标尺和辅助线只存在于当前会话，不写入工程或 PNG。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        });
        side.Children.Add(new Expander
            { Name = "GuideSettings", Header = "标尺与辅助线", Content = guides });
        var canvasSettings = new StackPanel { Spacing = 4 };
        canvasSettings.Children.Add(new TextBlock { Text = "画布 / 背景", FontWeight = FontWeight.SemiBold });
        AddPropertyField(canvasSettings, "Canvas width", _canvasWidth);
        AddPropertyField(canvasSettings, "Canvas height", _canvasHeight);
        AddPropertyField(canvasSettings, "Background color (#AARRGGBB)", _backgroundColor);
        AddButton(canvasSettings, "Apply canvas settings", ApplyCanvasSettings, "ApplyCanvasSettings");
        AddButton(canvasSettings, "Resize canvas and fit content", ResizeCanvasToFit,
            "ResizeCanvasToFit");
        canvasSettings.Children.Add(_backgroundSource);
        canvasSettings.Children.Add(_automaticBackground);
        _automaticBackground.IsCheckedChanged += (_, _) =>
        {
            if (_updatingBackgroundFields) return;
            try
            {
                EnsureCanvasIdle();
                _session.UpdateDocument(document => document.BackgroundBlend =
                    document.BackgroundBlend with { Enabled = _automaticBackground.IsChecked == true },
                    "切换自动背景");
                RefreshBackgroundState();
            }
            catch (Exception error) { ShowStatusError(error.Message); RefreshBackgroundFields(refreshInputs: false); }
        };
        AddPropertyField(canvasSettings, "Blend blur (0–20)", _backgroundBlur);
        AddPropertyField(canvasSettings, "Blend darkness (0–100%)", _backgroundDarkness);
        AddPropertyField(canvasSettings, "Blend range (0.25–2.0)", _backgroundRange);
        AddPropertyField(canvasSettings, "Blend transition (0–1)", _backgroundTransition);
        AddButton(canvasSettings, "Apply blend settings", ApplyBlendSettings, "ApplyBlendSettings");
        AddAsyncButton(canvasSettings, "Background contribution", OpenBackgroundContribution,
            "OpenBackgroundContribution");
        AddAsyncButton(canvasSettings, "Resource and quality check", OpenResourcePanel,
            "OpenResourcePanel");
        canvasSettings.Children.Add(_showBackgroundContributors);
        _showBackgroundContributors.IsCheckedChanged += (_, _) =>
        {
            if (_updatingBackgroundFields) return;
            _canvas.ShowBackgroundContributors = _showBackgroundContributors.IsChecked == true;
        };
        AddAsyncButton(canvasSettings, "Set background image", SetBackgroundImage);
        AddButton(canvasSettings, "Generate blended snapshot", GenerateBlendedBackground,
            "GenerateBlendedBackground");
        canvasSettings.Children.Add(new TextBlock
        {
            Text = "固定图片或快照优先于自动混合。清除固定背景后恢复自动更新。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        });
        AddButton(canvasSettings, "Clear background image", ClearBackgroundImage);
        side.Children.Add(new Expander { Name = "CanvasSettings", Header = "画布与背景", Content = canvasSettings });
        var templates = new StackPanel { Spacing = 4 };
        templates.Children.Add(new TextBlock { Text = "排列选中的图层 / 歌曲组", FontWeight = FontWeight.SemiBold });
        templates.Children.Add(_layoutTemplate);
        AddButton(templates, "Apply layout", ApplyLayoutTemplate, "ApplyLayoutTemplate");
        templates.Children.Add(_layoutName);
        AddButton(templates, "Capture selected layout", CaptureLayoutTemplate, "CaptureLayoutTemplate");
        AddAsyncButton(templates, "Open layout template", OpenLayoutTemplate);
        AddAsyncButton(templates, "Save layout template", SaveLayoutTemplate);
        templates.Children.Add(_templateLibraryHost);
        templates.Children.Add(new TextBlock
        {
            Text = "每个歌曲组选一个图层，或选择独立图层。槽位按选择顺序分配；模板保存位置和尺寸，组成员一起移动。",
            TextWrapping = TextWrapping.Wrap
        });
        var templateExpander = new Expander
            { Name = "LayoutTemplates", Header = "布局模板", Content = templates };
        templateExpander.Expanded += (_, _) => BuildTemplateLibraryPanel();
        side.Children.Add(templateExpander);
        var songStyles = new StackPanel { Spacing = 4 };
        songStyles.Children.Add(new TextBlock
        {
            Text = "歌曲样式库保存角色结构、相对几何和视觉样式，不写入正文、图片或 .ppglayout 槽位。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        });
        AddAsyncButton(songStyles, "Open song style library", OpenSongStyleLibrary,
            "OpenSongStyleLibrarySide");
        side.Children.Add(new Expander
        {
            Name = "SongStyles", Header = "歌曲样式库", Content = songStyles
        });
        var help = new StackPanel { Spacing = 6 };
        help.Children.Add(new TextBlock
        {
            Text = "空白处拖框选择；Ctrl 单击切换选择，Shift 单击追加。Ctrl+A 选择可见且未锁定的图层；Ctrl+Shift+A 反向选择；Ctrl+C / Ctrl+V 复制、粘贴选中图层。F6 / Shift+F6 在画布、图层列表、属性区和标尺之间循环焦点。双击文字编辑内容。拖动 8 个控制点缩放，Shift 临时保持比例；上方控制点旋转，Shift 按 15° 步进。移动时 Shift 锁定方向，Alt 暂停吸附。滚轮缩放视图，中键拖动画布，Ctrl+0 适合窗口。拖动顶部/左侧标尺创建辅助线；在画布上拖动辅助线可调整，拖回标尺或按 Delete 可删除。可将本地图片文件直接拖到画布批量导入。",
            TextWrapping = TextWrapping.Wrap
        });
        help.Children.Add(new TextBlock
        {
            Text = "历史摘要（只读）", FontWeight = FontWeight.SemiBold
        });
        help.Children.Add(_historySummary);
        side.Children.Add(new Expander
        {
            Name = "HelpPanel", Header = "操作提示", Content = help
        });
        side.Children.Add(_conversionHeader);
        side.Children.Add(_conversionDetails);
        side.Children.Add(_fontNotice);
        side.Children.Add(_recoveryNotice);

        var body = new Grid { Name = "EditorBody", ColumnDefinitions = new ColumnDefinitions("*,280") };
        // Keep floating bars, but reserve the outer bar's full hover/hit lane. Nested
        // list/group bars and sidebar text must not occupy that same horizontal strip.
        var sideScroll = new ScrollViewer
        {
            Name = "SidebarScroll", Content = side, Padding = new Thickness(0, 0, 16, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AllowAutoHide = true
        };
        Grid.SetColumn(sideScroll, 1);
        _rulerRow = new RowDefinition { Height = new GridLength(RulerSize) };
        _rulerColumn = new ColumnDefinition { Width = new GridLength(RulerSize) };
        var canvasWorkspace = new Grid
        {
            Name = "CanvasWorkspace",
            RowDefinitions = new RowDefinitions { _rulerRow, new RowDefinition() },
            ColumnDefinitions = new ColumnDefinitions { _rulerColumn, new ColumnDefinition() }
        };
        _rulerCorner = new Border
        {
            Name = "RulerCorner",
            Background = new SolidColorBrush(Color.FromRgb(39, 39, 39))
        };
        _topRuler = new CanvasRuler(_canvas, CanvasRulerOrientation.Horizontal)
            { Name = "HorizontalRuler" };
        _leftRuler = new CanvasRuler(_canvas, CanvasRulerOrientation.Vertical)
            { Name = "VerticalRuler" };
        SetAccessibility(_topRuler, "水平标尺");
        SetAccessibility(_leftRuler, "垂直标尺");
        Grid.SetRow(_rulerCorner, 0);
        Grid.SetColumn(_rulerCorner, 0);
        Grid.SetRow(_topRuler, 0);
        Grid.SetColumn(_topRuler, 1);
        Grid.SetRow(_leftRuler, 1);
        Grid.SetColumn(_leftRuler, 0);
        Grid.SetRow(_canvas, 1);
        Grid.SetColumn(_canvas, 1);
        canvasWorkspace.Children.Add(_rulerCorner);
        canvasWorkspace.Children.Add(_topRuler);
        canvasWorkspace.Children.Add(_leftRuler);
        canvasWorkspace.Children.Add(_canvas);
        Grid.SetColumn(canvasWorkspace, 0);
        body.Children.Add(sideScroll);
        body.Children.Add(canvasWorkspace);
        var root = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        _status.Margin = new Thickness(8);
        root.Children.Add(menu);
        root.Children.Add(toolbar);
        root.Children.Add(_status);
        root.Children.Add(body);
        Content = root;
        UpdateRulerVisibility();
        RefreshGuideStatus();
        ReloadTemplateLibrary();
        RefreshRecentMenu();
        Refresh();
        if (_exportPresetWarning is not null)
            _status.Text = "导出预设：" + _exportPresetWarning;
        else if (_preferencesWarning is not null)
            _status.Text = "首选项：" + _preferencesWarning;
        Opened += async (_, _) =>
        {
            if (_autoRecoveryEnabled)
            {
                await OfferRecoveryAsync();
                _recoveryTimer.Start();
            }
        };
    }

    private Menu BuildMenu()
    {
        var files = new MenuItem { Header = "文件" };
        AddMenuItem(files, "New", NewProject, "NewProject");
        AddMenuItem(files, "Open Project", OpenNative, "OpenProject");
        _recentMenu = new MenuItem { Header = "最近工程", Name = "RecentProjectsMenu" };
        files.Items.Add(_recentMenu);
        AddMenuItem(files, "Save Project", async () => { await SaveNative(); }, "SaveProject");
        AddMenuItem(files, "Save Project As", async () => { await SaveNativeAs(); }, "SaveProjectAs");
        files.Items.Add(new Separator());
        AddMenuItem(files, "Import project layers", ImportProjectLayers, "ImportProjectLayers");
        AddMenuItem(files, "Save selection as project", SaveSelectionProject, "SaveSelectionProject");
        var exports = new MenuItem { Header = "导出" };
        AddMenuItem(exports, "Export PNG", () => ExportPng(false), "ExportPng");
        AddMenuItem(exports, "Export Transparent PNG", () => ExportPng(true), "ExportTransparentPng");
        AddMenuItem(exports, "Batch export PNG", BatchExportPng, "BatchExportPng");
        AddMenuItem(exports, "Batch export selected layers", ExportSelectedPng, "BatchExportSelectionPng");
        AddMenuItem(exports, "Batch export across projects", BatchRenderProjects, "BatchRenderProjects");
        files.Items.Add(exports);
        var advanced = new MenuItem { Header = "其他格式" };
        AddMenuItem(advanced, "Save Temporary JSON", SaveProject, "SaveTemporaryJson");
        files.Items.Add(advanced);

        var edit = new MenuItem { Header = "编辑" };
        _undoMenuItem = AddMenuItem(edit, "Undo", UndoLastEdit, "Undo");
        _redoMenuItem = AddMenuItem(edit, "Redo", RedoLastEdit, "Redo");
        AddMenuItem(edit, "Copy selected layers", CopySelectedLayers, "CopySelectedLayers");
        AddMenuItem(edit, "Paste layers", PasteLayers, "PasteLayers");
        AddMenuItem(edit, "Clear selection", ClearSelection, "ClearSelection");
        AddMenuItem(edit, "Invert selection", InvertSelection, "InvertSelection");
        AddMenuItem(edit, "编辑选中文字…", () => EditTextLayer(SelectedLayer()), "EditSelectedText");
        AddMenuItem(edit, "Song info workbench", OpenSongInfoWorkbench, "OpenSongInfoWorkbench");
        AddMenuItem(edit, "Preferences", OpenPreferences, "OpenPreferences");
        AddMenuItem(edit, "Resource and quality check", OpenResourcePanel, "OpenResourcePanelMenu");
        AddMenuItem(edit, "Duplicate", () => { _session.DuplicateLayers(SelectedLayers()); Refresh(); }, "DuplicateLayers");
        AddMenuItem(edit, "Delete", () => { _session.DeleteLayers(SelectedLayers()); Refresh(); }, "DeleteLayers");

        var add = new MenuItem { Header = "添加" };
        AddMenuItem(add, "Import Image", ImportImage, "ImportImage");
        AddMenuItem(add, "Import multiple images", ImportImageBatch, "ImportImagesBatch");
        AddMenuItem(add, "Add Text", AddText, "AddText");
        AddMenuItem(add, "Add Shape", () => { _session.AddLayer(LayerPresets.Shape(_session.Document)); Refresh(); }, "AddShape");
        AddMenuItem(add, "Add Badge", () => { _session.AddLayer(EditorDocument.NewBadge(new PointD(700, 330))); Refresh(); }, "AddBadge");
        add.Items.Add(new Separator());
        AddMenuItem(add, "Build song from image", () => { _session.CreateSongCard(SelectedLayer()); Refresh(); }, "CreateSongCard");
        AddMenuItem(add, "Build 3 / 5 songs", BuildSongBatch, "BuildSongBatch");
        AddMenuItem(add, "Build N-song grid", BuildSongGrid, "BuildSongGrid");
        AddMenuItem(add, "Add Original label", () =>
        {
            var image = _session.SelectedLayerId is Guid id && _session.Document.Find(id).Kind == LayerKind.Image ? id : (Guid?)null;
            _session.AddOriginalTag(image); Refresh();
        }, "AddOriginalTag");

        var layers = new MenuItem { Header = "图层" };
        AddMenuItem(layers, "Replace image", ReplaceImage, "ReplaceImage");
        AddMenuItem(layers, "Rename layer…", RenameSelectedLayer, "RenameLayer");
        AddMenuItem(layers, "Show / hide", () => ToggleSelected(layer => layer.Visible,
            (layer, value) => layer.Visible = value, "显示或隐藏图层"), "ToggleSelectedVisibility");
        AddMenuItem(layers, "Lock / unlock", () => ToggleSelected(layer => layer.Locked,
            (layer, value) => layer.Locked = value, "锁定或解锁图层"), "ToggleSelectedLock");
        var groups = new MenuItem { Header = "编组" };
        AddMenuItem(groups, "Group selected", () => { _session.GroupLayers(SelectedLayers()); Refresh(); }, "GroupSelected");
        AddMenuItem(groups, "Ungroup", () => RunLayerAction(() => _session.UngroupLayers(SelectedLayers())), "Ungroup");
        AddMenuItem(groups, "Hide group", () => SetSelectedGroupVisible(false), "HideGroup");
        AddMenuItem(groups, "Show group", () => SetSelectedGroupVisible(true), "ShowGroup");
        layers.Items.Add(groups);
        var order = new MenuItem { Header = "叠放顺序" };
        AddMenuItem(order, "Move forward", () => ReorderSelected(LayerOrderAction.Forward), "MoveForward");
        AddMenuItem(order, "Move backward", () => ReorderSelected(LayerOrderAction.Backward), "MoveBackward");
        AddMenuItem(order, "Bring to front", () => ReorderSelected(LayerOrderAction.Front), "BringToFront");
        AddMenuItem(order, "Send to back", () => ReorderSelected(LayerOrderAction.Back), "SendToBack");
        layers.Items.Add(order);

        var arrange = new MenuItem { Header = "排列" };
        arrange.Items.Add(BuildArrangementMenu("以选中内容为参照", ArrangeReference.SelectionBounds));
        arrange.Items.Add(BuildArrangementMenu("以画布为参照", ArrangeReference.Canvas));
        var view = new MenuItem { Header = "视图" };
        AddMenuItem(view, "Fit view", _canvas.FitView, "FitViewMenu");
        AddMenuItem(view, "Zoom out", () => _canvas.ZoomOut(), "ZoomOutMenu");
        AddMenuItem(view, "Zoom in", () => _canvas.ZoomIn(), "ZoomInMenu");
        AddMenuItem(view, "Actual size (100%)", () => _canvas.ZoomToActualSize(),
            "ZoomActualSizeMenu");
        AddMenuItem(view, "Fit selection", ZoomToSelection, "ZoomToSelectionMenu");
        view.Items.Add(new Separator());
        _toggleRulersMenu = new MenuItem
            { Name = "ToggleRulersMenu", Header = UiText.Translate("Hide rulers") };
        _toggleRulersMenu.Click += (_, _) => ToggleRulers();
        view.Items.Add(_toggleRulersMenu);
        AddMenuItem(view, "Add vertical guide at center",
            () => AddGuideAtCenter(vertical: true), "AddVerticalGuideCenter");
        AddMenuItem(view, "Add horizontal guide at center",
            () => AddGuideAtCenter(vertical: false), "AddHorizontalGuideCenter");
        AddMenuItem(view, "Add safe-area guides", AddSafeAreaGuides, "AddSafeAreaGuidesMenu");
        AddMenuItem(view, "Clear guides", ClearManualGuides, "ClearManualGuidesMenu");
        AddMenuItem(view, "Background contribution", OpenBackgroundContribution,
            "OpenBackgroundContributionMenu");
        var templates = new MenuItem { Header = "模板" };
        AddMenuItem(templates, "Open layout template", OpenLayoutTemplate, "OpenLayoutTemplate");
        AddMenuItem(templates, "Save layout template", SaveLayoutTemplate, "SaveLayoutTemplate");
        AddMenuItem(templates, "Apply layout", ApplyLayoutTemplate, "ApplyLayoutTemplateMenu");
        AddMenuItem(templates, "Capture selected layout", CaptureLayoutTemplate, "CaptureLayoutTemplateMenu");
        AddMenuItem(templates, "Open song style library", OpenSongStyleLibrary,
            "OpenSongStyleLibrary");
        return new Menu { Name = "MainMenu", ItemsSource = new[] { files, edit, add, layers, arrange, view, templates } };
    }

    private MenuItem BuildArrangementMenu(string title, ArrangeReference reference)
    {
        var menu = new MenuItem { Header = title };
        var labels = new[] { "左对齐", "水平居中", "右对齐", "顶部对齐", "垂直居中", "底部对齐", "水平等间距", "垂直等间距" };
        foreach (var action in Enum.GetValues<ArrangeAction>())
            AddMenuItem(menu, labels[(int)action], () =>
            {
                _session.ArrangeSelection(action, reference, _canvas.MoveWholeGroup);
                Refresh(); _status.Text = "排列已应用。Ctrl+Z 可撤销整次操作。";
            });
        return menu;
    }

    private MenuItem AddMenuItem(MenuItem parent, string label, Action action, string? name = null)
    {
        var item = new MenuItem { Name = name, Header = UiText.Translate(label) };
        item.Click += (_, _) =>
        {
            try { EnsureCanvasIdle(); action(); }
            catch (Exception error) { ShowStatusError(error.Message); }
        };
        parent.Items.Add(item);
        return item;
    }

    private MenuItem AddMenuItem(MenuItem parent, string label, Func<Task> action, string? name = null)
    {
        var item = new MenuItem { Name = name, Header = UiText.Translate(label) };
        item.Click += async (_, _) =>
        {
            try { await RunFileOperation(action); }
            catch (Exception error) { ShowStatusError(error.Message); }
        };
        parent.Items.Add(item);
        return item;
    }

    private void ShowStatusError(string message) => _status.Text = $"错误：{UiText.Translate(message)}";

    private void ToggleRulers()
    {
        _showRulers.IsChecked = _showRulers.IsChecked != true;
        UpdateRulerVisibility();
    }

    private void UpdateRulerVisibility()
    {
        var visible = _showRulers.IsChecked == true;
        var size = new GridLength(visible ? RulerSize : 0);
        if (_rulerColumn is not null) _rulerColumn.Width = size;
        if (_rulerRow is not null) _rulerRow.Height = size;
        if (_rulerCorner is not null) _rulerCorner.IsVisible = visible;
        if (_topRuler is not null) _topRuler.IsVisible = visible;
        if (_leftRuler is not null) _leftRuler.IsVisible = visible;
        if (_toggleRulersMenu is not null)
            _toggleRulersMenu.Header = UiText.Translate(visible ? "Hide rulers" : "Show rulers");
    }

    private void AddVerticalGuideFromField()
    {
        if (!double.TryParse(_guideX.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var value))
        {
            ShowStatusError("Guide positions must be finite numbers (use '.' for decimals).");
            return;
        }
        ShowGuideResult(_canvas.ManualGuides.Add(true, value,
            _session.Document.CanvasWidth, _session.Document.CanvasHeight), vertical: true);
    }

    private void AddHorizontalGuideFromField()
    {
        if (!double.TryParse(_guideY.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var value))
        {
            ShowStatusError("Guide positions must be finite numbers (use '.' for decimals).");
            return;
        }
        ShowGuideResult(_canvas.ManualGuides.Add(false, value,
            _session.Document.CanvasWidth, _session.Document.CanvasHeight), vertical: false);
    }

    private void AddGuideAtCenter(bool vertical)
    {
        var value = vertical ? _session.Document.CanvasWidth / 2.0 :
            _session.Document.CanvasHeight / 2.0;
        ShowGuideResult(_canvas.ManualGuides.Add(vertical, value,
            _session.Document.CanvasWidth, _session.Document.CanvasHeight), vertical);
    }

    private void ShowGuideResult(ManualGuideAddResult result, bool vertical)
    {
        var name = vertical ? "垂直" : "水平";
        _status.Text = result switch
        {
            ManualGuideAddResult.Added => $"已添加{name}辅助线。拖动可调整，拖回标尺可删除。",
            ManualGuideAddResult.Duplicate => $"该位置已有{name}辅助线。",
            ManualGuideAddResult.OutOfRange => $"{name}辅助线坐标超出画布范围。",
            ManualGuideAddResult.LimitReached => $"辅助线数量已达到 {ManualGuideSet.MaxGuides} 条上限。",
            _ => "辅助线未添加。"
        };
    }

    private void ClearManualGuides()
    {
        var removed = _canvas.ClearManualGuides();
        _status.Text = removed == 0 ? "当前没有辅助线。" : $"已清除 {removed} 条辅助线。";
    }

    private void AddSafeAreaGuides()
    {
        var width = _session.Document.CanvasWidth;
        var height = _session.Document.CanvasHeight;
        var margin = Math.Min(width, height) * _canvas.SafeAreaMarginPercent / 100;
        if (margin <= 0 || margin * 2 >= Math.Min(width, height))
        {
            _status.Text = "当前安全区边距无法生成辅助线，请调整首选项。";
            return;
        }
        var added = 0;
        foreach (var position in new[] { margin, width - margin })
            if (_canvas.ManualGuides.Add(true, position, width, height) ==
                ManualGuideAddResult.Added) added++;
        foreach (var position in new[] { margin, height - margin })
            if (_canvas.ManualGuides.Add(false, position, width, height) ==
                ManualGuideAddResult.Added) added++;
        _canvas.Refresh();
        _status.Text = added == 0
            ? "安全区四边已经存在辅助线。"
            : $"已添加 {added} 条安全区辅助线；可拖动调整或清除。";
    }

    private void RefreshGuideStatus()
    {
        var vertical = _canvas.ManualGuides.Positions(vertical: true).Count;
        var horizontal = _canvas.ManualGuides.Positions(vertical: false).Count;
        _guideStatus.Text = _canvas.ManualGuides.Count == 0
            ? "当前没有辅助线。可从标尺拖入，或在上方输入精确坐标。"
            : $"当前 {_canvas.ManualGuides.Count} 条辅助线：垂直 {vertical}、水平 {horizontal}。";
    }

    private void CopySelectedLayers()
    {
        var ids = _session.SelectedLayerIds.ToArray();
        if (ids.Length == 0)
        {
            _status.Text = "请先选择要复制的图层。";
            return;
        }
        try
        {
            var count = _layerClipboard.Copy(_session.Document, ids, _canvas.MoveWholeGroup);
            _status.Text = $"已复制 {count} 个图层和资源；可在当前或其他工程中粘贴。";
        }
        catch (Exception error) { ShowStatusError(error.Message); }
    }

    private void PasteLayers()
    {
        if (!_layerClipboard.HasContent)
        {
            _status.Text = "图层剪贴板为空。请先复制选中图层。";
            return;
        }
        try
        {
            EnsureCanvasIdle();
            var ids = _layerClipboard.Paste(_session);
            Refresh();
            _status.Text = $"已粘贴 {ids.Count} 个图层；Ctrl+Z 可撤销整次粘贴。";
        }
        catch (Exception error) { ShowStatusError(error.Message); }
    }

    private void ClearSelection()
    {
        try
        {
            EnsureCanvasIdle();
            var hadSelection = _session.SelectedLayerIds.Count > 0;
            _session.SetSelection([]);
            Refresh();
            _status.Text = hadSelection ? "已取消选择。" : "当前没有选择。";
        }
        catch (Exception error) { ShowStatusError(error.Message); }
    }

    private void InvertSelection()
    {
        try
        {
            EnsureCanvasIdle();
            var eligible = IsLayerFilterActive
                ? _layerItems.Where(item => item.Visible && !item.Locked).Select(item => item.Id)
                : null;
            _session.InvertEditableSelection(eligible);
            Refresh();
            _status.Text = _session.SelectedLayerIds.Count == 0
                ? "反向选择后没有可见且未锁定的图层。"
                : $"已反向选择 {_session.SelectedLayerIds.Count} 个可见且未锁定的图层。";
        }
        catch (Exception error) { ShowStatusError(error.Message); }
    }

    private async Task OpenPreferences()
    {
        EnsureCanvasIdle();
        var updated = await new PreferencesDialog(_preferences)
            .ShowDialog<EditorPreferences?>(this);
        if (updated is null)
        {
            _status.Text = "已取消首选项修改。";
            return;
        }
        try
        {
            _preferencesStore.Save(updated);
            _preferences = updated;
            ApplyPreferences();
            _status.Text = "首选项已保存；仅影响本机编辑器，不写入工程。";
        }
        catch (Exception error) { ShowStatusError(error.Message); }
    }

    private void ApplyPreferences()
    {
        _showRulers.IsChecked = _preferences.ShowRulers;
        _moveWholeGroup.IsChecked = _preferences.MoveWholeGroup;
        _canvas.MoveWholeGroup = _preferences.MoveWholeGroup;
        _canvas.ShowSafeArea = _preferences.ShowSafeArea;
        _canvas.SafeAreaMarginPercent = _preferences.SafeAreaMarginPercent;
        UpdateRulerVisibility();
    }

    private async Task EditTextLayer(Guid id)
    {
        if (_editingText) return;
        try
        {
            EnsureCanvasIdle();
            var layer = _session.Document.Find(id);
            if (layer.Kind != LayerKind.Text) throw new InvalidOperationException("Select a text layer first.");
            if (layer.Locked) throw new InvalidOperationException("Locked layers cannot be edited.");
            _editingText = true;
            var pending = new PendingDocumentOperation(_session);
            var document = _session.Document;
            var selection = _session.SelectedLayerIds.ToArray();
            var text = await new TextEditDialog(layer.Text).ShowDialog<string?>(this);
            if (text is null)
            {
                if (!ReferenceEquals(document, _session.Document) || !selection.SequenceEqual(_session.SelectedLayerIds))
                    Refresh();
                return;
            }
            EnsureCanvasIdle();
            pending.EnsureUnchanged("编辑文字期间工程已发生变化，请重新编辑。");
            var changed = text != layer.Text;
            if (changed) _session.UpdateLayer(id, value => value.Text = text, "编辑文字");
            if (ReferenceEquals(document, _session.Document) && selection.SequenceEqual(_session.SelectedLayerIds))
            {
                // Confirmation owns the text content, while unrelated inputs remain drafts.
                if (changed) { _text.Text = text; _canvas.Refresh(); }
                RefreshTitle();
            }
            else Refresh();
            _status.Text = changed ? "文字已更新。Ctrl+Z 可撤销本次编辑。" : "文字未更改。";
        }
        catch (Exception error) { ShowStatusError(error.Message); }
        finally { _editingText = false; }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape && (_layerPointerPress is not null || _layerDragPointer is not null))
        {
            ClearLayerListDragState();
            _status.Text = "已取消图层重排。";
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F6 &&
            e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            var sourceBox = e.Source as TextBox ??
                (e.Source as Visual)?.GetVisualAncestors().OfType<TextBox>().FirstOrDefault();
            if (_canvas.IsInteracting || _session.ActiveGestureIds.Count > 0 ||
                (sourceBox is not null && HasPreedit(sourceBox))) return;
            if (CycleFocus(e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            {
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.S && e.KeyModifiers is KeyModifiers.Control or
            (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            try
            {
                await RunFileOperation(async () =>
                {
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) await SaveNativeAs();
                    else await SaveNative();
                });
            }
            catch (Exception error) { ShowStatusError(error.Message); }
            return;
        }
        if (e.Source is TextBox field)
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None &&
                !field.AcceptsReturn && IsLayerProperty(field) &&
                !ReferenceEquals(_composingPropertyEnter, e) && !HasPreedit(field))
            {
                e.Handled = true;
                try { EnsureCanvasIdle(); ApplyProperties(); }
                catch (Exception error) { ShowStatusError(error.Message); }
            }
            return;
        }
        if (_canvas.IsInteracting) return;
        if (_session.ActiveGestureIds.Count > 0) return;
        if (e.Key == Key.A && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) &&
            (e.Source is SceneCanvas || IsLayerListSource(e.Source)))
        {
            InvertSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control &&
            (e.Source is SceneCanvas || IsLayerListSource(e.Source)))
        {
            e.Handled = true;
            CopySelectedLayers();
            return;
        }
        if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control &&
            (e.Source is SceneCanvas || IsLayerListSource(e.Source)))
        {
            e.Handled = true;
            PasteLayers();
            return;
        }
        if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control &&
            (e.Source is SceneCanvas || IsLayerListSource(e.Source)))
        {
            if (IsLayerFilterActive && IsLayerListSource(e.Source))
                _session.SetSelection(_layerItems.Where(item => item.Visible && !item.Locked)
                    .Select(item => item.Id));
            else _session.SelectAllEditableLayers();
            Refresh();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.D0 or Key.D1 or Key.D2)
        {
            if (e.Key == Key.D0) _canvas.FitView();
            else if (e.Key == Key.D1) _canvas.ZoomToActualSize();
            else ZoomToSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None &&
            IsLayerListSource(e.Source))
        {
            e.Handled = true;
            try
            {
                if (_session.SelectedLayerIds.Count == 0) return;
                _session.DeleteLayers(_session.SelectedLayerIds.ToArray());
                Refresh();
            }
            catch (Exception error) { ShowStatusError(error.Message); }
            return;
        }
        if (e.Key == Key.F2 && e.KeyModifiers == KeyModifiers.None &&
            IsLayerListSource(e.Source))
        {
            e.Handled = true;
            try { await RunFileOperation(RenameSelectedLayer); }
            catch (Exception error) { ShowStatusError(error.Message); }
            return;
        }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (_session.ActiveGestureIds.Count > 0) return;
        try
        {
            if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                RedoLastEdit();
            else if (e.Key == Key.Z) UndoLastEdit();
            else if (e.Key == Key.Y) RedoLastEdit();
            else if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.Control)
            {
                if (_session.SelectedLayerIds.Count == 0) return;
                _session.DuplicateLayers(_session.SelectedLayerIds.ToArray());
                Refresh();
            }
            else return;
        }
        catch (Exception error) { ShowStatusError(error.Message); }
        e.Handled = true;
    }

    private bool IsLayerListSource(object? source) => source is Visual visual &&
        (ReferenceEquals(visual, _layers) || visual.GetVisualAncestors().Contains(_layers));

    private bool CycleFocus(bool reverse)
    {
        var zones = AvailableFocusZones();
        if (zones.Count == 0) return false;
        var focused = FocusManager?.GetFocusedElement() as Control;
        var current = FocusZoneOf(focused);
        if (current == FocusZone.Rulers) _lastRulerFocus = focused;

        var currentIndex = current is { } zone ? zones.IndexOf(zone) : -1;
        var first = currentIndex < 0
            ? (reverse ? zones.Count - 1 : 0)
            : (currentIndex + (reverse ? -1 : 1) + zones.Count) % zones.Count;
        for (var offset = 0; offset < zones.Count; offset++)
        {
            var index = (first + (reverse ? -offset : offset) + zones.Count * 2) % zones.Count;
            var target = zones[index];
            if (!FocusEditorZone(target)) continue;
            _status.Text = $"焦点：{FocusZoneName(target)}。F6 下一区域，Shift+F6 上一区域。";
            return true;
        }
        return false;
    }

    private List<FocusZone> AvailableFocusZones()
    {
        var zones = new List<FocusZone> { FocusZone.Canvas, FocusZone.Layers };
        if (_session.SelectedLayerId is not null && _propertyPanel.IsVisible)
            zones.Add(FocusZone.Properties);
        if (_topRuler?.IsVisible == true || _leftRuler?.IsVisible == true)
            zones.Add(FocusZone.Rulers);
        return zones;
    }

    private FocusZone? FocusZoneOf(Control? control)
    {
        if (control is null) return null;
        if (ReferenceEquals(control, _canvas)) return FocusZone.Canvas;
        if (IsLayerListSource(control)) return FocusZone.Layers;
        if (ReferenceEquals(control, _propertyPanel) ||
            ReferenceEquals(control, _layerPropertiesExpander) ||
            control.GetVisualAncestors().Contains(_propertyPanel)) return FocusZone.Properties;
        if (ReferenceEquals(control, _topRuler) || ReferenceEquals(control, _leftRuler))
            return FocusZone.Rulers;
        return null;
    }

    private bool FocusEditorZone(FocusZone zone)
    {
        switch (zone)
        {
            case FocusZone.Canvas:
                return _canvas.Focus();
            case FocusZone.Layers:
                return _layers.Focus();
            case FocusZone.Properties:
                _layerPropertiesExpander.IsExpanded = true;
                if (_lastPropertyFocus is { } last &&
                    FocusZoneOf(last) == FocusZone.Properties &&
                    last.IsEffectivelyVisible && last.IsEffectivelyEnabled && last.Focus())
                {
                    last.BringIntoView();
                    return true;
                }
                foreach (var field in _propertyPanel.GetVisualDescendants().OfType<Control>())
                    if (field.Focusable && field.IsEffectivelyVisible &&
                        field.IsEffectivelyEnabled && field.Focus())
                    {
                        _lastPropertyFocus = field;
                        field.BringIntoView();
                        return true;
                    }
                return false;
            case FocusZone.Rulers:
                var ruler = _lastRulerFocus as CanvasRuler;
                if (ruler?.IsVisible != true)
                    ruler = _topRuler?.IsVisible == true ? _topRuler : _leftRuler;
                if (ruler?.IsVisible != true) return false;
                var focused = ruler.Focus();
                if (focused) _lastRulerFocus = ruler;
                return focused;
            default:
                return false;
        }
    }

    private static string FocusZoneName(FocusZone zone) => zone switch
    {
        FocusZone.Canvas => "画布",
        FocusZone.Layers => "图层列表",
        FocusZone.Properties => "图层属性",
        FocusZone.Rulers => "标尺",
        _ => "编辑区"
    };

    private bool IsLayerProperty(TextBox field) => new[]
    {
        _propertyName, _propertyX, _propertyY, _propertyWidth, _propertyHeight, _propertyOpacity,
        _propertyRotation, _propertyColor, _propertyFont, _propertyFontSize, _propertyLineSpacing,
        _propertyLevel
    }.Contains(field);

    private static bool HasPreedit(TextBox field) =>
        field.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>()
            .Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));

    private void AddText()
    {
        _session.AddLayer(new Layer
        {
            Kind = LayerKind.Text, Name = "Text", Text = _text.Text ?? "",
            Bounds = new RectD(200, 160, 650, 160), FontSize = 54
        });
        Refresh();
        _status.Text = "已添加文字。双击画布上的文字可编辑内容。";
    }

    private void ApplyText()
    {
        if (_session.SelectedLayerId is not Guid id || _session.Document.Find(id).Kind != LayerKind.Text)
            throw new InvalidOperationException("Select a text layer first.");
        if (_session.Document.Find(id).Locked)
            throw new InvalidOperationException("Locked layers cannot be edited.");
        _session.UpdateLayer(id, layer => layer.Text = _text.Text ?? "", "编辑文字");
        // Text and style are separate commits; applying one must retain the other's inputs.
        Refresh(refreshProperties: false);
    }

    private async Task BuildSongBatch()
    {
        if (_buildingSongBatch) return;
        _buildingSongBatch = true;
        try
        {
            var workflow = new SongBatchWorkflow(_session, async path => (await LoadImageForCanvas(path)).Image,
                NativeImaging.ValidateImages, _importPaths);
            var count = await workflow.ImportAsync(async () =>
            {
                if (_songImagePathsPicker is not null)
                {
                    var paths = await _songImagePathsPicker();
                    if (paths.Count > 0) EnsureCanvasIdle();
                    return paths;
                }
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择 3 张曲绘生成三列布局，或 5 张生成五点布局",
                    AllowMultiple = true,
                    FileTypeFilter = [new FilePickerFileType("曲绘图片")
                        { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff"] }]
                });
                if (files.Count > 0) EnsureCanvasIdle();
                return files.Select(LocalPath).ToArray();
            });
            if (count > 0) Refresh();
            _status.Text = count == 0 ? "已取消生成歌曲。"
                : $"已生成 {count} 个歌曲组。可编辑曲名、曲师、谱师和难度；撤销可移除整批歌曲。";
        }
        catch (InvalidOperationException error) when (error.Message == "Choose exactly 3 or 5 artwork images.")
        {
            ShowStatusError(error.Message);
            await ShowMessageDialog("无法生成歌曲", UiText.Translate(error.Message));
        }
        finally { _buildingSongBatch = false; }
    }

    private async Task BuildSongGrid()
    {
        if (_buildingSongGrid) return;
        _buildingSongGrid = true;
        try
        {
            var pending = new PendingDocumentOperation(_session, () => _native.CurrentPath);
            var setup = new SongGridDialog(pending.Snapshot, ChooseSongGridImages);
            var request = await setup.ShowDialog<SongGridRequest?>(this);
            if (request is null)
            {
                _status.Text = "已取消生成歌曲网格。";
                return;
            }
            pending.EnsureUnchanged("Project changed while configuring the song grid. Please try again.");
            var workflow = new SongGridWorkflow(_session, LoadImageForCanvas,
                NativeImaging.ValidateImages, _importPaths, () => _native.CurrentPath);
            var progress = new BatchImageProgressDialog("正在生成歌曲网格");
            progress.Opened += async (_, _) =>
            {
                try
                {
                    var count = await workflow.ImportAsync(request.Paths, request.Settings,
                        progress.Progress, progress.Token);
                    progress.Finish(count);
                }
                catch (OperationCanceledException) { progress.Finish(-1); }
                catch (Exception error) { progress.Fail(error); }
            };
            var result = await progress.ShowDialog<int?>(this);
            if (progress.Failure is not null) throw progress.Failure;
            if (result is null or <= 0)
            {
                _status.Text = "已取消生成歌曲网格。";
                return;
            }
            Refresh();
            _status.Text = $"已生成 {result} 首歌曲的 {request.Settings.Rows} × {request.Settings.Columns} 网格；" +
                "原三曲/五点预设未改变，Ctrl+Z 可一次撤销整批。";
        }
        finally { _buildingSongGrid = false; }
    }

    private async Task<IReadOnlyList<string>> ChooseSongGridImages(Window owner)
    {
        IReadOnlyList<string> paths;
        if (_songGridPathsPicker is not null) paths = await _songGridPathsPicker();
        else
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择网格曲绘（顺序即从左到右、从上到下的槽位顺序）",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff"]
                    }
                ]
            });
            paths = files.Select(LocalPath).ToArray();
        }
        if (paths.Count > 0) EnsureCanvasIdle();
        return paths;
    }

    private async Task OpenSongInfoWorkbench()
    {
        if (_editingSongInfo) return;
        _editingSongInfo = true;
        try
        {
            EnsureCanvasIdle();
            var groups = _session.SelectedLayerIds
                .Select(id => _session.Document.Find(id).GroupId)
                .OfType<Guid>().Distinct().ToArray();
            if (groups.Length == 0)
                throw new InvalidOperationException("请先在画布或图层列表中选择至少一个歌曲组。");
            var pending = new PendingDocumentOperation(_session, () => _native.CurrentPath,
                includeSelection: true);
            var snapshot = pending.Snapshot;
            var suggestions = SongInfoWorkbench.Suggest(snapshot, groups);
            if (suggestions.Count == 0)
                throw new InvalidOperationException("所选内容没有可编辑的歌曲组。");

            string? Apply(IReadOnlyList<SongInfoEdit> edits, SongTextStyle? style)
            {
                try
                {
                    pending.EnsureUnchanged(
                        "Project changed while editing song info. Please reopen the workbench.");
                    var checks = new List<Action>();
                    var protections = new List<Action>();
                    foreach (var edit in edits)
                    {
                        if (string.IsNullOrWhiteSpace(edit.ReplacementArtworkPath)) continue;
                        var path = Path.GetFullPath(edit.ReplacementArtworkPath);
                        checks.Add(FilePathIdentity.PrepareTargetCheck(path));
                        protections.Add(_importPaths.PrepareAdd(path));
                    }
                    foreach (var check in checks) check();
                    _session.ApplySongInfoBatch(edits, style, NativeImaging.ValidateImages);
                    foreach (var protect in protections) protect();
                    return null;
                }
                catch (Exception error) { return UiText.Translate(error.Message); }
            }

            var dialog = new SongInfoWorkbenchDialog(snapshot, suggestions,
                ChooseSongInfoArtwork, LoadImageForCanvas, Apply);
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true)
            {
                Refresh();
                _status.Text = $"已更新 {groups.Length} 个歌曲组的信息；Ctrl+Z 可撤销整次编辑。";
            }
            else _status.Text = "已取消歌曲信息编辑。";
        }
        finally { _editingSongInfo = false; }
    }

    private async Task OpenSongStyleLibrary()
    {
        if (_editingSongStyle) return;
        _editingSongStyle = true;
        try
        {
            EnsureCanvasIdle();
            var dialog = new SongStyleLibraryDialog(_session, _songStyleLibrary,
                () => _session.SelectedLayerIds.ToArray());
            await dialog.ShowDialog<bool?>(this);
            if (dialog.DocumentChanged)
            {
                Refresh();
                _status.Text = "歌曲样式已应用；可继续编辑，Ctrl+Z 可撤销整次操作。";
            }
            else _status.Text = "已关闭歌曲样式库。";
        }
        finally { _editingSongStyle = false; }
    }

    private Task<string?> ChooseSongInfoArtwork(Window owner) =>
        ChooseOpenPath(owner, EditorFileAction.ImportImage, new FilePickerOpenOptions
        {
            Title = "选择替换曲绘", AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff"]
                }
            ]
        });

    private async Task ImportImageBatch() =>
        await ImportImageBatchCore(ChooseBatchImagePaths, "批量导入");

    public async Task<int> ImportDroppedImagesAsync(IReadOnlyList<string> paths,
        int ignoredItems = 0)
    {
        var selection = ImageImportPathFilter.Filter(paths);
        if (selection.Paths.Count == 0)
        {
            _status.Text = "拖放内容中没有支持的本地图片（PNG / JPEG / BMP / TIFF）。";
            return 0;
        }
        if (_canvas.IsInteracting || _fileOperationPending || _importingImageBatch)
        {
            _status.Text = "请先完成当前拖动、缩放或文件操作，再拖入图片。";
            return 0;
        }
        try { EnsureCanvasIdle(); }
        catch (Exception error)
        {
            ShowStatusError(error.Message);
            return 0;
        }
        return await ImportImageBatchCore(
            () => Task.FromResult(selection.Paths), "拖放", selection.Ignored + ignoredItems);
    }

    private async Task<int> ImportImageBatchCore(
        Func<Task<IReadOnlyList<string>>> choosePaths, string sourceLabel, int ignored = 0)
    {
        if (_importingImageBatch)
        {
            _status.Text = "请先完成当前批量导入。";
            return 0;
        }
        _importingImageBatch = true;
        try
        {
            var workflow = new BatchImageImportWorkflow(_session, LoadImageForCanvas,
                NativeImaging.ValidateImages, _importPaths, () => _native.CurrentPath);
            var dialog = new BatchImageProgressDialog();
            dialog.Opened += async (_, _) =>
            {
                try
                {
                    var count = await workflow.ImportAsync(choosePaths,
                        dialog.Progress, dialog.Token);
                    dialog.Finish(count);
                }
                catch (OperationCanceledException) { dialog.Finish(-1); }
                catch (Exception error) { dialog.Fail(error); }
            };
            var result = await dialog.ShowDialog<int?>(this);
            if (dialog.Failure is not null) throw dialog.Failure;
            if (result is not int count || count <= 0)
            {
                _status.Text = "已取消" + sourceLabel + "图片。";
                return 0;
            }
            Refresh();
            _status.Text = $"已通过{sourceLabel}导入 {count} 张图片；Ctrl+Z 可一次移除整批。" +
                (ignored > 0 ? $" 已忽略 {ignored} 个非图片/非本地项。" : "");
            return count;
        }
        finally { _importingImageBatch = false; }
    }

    private void OnCanvasDragOver(object? sender, DragEventArgs e)
    {
        try
        {
            var files = e.DataTransfer.TryGetFiles();
            e.DragEffects = files?.Any(item =>
                item is IStorageFile file && file.TryGetLocalPath() is not null) == true
                ? DragDropEffects.Copy : DragDropEffects.None;
        }
        catch { e.DragEffects = DragDropEffects.None; }
        e.Handled = true;
    }

    private async void OnCanvasDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            var items = (e.DataTransfer.TryGetFiles() ?? []).ToArray();
            var paths = items.OfType<IStorageFile>()
                .Select(file => file.TryGetLocalPath())
                .Where(path => path is not null)
                .Select(path => path!)
                .ToArray();
            await ImportDroppedImagesAsync(paths, items.Length - paths.Length);
        }
        catch (Exception error) { ShowStatusError(error.Message); }
    }

    private async Task<IReadOnlyList<string>> ChooseBatchImagePaths()
    {
        IReadOnlyList<string> paths;
        if (_batchImagePathsPicker is not null) paths = await _batchImagePathsPicker();
        else
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "批量导入图片（可选择任意数量）",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff"]
                    }
                ]
            });
            paths = files.Select(LocalPath).ToArray();
        }
        if (paths.Count > 0) EnsureCanvasIdle();
        return paths;
    }

    private async Task ShowMessageDialog(string title, string message)
    {
        var dialog = new Window
        {
            Title = title, Width = 430, Height = 180, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var button = new Button
        {
            Name = "ConfirmMessage", Content = "确定", IsDefault = true, IsCancel = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        button.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 20,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, button } };
        await dialog.ShowDialog(this);
    }

    private async Task SaveSelectionProject()
    {
        if (_savingSelectionProject) return;
        _savingSelectionProject = true;
        try
        {
            var workflow = new SelectionProjectWorkflow(_session, NativeImaging.ValidateImages,
                _importPaths, () => _native.CurrentPath, App.ProductVersion);
            var saved = await workflow.SaveAsync(_canvas.MoveWholeGroup, async () =>
            {
                if (_selectionProjectPathPicker is not null) return CompletePathChoice(await _selectionProjectPathPicker());
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "将选中图层保存为工程（保留位置，使用纯色背景）",
                    SuggestedFileName = "song-card" + NativeProjectFile.Extension,
                    DefaultExtension = NativeProjectFile.ExtensionName,
                    SuggestedFileType = NativeProjectType(), FileTypeChoices = [NativeProjectType()],
                    ShowOverwritePrompt = true
                });
                return CompletePathChoice(file is null ? null : LocalPath(file));
            });
            _status.Text = saved
                ? "选中内容已保存为可复用工程，当前工程保留原状态。"
                : "已取消保存选中内容。";
        }
        finally { _savingSelectionProject = false; }
    }

    private async Task ImportProjectLayers()
    {
        if (_importingProjectLayers) return;
        _importingProjectLayers = true;
        try
        {
            var workflow = new ProjectLayerImportWorkflow(_session, NativeImaging.ValidateImages,
                _importPaths, () => _native.CurrentPath);
            var count = await workflow.ImportAsync(async () =>
            {
                if (_projectLayerPathPicker is not null) return CompletePathChoice(await _projectLayerPathPicker());
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "导入工程的全部图层（保留位置，沿用当前画布和背景）",
                    AllowMultiple = false, FileTypeFilter = [NativeProjectType()]
                });
                return CompletePathChoice(files.Count == 0 ? null : LocalPath(files[0]));
            });
            if (count > 0) Refresh();
            _status.Text = count == 0 ? "已取消导入工程图层。"
                : $"已按原位置导入 {count} 个图层。撤销可移除本次导入；画布和背景沿用当前设置。";
        }
        finally { _importingProjectLayers = false; }
    }

    private Task ImportImage() => ImportImageCore(ImageImportAction.Add);
    private Task ReplaceImage() => ImportImageCore(ImageImportAction.Replace);
    private Task SetBackgroundImage() => ImportImageCore(ImageImportAction.Background);

    private async Task ImportImageCore(ImageImportAction action)
    {
        var document = _session.Document;
        var selection = _session.SelectedLayerIds.ToArray();
        var request = action switch
        {
            ImageImportAction.Add => EditorFileAction.ImportImage,
            ImageImportAction.Replace => EditorFileAction.ReplaceImage,
            _ => EditorFileAction.BackgroundImage
        };
        var workflow = new ImageImportWorkflow(_session, LoadImageForCanvas, _importPaths, () => _native.CurrentPath);
        var imported = await workflow.ImportAsync(action, () => ChooseOpenPath(request, new FilePickerOpenOptions
        {
            Title = action switch { ImageImportAction.Add => "导入图片",
                ImageImportAction.Replace => "替换选中的图片", _ => "设置背景图片" },
            AllowMultiple = false
        }));
        if (!imported) { _status.Text = "已取消导入图片。"; return; }
        if (action == ImageImportAction.Background && ReferenceEquals(document, _session.Document) &&
            selection.SequenceEqual(_session.SelectedLayerIds)) RefreshBackgroundState();
        else Refresh();
        _status.Text = action switch { ImageImportAction.Replace => "已替换选中的图片，保留原位置和编组。",
            ImageImportAction.Background => "固定背景图片已保存到工程中。", _ => "图片已导入。" };
    }

    private void ClearBackgroundImage()
    {
        _session.ClearFixedBackground();
        _status.Text = "已清除固定背景图片。";
        RefreshBackgroundState();
    }

    private void GenerateBlendedBackground()
    {
        _session.EnsureReadyForPersistence();
        NativeImaging.ValidateImages(_session.Document);
        var bytes = BackgroundComposer.Generate(_session.Document, NativeImaging.Decoder);
        _session.SetFixedBackground(bytes);
        _status.Text = "混合背景快照已保存到工程中；修改曲绘后可重新生成。";
        RefreshBackgroundState();
    }

    private async Task NewProject()
    {
        if (!await ConfirmDocumentChange()) return;
        _projects.New();
        _recovery.OnDocumentReplaced();
        SetConversionSummary(_projects.Summary, _projects.Details);
        Refresh();
        ResetLayerListView();
    }

    private async Task<bool> ConfirmDocumentChange()
    {
        try { return await PendingChangesWorkflow.ConfirmAsync(_native, PromptPendingChanges, SaveNative); }
        catch (Exception error)
        {
            ShowStatusError(error.Message);
            return false;
        }
    }

    private async Task<PendingChangesChoice> PromptPendingChanges()
    {
        var dialog = new Window
        {
            Title = "未保存的修改", Width = 390, Height = 160,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "取消" };
        cancel.Click += (_, _) => dialog.Close(PendingChangesChoice.Cancel);
        var save = new Button { Content = "保存" };
        save.Click += (_, _) => dialog.Close(PendingChangesChoice.Save);
        var discard = new Button { Content = "放弃修改" };
        discard.Click += (_, _) => dialog.Close(PendingChangesChoice.Discard);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        buttons.Children.Add(discard);
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 16,
            Children = { new TextBlock { Text = "当前工程有未保存的修改。",
                TextWrapping = TextWrapping.Wrap }, buttons } };
        var choice = await dialog.ShowDialog<PendingChangesChoice>(this);
        if (choice != PendingChangesChoice.Cancel) EnsureCanvasIdle();
        return choice;
    }

    private async Task OpenNative()
    {
        var pending = new PendingDocumentOperation(_session, () => _native.CurrentPath);
        var path = await ChooseOpenPath(EditorFileAction.OpenProject, new FilePickerOpenOptions
        {
            Title = "打开工程",
            AllowMultiple = false,
            FileTypeFilter = [NativeProjectType(), CompatibleProjectType(), AllFilesType()]
        });
        if (path is null) return;
        pending.EnsureUnchanged("Project changed while choosing a project to open. Please open it again.");
        if (!await ConfirmDocumentChange()) return;
        OpenNativePath(path);
    }

    private void OpenNativePath(string path)
    {
        _projects.Open(path);
        SetConversionSummary(_projects.Summary, _projects.Details);
        if (_native.CurrentPath is not null) RecordRecentProject(_native.CurrentPath);
        _recovery.OnDocumentReplaced();
        Refresh();
        ResetLayerListView();
    }

    private async Task OpenRecentProjectAsync(string path)
    {
        if (!File.Exists(path))
        {
            ShowStatusError("最近工程文件不存在。可从“最近工程”菜单清理缺失项。");
            RefreshRecentMenu();
            return;
        }
        var pending = new PendingDocumentOperation(_session, () => _native.CurrentPath);
        if (!await ConfirmDocumentChange()) return;
        pending.EnsureUnchanged("Project changed while confirming unsaved changes. Please try again.");
        OpenNativePath(path);
    }

    private void RecordRecentProject(string path)
    {
        if (_recentProjects.Record(path, DateTimeOffset.Now)) RefreshRecentMenu();
        else
        {
            _recoveryNotice.Text = "最近工程列表暂时无法更新：" + (_recentProjects.LastError ?? "未知错误");
            _recoveryNotice.IsVisible = true;
        }
    }

    private void RefreshRecentMenu()
    {
        if (_recentMenu is null) return;
        _recentMenu.Items.Clear();
        var snapshot = _recentProjects.Load();
        if (snapshot.Diagnostics.Count > 0)
        {
            var diagnosticItem = new MenuItem
            {
                Header = "最近工程索引无法读取",
                IsEnabled = false
            };
            _recentMenu.Items.Add(diagnosticItem);
            ToolTip.SetTip(diagnosticItem, snapshot.Diagnostics[0].Message);
        }
        if (snapshot.Entries.Count == 0)
        {
            _recentMenu.Items.Add(new MenuItem { Header = "（暂无最近工程）", IsEnabled = false });
            return;
        }
        foreach (var entry in snapshot.Entries)
        {
            var item = new MenuItem
            {
                Header = entry.DisplayName + (entry.Exists ? "" : "（文件不存在）"),
                IsEnabled = entry.Exists
            };
            ToolTip.SetTip(item, entry.Path + "\n" + entry.LastOpenedUtc.ToLocalTime().ToString("g"));
            item.Click += async (_, _) =>
            {
                try { await RunFileOperation(() => OpenRecentProjectAsync(entry.Path)); }
                catch (Exception error) { ShowStatusError(error.Message); }
            };
            _recentMenu.Items.Add(item);
        }
        if (snapshot.Entries.Any(entry => !entry.Exists))
        {
            _recentMenu.Items.Add(new Separator());
            var clean = new MenuItem { Header = "清理缺失项", Name = "CleanMissingRecentProjects" };
            clean.Click += (_, _) =>
            {
                try
                {
                    var removed = _recentProjects.RemoveMissing();
                    RefreshRecentMenu();
                    _status.Text = $"已从最近工程移除 {removed} 个缺失项。";
                }
                catch (Exception error) { ShowStatusError(error.Message); }
            };
            _recentMenu.Items.Add(clean);
        }
    }

    private async Task CaptureRecoveryAsync()
    {
        if (_recoveryCaptureInProgress || _fileOperationPending || _savingNative ||
            _exportingPng || _closingPrompt || _canvas.IsInteracting ||
            _session.ActiveGestureIds.Count > 0)
            return;
        if (!_session.IsDirty)
        {
            if (_recovery.HasSnapshot) _recovery.OnDocumentClean();
            return;
        }
        _recoveryCaptureInProgress = true;
        try
        {
            _session.EnsureReadyForPersistence();
            var snapshot = _session.Document.Copy();
            var projectPath = _native.CurrentPath;
            var now = DateTimeOffset.Now;
            await Task.Run(() => _recovery.CaptureIfChanged(snapshot, projectPath, now));
            _recoveryNotice.IsVisible = false;
        }
        catch (Exception error)
        {
            _recoveryNotice.Text = "自动恢复快照暂时无法写入：" + UiText.Translate(error.Message);
            _recoveryNotice.IsVisible = true;
        }
        finally { _recoveryCaptureInProgress = false; }
    }

    private async Task OfferRecoveryAsync()
    {
        if (_recoveryOfferShown) return;
        _recoveryOfferShown = true;
        var load = _recovery.LoadCandidates();
        if (load.Candidates.Count == 0)
        {
            if (load.Diagnostics.Count > 0)
            {
                _recoveryNotice.Text = $"{load.Diagnostics.Count} 个自动恢复条目无法读取；原文件仍保留。";
                _recoveryNotice.IsVisible = true;
            }
            return;
        }
        var dialog = new RecoveryDialog(load.Candidates, load.Diagnostics);
        var choice = await dialog.ShowDialog<RecoveryDialogChoice?>(this);
        if (choice is null) return;
        if (choice.Action == RecoveryAction.Discard)
        {
            try
            {
                _recovery.Delete(choice.Candidate);
                _status.Text = "已丢弃选中的自动恢复快照。";
            }
            catch (Exception error) { ShowStatusError(error.Message); }
            return;
        }
        try
        {
            var document = NativePackageStore.Read(choice.Candidate.SnapshotPath);
            _projects.RestoreRecovered(document);
            try { _recovery.Delete(choice.Candidate); }
            catch (Exception error) { _recoveryNotice.Text = "恢复快照已载入，但旧快照未能删除：" + error.Message;
                _recoveryNotice.IsVisible = true; }
            _recovery.OnDocumentReplaced();
            SetConversionSummary(_projects.Summary, _projects.Details);
            Refresh();
            ResetLayerListView();
            _status.Text = "已恢复为未保存工程。请检查内容后另存为新的 .ppgproj。";
        }
        catch (Exception error)
        {
            ShowStatusError("恢复快照失败：" + error.Message);
        }
    }

    private void SetConversionSummary(string summary, IEnumerable<string> details)
    {
        _status.Text = UiText.Translate(summary);
        var text = string.Join(Environment.NewLine, details.Select(UiText.Translate));
        _conversionDetails.Text = text;
        _conversionDetails.IsVisible = text.Length > 0;
        _conversionHeader.IsVisible = text.Length > 0;
    }

    private async Task<bool> SaveNative()
    {
        if (_savingNative) return false;
        _savingNative = true;
        try
        {
            if (_native.CurrentPath is null) return await SaveNativeAsCore();
            _importPaths.EnsureNotProtected(_native.CurrentPath);
            var document = _session.Document;
            var selection = _session.SelectedLayerIds.ToArray();
            _native.Save();
            _recovery.OnDocumentClean();
            RecordRecentProject(_native.CurrentPath);
            _status.Text = "工程已保存。";
            RefreshAfterSave(document, selection);
            return true;
        }
        finally { _savingNative = false; }
    }

    private async Task<bool> SaveNativeAs()
    {
        if (_savingNative) return false;
        _savingNative = true;
        try { return await SaveNativeAsCore(); }
        finally { _savingNative = false; }
    }

    private async Task<bool> SaveNativeAsCore()
    {
        var document = _session.Document;
        var selection = _session.SelectedLayerIds.ToArray();
        var saved = await _native.SaveAsAsync(async () =>
        {
            if (_nativeSavePathPicker is not null) return CompletePathChoice(await _nativeSavePathPicker());
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"工程另存为 {NativeProjectFile.FilterPattern}",
                SuggestedFileName = _native.CurrentPath is null
                    ? "project" + NativeProjectFile.Extension
                    : Path.GetFileName(_native.CurrentPath),
                DefaultExtension = NativeProjectFile.ExtensionName,
                SuggestedFileType = NativeProjectType(),
                FileTypeChoices = [NativeProjectType(), AllFilesType()],
                ShowOverwritePrompt = true
            });
            return CompletePathChoice(file is null ? null : LocalPath(file));
        }, _importPaths);
        if (!saved) return false;
        _recovery.OnDocumentClean();
        if (_native.CurrentPath is not null) RecordRecentProject(_native.CurrentPath);
        _status.Text = "工程已保存。";
        RefreshAfterSave(document, selection);
        return true;
    }

    private async Task SaveProject()
    {
        var document = _session.Document;
        var selection = _session.SelectedLayerIds.ToArray();
        var saved = await _native.SaveTemporaryAsync(() => ChooseSavePath(EditorFileAction.SaveTemporary, new FilePickerSaveOptions
        {
            Title = "保存临时 JSON 工程", SuggestedFileName = "p1-temporary.json",
            DefaultExtension = "json"
        }), _importPaths);
        if (!saved) return;
        _recovery.OnDocumentClean();
        _status.Text = "临时 JSON 工程已保存。";
        RefreshAfterSave(document, selection);
    }

    private async Task ExportPng(bool transparent)
    {
        if (_exportingPng) return;
        _exportingPng = true;
        try
        {
            PngExportSize? size = null;
            var exported = await _export.ExportWithReviewAsync(transparent, LocalFontRegistry.IsMissing,
                ReviewExportWarnings, async () =>
                {
                    var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = transparent ? "导出透明 PNG" : "导出 PNG",
                        SuggestedFileName = transparent ? "transparent-export.png" : "export.png",
                        DefaultExtension = "png"
                    });
                    return CompletePathChoice(file is null ? null : LocalPath(file));
                }, async () =>
                {
                    var width = await new PngExportDialog(_session.Document,
                        transparent: transparent,
                        defaultPolicy: _preferences.DefaultExportQualityPolicy)
                        .ShowDialog<int?>(this);
                    if (width is not null)
                    {
                        EnsureCanvasIdle();
                        size = PngExportSize.Create(_session.Document, width);
                    }
                    return width;
                });
            _status.Text = exported
                ? $"PNG 已导出（{size!.Value.Width} × {size.Value.Height}）" +
                    (transparent ? "，背景透明。" : "。")
                : "已取消 PNG 导出。";
        }
        finally { _exportingPng = false; }
    }

    private async Task BatchExportPng()
    {
        if (_exportingPng) return;
        _exportingPng = true;
        try
        {
            var pending = new PendingDocumentOperation(_session, () => _native.CurrentPath);
            var snapshot = pending.Snapshot;
            NativeImaging.ValidateImages(snapshot);
            var warnings = ExportPreflight.Inspect(snapshot, LocalFontRegistry.IsMissing);
            var setup = new PngExportQueueDialog(snapshot, warnings, ChooseExportDirectory,
                _preferences.DefaultExportQualityPolicy, _exportPresetLibrary);
            var request = await setup.ShowDialog<PngExportQueueRequest?>(this);
            if (request is null)
            {
                _status.Text = "已取消批量导出 PNG。";
                return;
            }
            pending.EnsureUnchanged("Document changed during batch export setup. Please start again.");
            await RunPngQueueSnapshotAsync(snapshot, request, "批量导出");
        }
        finally { _exportingPng = false; }
    }

    private async Task RunPngQueueSnapshotAsync(EditorDocument snapshot,
        PngExportQueueRequest request, string label)
    {
        var tasks = PngExportQueue.Normalize(snapshot, request.Tasks, false, request.CropRegion);
        var directory = PngExportQueue.CreateRunDirectory(request.ParentDirectory, DateTime.Now);
        var progress = new PngExportProgressDialog();
        progress.Opened += async (_, _) =>
        {
            try
            {
                var result = await PngExportQueue.ExecuteRegionAsync(snapshot, tasks, directory,
                    NativeImaging.ValidateImages,
                    (document, transparent, width, region) =>
                        Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
                            ? PngExporter.Render(document, transparent, NativeImaging.Decoder, width, region)
                            : Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                                PngExporter.Render(document, transparent, NativeImaging.Decoder, width, region)),
                    (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
                    _importPaths, () => _native.CurrentPath, progress.Progress, progress.Token,
                    isLocalFontMissing: LocalFontRegistry.IsMissing,
                    readImageDimensions: ImageAssetValidator.ReadEncodedDimensions,
                    qualityPolicy: request.QualityPolicy,
                    outputRegion: request.CropRegion);
                progress.Complete(result);
            }
            catch (OperationCanceledException)
            {
                progress.Stop("批量导出已取消，当前文件未写出。已完成文件仍保留。");
            }
            catch (Exception error) { progress.Fail(error); }
        };
        await progress.ShowDialog(this);
        if (progress.Failure is not null) throw progress.Failure;
        if (progress.Result is not { } queueResult)
        {
            _status.Text = label + "已取消。";
            return;
        }
        var completed = queueResult.Items.Count(item => item.Status == PngExportTaskStatus.Completed);
        var failed = queueResult.Items.Count(item => item.Status == PngExportTaskStatus.Failed);
        var skipped = queueResult.Items.Count - completed - failed;
        _status.Text = (queueResult.Cancelled ? label + "已取消" : label + "完成") +
            $"：成功 {completed}，失败 {failed}，取消/未执行 {skipped}。输出位置：{queueResult.Directory}";
    }

    private async Task ExportSelectedPng()
    {
        if (_exportingPng) return;
        _exportingPng = true;
        try
        {
            var selected = _session.SelectedLayerIds.ToArray();
            if (selected.Length == 0)
                throw new InvalidOperationException("请先在画布或图层列表中选择要导出的组/图层。");
            var pending = new PendingDocumentOperation(_session, () => _native.CurrentPath,
                includeSelection: true);
            var snapshot = SelectionProject.Create(pending.Snapshot, selected, _canvas.MoveWholeGroup);
            NativeImaging.ValidateImages(snapshot);
            var warnings = ExportPreflight.Inspect(snapshot, LocalFontRegistry.IsMissing);
            PngExportRegion? cropRegion = null;
            string cropNote;
            try
            {
                cropRegion = PngExportRegion.FromLayers(snapshot, snapshot.Layers);
                cropNote = "裁切使用选中图层旋转后的可视并集，向外取整到整数像素并与画布相交。" +
                    "隐藏或锁定成员仍计入几何范围；固定背景和自动混合背景不随选中内容导出，透明背景优先。";
            }
            catch (InvalidOperationException)
            {
                cropNote = "当前选中内容与画布没有交集，裁切模式不可用；仍可导出完整画布。";
            }
            var setup = new PngExportQueueDialog(snapshot, warnings, ChooseExportDirectory,
                _preferences.DefaultExportQualityPolicy, _exportPresetLibrary,
                selectionCropRegion: cropRegion, selectionCropNote: cropNote);
            var request = await setup.ShowDialog<PngExportQueueRequest?>(this);
            if (request is null)
            {
                _status.Text = "已取消导出选中内容。";
                return;
            }
            pending.EnsureUnchanged("Project or selection changed during selection export. Please start again.");
            await RunPngQueueSnapshotAsync(snapshot, request, "选中内容导出");
        }
        finally { _exportingPng = false; }
    }

    private async Task BatchRenderProjects()
    {
        if (_batchRendering) return;
        _batchRendering = true;
        try
        {
            var setup = new ProjectBatchRenderDialog(ChooseBatchProjects, ChooseExportDirectory,
                _preferences.DefaultExportQualityPolicy, _exportPresetLibrary);
            var request = await setup.ShowDialog<ProjectBatchRenderRequest?>(this);
            if (request is null)
            {
                _status.Text = "已取消跨工程批量导出。";
                return;
            }
            var progress = new ProjectBatchProgressDialog();
            progress.Opened += async (_, _) =>
            {
                try
                {
                    var result = await ProjectRenderBatch.ExecuteAsync(
                        new ProjectRenderPlan(request.OutputParent, request.ProjectPaths, request.Tasks,
                            request.QualityPolicy),
                        NativeImaging.ValidateImages,
                        (document, transparent, width) =>
                            Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
                                ? PngExporter.Render(document, transparent, NativeImaging.Decoder, width)
                                : Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                                    PngExporter.Render(document, transparent, NativeImaging.Decoder, width)),
                        (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
                        new ProtectedSourcePaths(), () => _native.CurrentPath,
                        LocalFontRegistry.IsMissing, progress.Progress, progress.Token,
                        readImageDimensions: ImageAssetValidator.ReadEncodedDimensions,
                        qualityPolicy: request.QualityPolicy);
                    progress.Complete(result);
                }
                catch (OperationCanceledException)
                {
                    progress.Stop("跨工程批量导出已取消，已完成文件仍保留。");
                }
                catch (Exception error) { progress.Fail(error); }
            };
            await progress.ShowDialog(this);
            if (progress.Failure is not null) throw progress.Failure;
            if (progress.Result is not { } result)
            {
                _status.Text = "跨工程批量导出已取消。";
                return;
            }
            var completed = result.Projects.Count(project => project.Status == ProjectRenderStatus.Completed);
            var failed = result.Projects.Count(project => project.Status == ProjectRenderStatus.Failed);
            _status.Text = (result.Cancelled ? "跨工程批量导出已取消" : "跨工程批量导出完成") +
                $"：成功工程 {completed}，失败工程 {failed}。输出位置：{result.Directory}";
        }
        finally { _batchRendering = false; }
    }

    private async Task<IReadOnlyList<string>> ChooseBatchProjects(Window owner)
    {
        IReadOnlyList<string> paths;
        if (_batchProjectPathsPicker is not null) paths = await _batchProjectPathsPicker();
        else
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择多份原生工程", AllowMultiple = true,
                FileTypeFilter = [NativeProjectType()]
            });
            paths = files.Select(LocalPath).ToArray();
        }
        if (paths.Count > 0) EnsureCanvasIdle();
        return paths;
    }

    private async Task<string?> ChooseExportDirectory(Window owner)
    {
        if (_exportDirectoryPicker is not null)
            return CompletePathChoice(await _exportDirectoryPicker());
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择批量导出 PNG 的父目录", AllowMultiple = false
        });
        return CompletePathChoice(folders.Count == 0 ? null : folders[0].TryGetLocalPath());
    }

    private async Task<bool> ReviewExportWarnings(IReadOnlyList<ExportWarning> warnings)
    {
        var dialog = new Window
        {
            Title = "PNG 导出检查", Width = 620, Height = 420,
            MinWidth = 380, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close(false);
        var proceed = new Button { Content = "继续导出" };
        proceed.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(proceed);
        var panel = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var heading = new TextBlock { Text = $"发现 {warnings.Count} 项导出提示。确认符合预期后可继续导出。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        panel.Children.Add(new ScrollViewer { Content = new TextBlock
        {
            Text = string.Join("\n\n", warnings.Select(w => $"{UiText.LayerName(w.LayerName)}：{UiText.Translate(w.Message)}")),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        } });
        dialog.Content = panel;
        var accepted = await dialog.ShowDialog<bool>(this);
        if (accepted) EnsureCanvasIdle();
        return accepted;
    }

    private static string LocalPath(IStorageFile file) => file.TryGetLocalPath()
        ?? throw new IOException("A local path is required.");

    private static FilePickerFileType NativeProjectType() =>
        new($"工程（{NativeProjectFile.FilterPattern}）")
        {
            Patterns = [NativeProjectFile.FilterPattern],
            MimeTypes = ["application/zip"]
        };

    private static FilePickerFileType CompatibleProjectType() =>
        new("兼容工程")
        {
            Patterns = ["*.phipreview", "*.phifree"]
        };

    private static FilePickerFileType AllFilesType() => new("所有文件") { Patterns = ["*"] };

    private void RefreshTitle()
    {
        var name = _native.CurrentPath is not null
            ? Path.GetFileName(_native.CurrentPath)
            : _projects.SourceLabel is null ? "未命名" : $"未命名（导入自 {_projects.SourceLabel}）";
        Title = $"{App.ProductDisplayName} — {name}{(_native.IsDirty ? " *" : "")}";
        // Partial refreshes also reach this path; update committed-document feedback without
        // reloading property, canvas or background drafts.
        RefreshHistoryFeedback();
    }

    private void RefreshAfterSave(EditorDocument document, IReadOnlyList<Guid> selection)
    {
        // Saving changes the path/savepoint, not pending inputs. A picker may have outlived
        // its selection, in which case reconcile the current context through a full refresh.
        if (ReferenceEquals(document, _session.Document) && selection.SequenceEqual(_session.SelectedLayerIds))
            RefreshTitle();
        else Refresh();
    }

    private void UndoLastEdit() => ApplyHistoryStep(redo: false);

    private void RedoLastEdit() => ApplyHistoryStep(redo: true);

    private void ApplyHistoryStep(bool redo)
    {
        var label = redo ? _session.RedoLabel : _session.UndoLabel;
        var changed = redo ? _session.Redo() : _session.Undo();
        Refresh();
        if (!changed)
        {
            _status.Text = redo ? "没有可重做的操作。" : "没有可撤销的操作。";
            return;
        }
        var next = redo ? _session.UndoLabel : _session.RedoLabel;
        _status.Text = $"已{(redo ? "重做" : "撤销")}：{label}。" +
            (next is null ? "" : $"可{(redo ? "撤销" : "重做")}：{next}。");
    }

    private void RefreshHistoryFeedback()
    {
        UpdateHistoryAction(_undoMenuItem, "Undo", _session.UndoLabel, "Ctrl+Z");
        UpdateHistoryAction(_redoMenuItem, "Redo", _session.RedoLabel, "Ctrl+Y / Ctrl+Shift+Z");
        if (_undoButton is not null)
        {
            _undoButton.IsEnabled = _session.UndoLabel is not null;
            ToolTip.SetTip(_undoButton, HistoryToolTip("Undo", _session.UndoLabel, "Ctrl+Z"));
        }

        var undo = _session.RecentUndoLabels;
        var redo = _session.RecentRedoLabels;
        _historySummary.Text =
            (undo.Count == 0 ? "可撤销：无" :
                "可撤销：\n" + string.Join("\n", undo.Select((label, index) => $"{index + 1}. {label}"))) +
            "\n\n" +
            (redo.Count == 0 ? "可重做：无" :
                "可重做：\n" + string.Join("\n", redo.Select((label, index) => $"{index + 1}. {label}")));
    }

    private static void UpdateHistoryAction(MenuItem? item, string fallback,
        string? label, string gesture)
    {
        if (item is null) return;
        item.Header = label is null ? UiText.Translate(fallback) : $"{(fallback == "Undo" ? "撤销" : "重做")}：{label}";
        item.IsEnabled = label is not null;
        ToolTip.SetTip(item, HistoryToolTip(fallback, label, gesture));
    }

    private static string HistoryToolTip(string fallback, string? label, string gesture) =>
        label is null
            ? $"没有可{(fallback == "Undo" ? "撤销" : "重做")}的操作（{gesture}）"
            : $"{(fallback == "Undo" ? "撤销" : "重做")}：{label}（{gesture}）";

    private void Refresh() => Refresh(refreshProperties: true);

    private void Refresh(bool refreshProperties, bool refreshText = true)
    {
        RefreshTitle();
        RefreshLayerList();
        RefreshGroups();
        RefreshCanvasFields();
        if (refreshProperties) RefreshPropertyFields(refreshText);
        var missing = _session.Document.Layers.Where(layer => layer.Kind == LayerKind.Text &&
            LocalFontRegistry.IsMissing(layer.FontFamily)).Select(layer => layer.Name).Distinct().ToArray();
        _fontNotice.Text = missing.Length == 0 ? "" :
            "以下图层缺少本地字体：" + string.Join(", ", missing) +
            "。已使用微软雅黑替代；导入原字体即可恢复。";
        if (_fonts.Warnings.Count > 0)
            _fontNotice.Text += $" {_fonts.Warnings.Count} 个本地字体文件无法加载。";
        _fontNotice.IsVisible = _fontNotice.Text.Length > 0;
        if (!_session.IsDirty && _recovery.HasSnapshot) _recovery.OnDocumentClean();
        _canvas.Refresh();
        UpdateZoomStatus();
    }

    private void UpdateZoomStatus()
    {
        var percent = _canvas.ZoomPercent;
        _zoomStatus.Text = percent > 0 ? $"缩放 {Math.Round(percent):0}%" : "缩放 --";
        SetAccessibility(_zoomStatus, "视图" + _zoomStatus.Text,
            "当前视图缩放；Ctrl+0 适合窗口，Ctrl+1 实际大小，Ctrl+2 适合所选图层。");
    }

    private void ZoomToSelection()
    {
        if (_session.SelectedLayerIds.Count == 0)
        {
            _status.Text = "请先选择要适合视图的图层。";
            return;
        }
        if (!_canvas.ZoomToSelection())
        {
            _status.Text = "请先完成当前拖动或缩放操作。";
            return;
        }
        _status.Text = "已适合所选图层。";
    }

    /// <summary>Refresh list and canvas only; property, canvas and batch drafts stay untouched.</summary>
    private void RefreshLayerOnly()
    {
        RefreshTitle();
        RefreshLayerList();
        RefreshGroups();
        _canvas.Refresh();
    }

    private bool RefreshLayerList()
    {
        Guid[] selected = _session.SelectedLayerIds.ToArray();
        var oldSelection = _layers.SelectedItems?.OfType<LayerListItem>().Select(item => item.Id).ToArray() ?? [];
        var sameSelection = selected.SequenceEqual(oldSelection);
        var anchor = _layers.Selection.AnchorIndex;
        var anchorId = anchor >= 0 && anchor < _layerItems.Count ? _layerItems[anchor].Id : (Guid?)null;
        var hadFocus = _layers.IsKeyboardFocusWithin;
        var existing = _layerItems.ToDictionary(item => item.Id);
        _layerMatches = LayerSearch.Matches(_session.Document, _layerSearch.Text).ToArray();
        var matchSet = _layerMatches.ToHashSet();
        var filterActive = IsLayerFilterActive;
        if (_session.SelectedLayerId is Guid selectedId && matchSet.Contains(selectedId))
            _layerMatchCursor = Array.IndexOf(_layerMatches, selectedId);
        else if (_layerMatchCursor >= _layerMatches.Length)
            _layerMatchCursor = _layerMatches.Length - 1;
        UpdateLayerMatchInfo();
        var visibleLayers = filterActive
            ? _session.Document.Layers.Where(layer => matchSet.Contains(layer.Id))
            : _session.Document.Layers.AsEnumerable();
        var items = visibleLayers.Reverse().Select(layer =>
        {
            var label = LayerRowLabel(layer);
            var match = matchSet.Contains(layer.Id);
            return existing.TryGetValue(layer.Id, out var item) && item.Label == label &&
                item.Visible == layer.Visible && item.Locked == layer.Locked && item.IsMatch == match
                ? item : new LayerListItem(layer.Id, label, layer.Visible, layer.Locked, match);
        }).ToArray();
        var trimmed = false;
        if (filterActive)
        {
            var visibleIds = items.Select(item => item.Id).ToHashSet();
            var trimmedSelection = selected.Where(visibleIds.Contains).ToArray();
            if (!trimmedSelection.SequenceEqual(selected))
            {
                selected = trimmedSelection;
                _session.SetSelection(selected);
                trimmed = true;
                sameSelection = false;
            }
        }
        UpdateLayerFilterNotice(items.Length, filterActive);
        var changed = !_layerItems.SequenceEqual(items);
        var autoScroll = _layers.AutoScrollToSelectedItem;
        _updatingLayerList = true;
        _layers.AutoScrollToSelectedItem = false;
        try
        {
            // Keep focus inside the list before an edited/removed row can detach.
            if (hadFocus && changed) _layers.Focus();
            if (changed)
            {
                var ids = items.Select(item => item.Id).ToHashSet();
                for (var index = _layerItems.Count - 1; index >= 0; index--)
                    if (!ids.Contains(_layerItems[index].Id)) _layerItems.RemoveAt(index);
                for (var index = 0; index < items.Length; index++)
                {
                    var current = existing.TryGetValue(items[index].Id, out var previous)
                        ? _layerItems.IndexOf(previous) : -1;
                    if (current < 0) _layerItems.Insert(index, items[index]);
                    else
                    {
                        if (current != index) _layerItems.Move(current, index);
                        if (!ReferenceEquals(_layerItems[index], items[index])) _layerItems[index] = items[index];
                    }
                }
            }
            if (_layers.SelectedItems is { } selectedItems)
            {
                var byId = items.ToDictionary(item => item.Id);
                var desired = selected.Select(id => byId[id]).ToArray();
                if (!selectedItems.OfType<LayerListItem>().SequenceEqual(desired))
                {
                    selectedItems.Clear();
                    foreach (var item in desired) selectedItems.Add(item);
                }
            }
            var primaryIndex = Array.FindIndex(items, item => item.Id == _session.SelectedLayerId);
            _layers.Selection.AnchorIndex = sameSelection && anchorId is Guid id
                ? Array.FindIndex(items, item => item.Id == id) : primaryIndex;
            if (!sameSelection && primaryIndex >= 0) _layers.ScrollIntoView(primaryIndex);
        }
        finally
        {
            _layers.AutoScrollToSelectedItem = autoScroll;
            _updatingLayerList = false;
        }
        UpdateLayerSelectionSummary();
        return trimmed;
    }

    private void UpdateLayerSelectionSummary()
    {
        var layers = _session.SelectedLayerIds
            .Select(id => _session.Document.Find(id)).ToArray();
        if (layers.Length == 0)
        {
            _layerSelectionSummary.Text = "未选择图层";
            return;
        }
        var hidden = layers.Count(layer => !layer.Visible);
        var locked = layers.Count(layer => layer.Locked);
        var suffix = hidden == 0 && locked == 0 ? ""
            : $" · 隐藏 {hidden} · 锁定 {locked}";
        _layerSelectionSummary.Text = $"已选 {layers.Length}{suffix}";
    }

    private void RefreshGroups()
    {
        var items = _session.Document.Layers.AsEnumerable().Reverse()
            .Where(layer => layer.GroupId is Guid)
            .GroupBy(layer => layer.GroupId!.Value)
            .Select(group => new GroupListItem(group.Key, GroupLabel(group.ToArray())))
            .ToArray();
        if (_selectedGroupId is not Guid selected ||
            items.All(item => item.GroupId != selected))
            _selectedGroupId = items.FirstOrDefault()?.GroupId;
        _expandedGroups.RemoveWhere(id => items.All(item => item.GroupId != id));
        _groupCardsHost.Children.Clear();
        _groupHeaderButtons.Clear();
        foreach (var item in items)
        {
            var header = new Button
            {
                Name = "GroupOverviewHeader",
                Content = item.Label,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left
            };
            SetAccessibility(header, "编组：" + item.Label,
                "单击选择该编组作为整组操作对象。");
            header.Click += (_, _) => SelectGroupContext(item.GroupId);
            _groupHeaderButtons[item.GroupId] = header;
            var members = new StackPanel { Spacing = 1 };
            foreach (var layer in _session.Document.Layers.Where(layer =>
                layer.GroupId == item.GroupId))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                var memberButton = new Button
                {
                    Name = "GroupOverviewMember",
                    Content = LayerRowLabel(layer),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(16, 1, 2, 1),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    FontSize = 11
                };
                SetAccessibility(memberButton, "编组图层：" + LayerRowLabel(layer),
                    "选择该编组成员图层。");
                memberButton.Click += (_, _) =>
                {
                    _selectedGroupId = item.GroupId;
                    _session.SelectedLayerId = layer.Id;
                    Refresh();
                };
                row.Children.Add(memberButton);
                var visibility = new Button
                {
                    Name = "GroupMemberVisibility",
                    Content = layer.Visible ? "隐藏" : "显示",
                    FontSize = 10, Padding = new Thickness(3, 0), MinWidth = 34,
                    Focusable = false
                };
                SetAccessibility(visibility,
                    (layer.Visible ? "隐藏编组成员：" : "显示编组成员：") + LayerRowLabel(layer),
                    layer.Visible ? "隐藏该编组成员。" : "显示该编组成员。");
                visibility.Click += (_, _) => ToggleLayerInline(layer.Id,
                    value => value.Visible = !value.Visible,
                    layer.Visible ? "成员已隐藏。" : "成员已显示。", "显示或隐藏图层");
                Grid.SetColumn(visibility, 1);
                row.Children.Add(visibility);
                var locked = new Button
                {
                    Name = "GroupMemberLock",
                    Content = layer.Locked ? "解锁" : "锁定",
                    FontSize = 10, Padding = new Thickness(3, 0), MinWidth = 34,
                    Focusable = false
                };
                SetAccessibility(locked,
                    (layer.Locked ? "解锁编组成员：" : "锁定编组成员：") + LayerRowLabel(layer),
                    layer.Locked ? "解锁该编组成员。" : "锁定该编组成员。");
                locked.Click += (_, _) => ToggleLayerInline(layer.Id,
                    value => value.Locked = !value.Locked,
                    layer.Locked ? "成员已解锁。" : "成员已锁定。", "锁定或解锁图层");
                Grid.SetColumn(locked, 2);
                row.Children.Add(locked);
                members.Children.Add(row);
            }
            var card = new Expander
            {
                Name = "GroupOverviewCard",
                Header = header,
                Content = members,
                IsExpanded = _expandedGroups.Contains(item.GroupId)
            };
            card.Expanded += (_, _) => _expandedGroups.Add(item.GroupId);
            card.Collapsed += (_, _) => _expandedGroups.Remove(item.GroupId);
            _groupCardsHost.Children.Add(card);
        }
        if (_groupOverviewExpander is not null)
            _groupOverviewExpander.IsVisible = items.Length > 0;
        UpdateGroupHeaderSelection();
        UpdateGroupButtons();
    }

    private static string GroupLabel(IReadOnlyList<Layer> members)
    {
        var title = members.FirstOrDefault(layer => layer.SongRole == SongRole.Title &&
                !string.IsNullOrWhiteSpace(layer.Text))?.Text
            ?? members.FirstOrDefault(layer => layer.SongRole == SongRole.Artwork)?.Name
            ?? members.FirstOrDefault(layer => !string.IsNullOrWhiteSpace(layer.Name))?.Name
            ?? "组";
        var hidden = members.Count(layer => !layer.Visible);
        var locked = members.Count(layer => layer.Locked);
        return $"{UiText.LayerName(title)} · {members.Count} 层" +
            (hidden > 0 ? $" · 隐藏 {hidden}" : "") +
            (locked > 0 ? $" · 锁定 {locked}" : "");
    }

    private static string LayerRowLabel(Layer layer) =>
        $"{(layer.Visible ? "" : "[隐藏] ")}{(layer.Locked ? "[锁定] " : "")}" +
        $"{UiText.LayerKindName(layer.Kind)}：{UiText.LayerName(layer.Name)}";

    private IReadOnlyList<Layer> SelectedGroupMembers()
    {
        return _selectedGroupId is Guid groupId
            ? _session.Document.Layers.Where(layer => layer.GroupId == groupId).ToArray()
            : [];
    }

    private void SelectGroupContext(Guid groupId)
    {
        _selectedGroupId = groupId;
        UpdateGroupHeaderSelection();
        UpdateGroupButtons();
        _status.Text = "已选择编组；可执行整组选择、显隐、锁定或取消编组。";
    }

    private void UpdateGroupHeaderSelection()
    {
        foreach (var (groupId, header) in _groupHeaderButtons)
            header.Background = groupId == _selectedGroupId
                ? new SolidColorBrush(Color.FromArgb(75, 80, 160, 255))
                : Brushes.Transparent;
    }

    private void UpdateGroupButtons()
    {
        var members = SelectedGroupMembers();
        var enabled = members.Count > 0;
        _selectGroupButton.IsEnabled = enabled;
        _toggleGroupVisibilityButton.IsEnabled = enabled;
        _toggleGroupLockButton.IsEnabled = enabled;
        _ungroupButton.IsEnabled = enabled;
        if (!enabled)
        {
            _toggleGroupVisibilityButton.Content = "隐藏整组";
            _toggleGroupLockButton.Content = "锁定整组";
            return;
        }
        _toggleGroupVisibilityButton.Content = members.All(layer => layer.Visible)
            ? "隐藏整组" : "显示整组";
        _toggleGroupLockButton.Content = members.All(layer => layer.Locked)
            ? "解锁整组" : "锁定整组";
    }

    private void SelectGroupFromOverview()
    {
        var members = SelectedGroupMembers();
        if (members.Count == 0) return;
        if (_filterLayerMatches.IsChecked == true) _filterLayerMatches.IsChecked = false;
        _session.SetSelection(members.Select(layer => layer.Id));
        Refresh();
        _status.Text = $"已选择整组 {members.Count} 个图层。";
    }

    private void ToggleSelectedGroupVisibility()
    {
        var members = SelectedGroupMembers();
        if (members.Count == 0) return;
        var visible = !members.All(layer => layer.Visible);
        var ids = members.Select(layer => layer.Id).ToHashSet();
        RunLayerAction(() => _session.UpdateDocument(document =>
        {
            foreach (var layer in document.Layers.Where(layer => ids.Contains(layer.Id)))
                layer.Visible = visible;
        }, "显示或隐藏整组"));
        _status.Text = visible ? "已显示整组。" : "已隐藏整组。";
    }

    private void ToggleSelectedGroupLock()
    {
        var members = SelectedGroupMembers();
        if (members.Count == 0) return;
        var locked = !members.All(layer => layer.Locked);
        var ids = members.Select(layer => layer.Id).ToHashSet();
        RunLayerAction(() => _session.UpdateDocument(document =>
        {
            foreach (var layer in document.Layers.Where(layer => ids.Contains(layer.Id)))
                layer.Locked = locked;
        }, "锁定或解锁整组"));
        _status.Text = locked ? "已锁定整组。" : "已解锁整组。";
    }

    private void UngroupSelectedGroup()
    {
        var members = SelectedGroupMembers();
        if (members.Count == 0) return;
        RunLayerAction(() => _session.UngroupLayers(members.Select(layer => layer.Id)));
        _status.Text = "已取消整组编组。";
    }

    private Control BuildLayerRow(LayerListItem? item)
    {
        if (item is null) return new TextBlock();
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            // Leave the inner floating bar space to expand without covering row actions.
            Margin = new Thickness(1, 1, 17, 1)
        };
        grid.ContextMenu = BuildLayerContextMenu(item);
        var label = new TextBlock
        {
            Text = item.Label,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Padding = new Thickness(2, 0),
            Background = item.IsMatch
                ? new SolidColorBrush(Color.FromArgb(90, 255, 210, 74))
                : Brushes.Transparent
        };
        ToolTip.SetTip(label, item.Label);
        SetAccessibility(label, "图层：" + item.Label, item.Label);
        grid.Children.Add(label);
        var visibility = new Button
        {
            Name = "LayerVisibilityButton",
            Content = item.Visible ? "隐藏" : "显示",
            FontSize = 11, Padding = new Thickness(4, 1), MinWidth = 42,
            Margin = new Thickness(2, 0), Focusable = false
        };
        ToolTip.SetTip(visibility, item.Visible ? "隐藏此图层" : "显示此图层");
        SetAccessibility(visibility, item.Visible ? "隐藏图层" : "显示图层",
            item.Visible ? "隐藏此图层" : "显示此图层");
        visibility.Click += (_, e) =>
        {
            e.Handled = true;
            ToggleLayerInline(item.Id, layer => layer.Visible = !layer.Visible,
                item.Visible ? "图层已隐藏。" : "图层已显示。", "显示或隐藏图层");
        };
        Grid.SetColumn(visibility, 1);
        grid.Children.Add(visibility);
        var locked = new Button
        {
            Name = "LayerLockButton",
            Content = item.Locked ? "解锁" : "锁定",
            FontSize = 11, Padding = new Thickness(4, 1), MinWidth = 42,
            Margin = new Thickness(2, 0), Focusable = false
        };
        ToolTip.SetTip(locked, item.Locked ? "解锁此图层" : "锁定此图层");
        SetAccessibility(locked, item.Locked ? "解锁图层" : "锁定图层",
            item.Locked ? "解锁此图层" : "锁定此图层");
        locked.Click += (_, e) =>
        {
            e.Handled = true;
            ToggleLayerInline(item.Id, layer => layer.Locked = !layer.Locked,
                item.Locked ? "图层已解锁。" : "图层已锁定。", "锁定或解锁图层");
        };
        Grid.SetColumn(locked, 2);
        grid.Children.Add(locked);
        return grid;
    }

    private ContextMenu BuildLayerContextMenu(LayerListItem item)
    {
        var menu = new ContextMenu { Name = "LayerRowContextMenu" };
        SetAccessibility(menu, "图层行操作", "复制、删除、重命名或切换此图层的状态。");
        var rename = new MenuItem { Name = "LayerContextRename", Header = "重命名图层…" };
        SetAccessibility(rename, "重命名图层", "重命名当前右键点击的图层。");
        rename.Click += async (_, _) =>
        {
            try { await RunFileOperation(() => RenameLayerCoreAsync(item.Id)); }
            catch (Exception error) { ShowStatusError(error.Message); }
        };
        menu.Items.Add(rename);
        AddLayerContextItem(menu, "复制图层", "LayerContextDuplicate",
            () => RunLayerAction(() => _session.DuplicateLayers([item.Id])));
        AddLayerContextItem(menu, "删除图层", "LayerContextDelete",
            () => RunLayerAction(() => _session.DeleteLayers([item.Id])));
        menu.Items.Add(new Separator());
        AddLayerContextItem(menu, item.Visible ? "隐藏图层" : "显示图层",
            "LayerContextVisibility",
            () => ToggleLayerInline(item.Id, layer => layer.Visible = !layer.Visible,
                item.Visible ? "图层已隐藏。" : "图层已显示。", "显示或隐藏图层"));
        AddLayerContextItem(menu, item.Locked ? "解锁图层" : "锁定图层",
            "LayerContextLock",
            () => ToggleLayerInline(item.Id, layer => layer.Locked = !layer.Locked,
                item.Locked ? "图层已解锁。" : "图层已锁定。", "锁定或解锁图层"));
        return menu;
    }

    private void AddLayerContextItem(ContextMenu menu, string header, string name, Action action)
    {
        var item = new MenuItem { Name = name, Header = header };
        SetAccessibility(item, header, header);
        item.Click += (_, _) =>
        {
            try { EnsureCanvasIdle(); action(); }
            catch (Exception error) { ShowStatusError(error.Message); }
        };
        menu.Items.Add(item);
    }

    private Task RenameSelectedLayer() => RenameLayerCoreAsync(SelectedLayer());

    private async Task RenameLayerCoreAsync(Guid id)
    {
        EnsureCanvasIdle();
        var layer = _session.Document.Find(id);
        if (layer.Locked)
            throw new InvalidOperationException("图层已锁定，请先解锁再重命名。");
        var value = await new NamePromptDialog("重命名图层", "输入新的图层名称", layer.Name, 200)
            .ShowDialog<string?>(this);
        if (value is null) return;
        var name = value.Trim();
        if (name.Length == 0) throw new InvalidOperationException("图层名称不能为空。");
        if (name.Length > 200) throw new InvalidOperationException("图层名称不能超过 200 个字符。");
        if (name == layer.Name)
        {
            _status.Text = "图层名称未更改。";
            return;
        }
        _session.UpdateLayer(id, target => target.Name = name, "重命名图层");
        Refresh();
        _status.Text = $"图层已重命名为“{name}”。Ctrl+Z 可撤销。";
    }

    private void OnLayerListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ClearLayerListDragState();
        if (!e.GetCurrentPoint(_layers).Properties.IsLeftButtonPressed ||
            e.Source is not Visual source ||
            source.FindAncestorOfType<Button>(includeSelf: true) is not null ||
            source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not LayerListItem item)
            return;
        var selectedIds = _session.SelectedLayerIds.Contains(item.Id)
            ? _session.SelectedLayerIds.ToArray() : [item.Id];
        _layerPointerPress = new LayerPointerPress(
            e.Pointer, e.GetPosition(_layers), selectedIds);
    }

    private void OnLayerListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_layerPointerPress is not { } press || press.Pointer != e.Pointer) return;
        if (!e.GetCurrentPoint(_layers).Properties.IsLeftButtonPressed)
        {
            ClearLayerListDragState();
            return;
        }
        var position = e.GetPosition(_layers);
        if (_layerDragPointer is null)
        {
            var delta = position - press.Origin;
            if (Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) < 6) return;
            if (IsLayerFilterActive)
            {
                ClearLayerListDragState();
                _status.Text = "筛选视图中不能拖拽重排；请关闭“仅显示匹配项”后重试。";
                return;
            }
            if (_canvas.IsInteracting || _session.ActiveGestureIds.Count > 0) return;
            var ids = press.SelectedIds;
            _session.SetSelection(ids);
            _layerDragIds = ids;
            _layerDragPointer = press.Pointer;
            press.Pointer.Capture(_layers);
            _status.Text = $"正在移动 {ids.Length} 个图层；上方橙色线为释放位置，Esc 取消。";
        }
        UpdateLayerListDropTarget(position);
        AutoScrollLayerList(position);
    }

    private void OnLayerListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_layerPointerPress is null) return;
        if (_layerDragPointer is null)
        {
            _layerPointerPress = null;
            return;
        }
        UpdateLayerListDropTarget(e.GetPosition(_layers));
        var ids = _layerDragIds;
        var targetId = _layerDragTargetId;
        var above = _layerDragAbove;
        var targetLabel = targetId is Guid id
            ? _layerItems.FirstOrDefault(item => item.Id == id)?.Label : null;
        ClearLayerListDragState();
        e.Handled = true;
        if (targetId is not Guid target) return;
        try
        {
            var changed = false;
            RunLayerAction(() => changed = _session.MoveLayersRelativeTo(ids, target, above));
            Refresh();
            _status.Text = changed
                ? $"已将 {ids.Length} 个图层放到“{targetLabel}”{(above ? "上方" : "下方")}；Ctrl+Z 可撤销。"
                : "图层顺序未变化。";
        }
        catch (Exception error)
        {
            Refresh();
            ShowStatusError(error.Message);
        }
    }

    private void OnLayerListPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if ((_layerDragPointer ?? _layerPointerPress?.Pointer) != e.Pointer) return;
        ClearLayerListDragState();
    }

    private void UpdateLayerListDropTarget(Point position)
    {
        if (!TryFindLayerDropTarget(position, out var targetId, out var above))
        {
            _layerDragTargetId = null;
            _layerDropIndicator.IsVisible = false;
            return;
        }
        if (_layerDragIds.Contains(targetId))
        {
            _layerDragTargetId = null;
            _layerDropIndicator.IsVisible = false;
            _status.Text = "释放目标位于当前选择中；请移到其他图层的上方或下方。";
            return;
        }
        var changed = _layerDragTargetId != targetId || _layerDragAbove != above;
        _layerDragTargetId = targetId;
        _layerDragAbove = above;
        var row = _layers.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => item.DataContext is LayerListItem layer && layer.Id == targetId);
        var top = row?.TranslatePoint(default, _layerListHost);
        if (row is null || top is null)
        {
            _layerDropIndicator.IsVisible = false;
            return;
        }
        var y = top.Value.Y + (above ? 0 : row.Bounds.Height);
        _layerDropIndicator.Margin = new Thickness(0, y, 0, 0);
        _layerDropIndicator.IsVisible = y >= 0 && y <= _layerListHost.Bounds.Height;
        if (changed)
        {
            var label = _layerItems.FirstOrDefault(item => item.Id == targetId)?.Label ?? "目标图层";
            _status.Text = $"释放到“{label}”{(above ? "上方" : "下方")}；松开应用，Esc 取消。";
        }
    }

    private void AutoScrollLayerList(Point position)
    {
        var scroll = _layers.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null) return;
        var current = scroll.Offset.Y;
        var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var next = LayerListAutoScroll.NextOffset(
            current, maximum, position.Y, scroll.Viewport.Height);
        if (Math.Abs(next - current) < 1e-6) return;
        scroll.Offset = new Vector(scroll.Offset.X, next);
        UpdateLayerListDropTarget(position);
    }

    private bool TryFindLayerDropTarget(Point point, out Guid targetId, out bool above)
    {
        targetId = default;
        above = false;
        var rows = _layers.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(row => (Row: row, Item: row.DataContext as LayerListItem))
            .Where(candidate => candidate.Item is not null)
            .Select(candidate => (candidate.Row, Item: candidate.Item!,
                Index: _layerItems.IndexOf(candidate.Item!)))
            .Where(candidate => candidate.Index >= 0)
            .OrderBy(candidate => candidate.Index)
            .ToArray();
        if (rows.Length == 0) return false;
        var first = rows[0].Row.TranslatePoint(default, _layers);
        var last = rows[^1].Row.TranslatePoint(default, _layers);
        if (first is not null && point.Y < first.Value.Y)
        {
            targetId = rows[0].Item.Id;
            above = true;
            return true;
        }
        foreach (var row in rows)
        {
            var top = row.Row.TranslatePoint(default, _layers);
            if (top is null) continue;
            var bottom = top.Value.Y + row.Row.Bounds.Height;
            if (point.Y > bottom) continue;
            targetId = row.Item.Id;
            above = point.Y < top.Value.Y + row.Row.Bounds.Height / 2;
            return true;
        }
        if (last is null) return false;
        targetId = rows[^1].Item.Id;
        above = false;
        return true;
    }

    private void ClearLayerListDragState()
    {
        var pointer = _layerDragPointer;
        _layerPointerPress = null;
        _layerDragPointer = null;
        _layerDragIds = [];
        _layerDragTargetId = null;
        _layerDragAbove = false;
        _layerDropIndicator.IsVisible = false;
        pointer?.Capture(null);
    }

    private void ToggleLayerInline(Guid id, Action<Layer> change, string message, string historyLabel)
    {
        try
        {
            EnsureCanvasIdle();
            _session.UpdateLayer(id, change, historyLabel);
            RefreshLayerOnly();
            _status.Text = message;
        }
        catch (Exception error) { ShowStatusError(error.Message); }
    }

    private void UpdateLayerMatchInfo()
    {
        var query = _layerSearch.Text ?? "";
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        _layerSearchFeedback.IsVisible = hasQuery;
        _filterLayerMatches.IsEnabled = hasQuery;
        if (!hasQuery)
        {
            _layerMatchInfo.Text = "输入名称或文字内容进行查找。";
            _previousLayerMatch.IsEnabled = false;
            _nextLayerMatch.IsEnabled = false;
            _selectAllLayerMatches.IsEnabled = false;
            _layerFilterNotice.IsVisible = false;
            return;
        }
        var count = _layerMatches.Length;
        _previousLayerMatch.IsEnabled = count > 0;
        _nextLayerMatch.IsEnabled = count > 0;
        _selectAllLayerMatches.IsEnabled = count > 0;
        _layerMatchInfo.Text = count == 0
            ? "没有匹配项。"
            : _layerMatchCursor >= 0
                ? $"匹配 {count} 个图层；当前第 {_layerMatchCursor + 1} 个。"
                : $"匹配 {count} 个图层。";
    }

    private bool IsLayerFilterActive =>
        _filterLayerMatches.IsChecked == true && !string.IsNullOrWhiteSpace(_layerSearch.Text);

    private void UpdateLayerFilterNotice(int visibleCount, bool active)
    {
        if (!active)
        {
            _layerFilterNotice.IsVisible = false;
            _layerFilterNotice.Text = "";
            return;
        }
        _layerFilterNotice.IsVisible = true;
        _layerFilterNotice.Text = $"仅显示 {visibleCount} / {_session.Document.Layers.Count} 个匹配图层；" +
            "筛选状态下不在结果中的已选图层会取消选择。";
    }

    private void MoveLayerMatch(int delta)
    {
        if (_layerMatches.Length == 0)
        {
            _status.Text = string.IsNullOrWhiteSpace(_layerSearch.Text) ? "请输入查找内容。" : "没有匹配项。";
            return;
        }
        var current = _session.SelectedLayerId is Guid selected ? Array.IndexOf(_layerMatches, selected) : -1;
        if (current < 0) current = delta > 0 ? -1 : 0;
        var cursor = (current + delta + _layerMatches.Length) % _layerMatches.Length;
        var id = _layerMatches[cursor];
        _session.SetSelection([id]);
        _layerMatchCursor = cursor;
        RefreshLayerOnly();
        ScrollToLayer(id);
        var layer = _session.Document.Find(id);
        _status.Text = $"已定位到匹配图层：{UiText.LayerName(layer.Name)}（{cursor + 1}/{_layerMatches.Length}）。";
    }

    private void SelectAllLayerMatches()
    {
        if (_layerMatches.Length == 0)
        {
            _status.Text = "没有匹配项可选中。";
            return;
        }
        var ids = _layerMatches.ToArray();
        var hidden = ids.Count(id => !_session.Document.Find(id).Visible);
        var locked = ids.Count(id => _session.Document.Find(id).Locked);
        _session.SetSelection(ids);
        _layerMatchCursor = 0;
        RefreshLayerOnly();
        ScrollToLayer(ids[0]);
        _status.Text = $"已按列表顺序选中 {ids.Length} 个匹配图层" +
            $"（隐藏 {hidden} 个，锁定 {locked} 个）。行内按钮可分别管理显隐和锁定。";
    }

    private void ScrollToLayer(Guid id)
    {
        var index = _layerItems.ToList().FindIndex(item => item.Id == id);
        if (index >= 0) _layers.ScrollIntoView(index);
    }

    private void ResetLayerListView()
    {
        if (_layers.IsKeyboardFocusWithin) _layers.Focus();
        _layers.Selection.AnchorIndex = -1;
        if (_layers.Scroll is { } scroll) scroll.Offset = default;
    }

    private void BuildPropertyPanel()
    {
        _propertyPanel.Children.Add(new TextBlock { Text = "位置与尺寸", FontWeight = FontWeight.SemiBold });
        AddPropertyField(_propertyPanel, "Name", _propertyName);
        AddPropertyPair(_propertyPanel, "X", _propertyX, "Y", _propertyY);
        AddPropertyPair(_propertyPanel, "Width", _propertyWidth, "Height", _propertyHeight);
        AddPropertyPair(_propertyPanel, "不透明度（%）", _propertyOpacity,
            "旋转角度（°）", _propertyRotation);
        ToolTip.SetTip(_propertyOpacity, "不透明度：0–100%。");
        ToolTip.SetTip(_propertyRotation, "旋转角度：-180°–180°。");
        _propertyPanel.Children.Add(_propertyKeepAspect);
        var imageOptions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        _propertySlanted.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _openImageCropButton.Margin = new Thickness(6, 0, 0, 0);
        _openImageCropButton.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Grid.SetColumn(_openImageCropButton, 1);
        imageOptions.Children.Add(_propertySlanted);
        imageOptions.Children.Add(_openImageCropButton);
        _propertyPanel.Children.Add(imageOptions);
        SetAccessibility(_openImageCropButton, "编辑图片裁切",
            "调整当前图片的适配方式和源图取样范围。");
        _openImageCropButton.Click += (_, _) =>
        {
            try { EnsureCanvasIdle(); _ = OpenImageCrop(); }
            catch (Exception error) { ShowStatusError(error.Message); }
        };
        AddPropertyField(_colorPropertyFields, "Color (#AARRGGBB)", _propertyColor);
        _propertyPanel.Children.Add(_colorPropertyFields);
        AddPropertyField(_textPropertyFields, "文字内容（双击画布文字也可编辑）", _text);
        AddButton(_textPropertyFields, "Apply Text", ApplyText);
        AddPropertyField(_textPropertyFields, "Font family", _propertyFont);
        AddAsyncButton(_textPropertyFields, "Import local font (TTF / OTF)", ImportLocalFont);
        _localFontChoices.ItemsSource = _fonts.Fonts.ToArray();
        AddPropertyField(_textPropertyFields, "Available local fonts", _localFontChoices);
        AddButton(_textPropertyFields, "Use selected local font", ApplyLocalFont, "ApplyLocalFont");
        AddPropertyPair(_textPropertyFields, "Font size", _propertyFontSize, "Line spacing", _propertyLineSpacing);
        AddPropertyPair(_textPropertyFields, "Horizontal alignment", _propertyAlignment,
            "Vertical alignment", _propertyVerticalAlignment);
        _textPropertyFields.Children.Add(_propertyBold);
        _textPropertyFields.Children.Add(_propertyItalic);
        _propertyPanel.Children.Add(_textPropertyFields);
        AddPropertyPair(_badgePropertyFields, "Difficulty", _propertyDifficulty, "Positive level", _propertyLevel);
        _propertyPanel.Children.Add(_badgePropertyFields);
        _propertyPanel.Children.Add(new TextBlock
        {
            Text = "单行属性按 Enter 应用；多行文字按 Enter 换行。",
            Name = "PropertyApplyHint", FontSize = 11, TextWrapping = TextWrapping.Wrap
        });
        AddButton(_propertyPanel, "Apply properties", ApplyProperties, "ApplyLayerProperties");
    }

    private static void AddPropertyField(Panel panel, string label, Control field)
    {
        panel.Children.Add(new TextBlock { Text = UiText.Translate(label), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(field);
    }

    private static void AddPropertyPair(Panel panel, string firstLabel, Control first,
        string secondLabel, Control second)
    {
        // Shared label/input rows keep both inputs aligned when one caption wraps.
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 8, RowSpacing = 2
        };
        var left = new TextBlock { Text = UiText.Translate(firstLabel), TextWrapping = TextWrapping.Wrap };
        var right = new TextBlock { Text = UiText.Translate(secondLabel), TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(right, 1);
        Grid.SetRow(first, 1);
        Grid.SetRow(second, 1);
        Grid.SetColumn(second, 1);
        row.Children.Add(left);
        row.Children.Add(right);
        row.Children.Add(first);
        row.Children.Add(second);
        panel.Children.Add(row);
    }

    private void BuildBatchStylePanel()
    {
        _batchBold.ItemTemplate = new FuncDataTemplate<bool>((value, _) =>
            new TextBlock { Text = value ? "是（粗体）" : "否（常规）" });
        _batchItalic.ItemTemplate = new FuncDataTemplate<bool>((value, _) =>
            new TextBlock { Text = value ? "是（斜体）" : "否（正体）" });
        _batchAlignment.ItemTemplate = new FuncDataTemplate<Editor.Core.HorizontalAlignment>((value, _) =>
            new TextBlock { Text = value switch
            {
                Editor.Core.HorizontalAlignment.Left => "左对齐",
                Editor.Core.HorizontalAlignment.Center => "居中",
                _ => "右对齐"
            } });
        _batchVertical.ItemTemplate = new FuncDataTemplate<Editor.Core.VerticalAlignment>((value, _) =>
            new TextBlock { Text = value switch
            {
                Editor.Core.VerticalAlignment.Top => "顶部",
                Editor.Core.VerticalAlignment.Center => "居中",
                _ => "底部"
            } });
        AddBatchField(_batchTextFields, _batchApplyFont, _batchFont);
        AddBatchField(_batchTextFields, _batchApplyFontSize, _batchFontSize);
        AddBatchField(_batchTextFields, _batchApplyColor, _batchColor);
        AddBatchField(_batchTextFields, _batchApplyBold, _batchBold);
        AddBatchField(_batchTextFields, _batchApplyItalic, _batchItalic);
        AddBatchField(_batchTextFields, _batchApplyLineSpacing, _batchLineSpacing);
        AddBatchField(_batchTextFields, _batchApplyAlignment, _batchAlignment);
        AddBatchField(_batchTextFields, _batchApplyVertical, _batchVertical);
        AddBatchField(_batchBadgeFields, _batchApplyDifficulty, _batchDifficulty);
        AddBatchField(_batchBadgeFields, _batchApplyLevel, _batchLevel);
        _batchApplyButton.Click += (_, _) =>
        {
            try { EnsureCanvasIdle(); ApplyBatchStyle(); }
            catch (Exception error) { ShowStatusError(error.Message); }
        };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "只修改勾选的字段；未勾选的混合字段保留各层原值。仅支持同一类型的文字图层或难度徽标。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        });
        panel.Children.Add(_batchTargetInfo);
        panel.Children.Add(_batchMessage);
        panel.Children.Add(_batchTextFields);
        panel.Children.Add(_batchBadgeFields);
        panel.Children.Add(_batchApplyButton);
        _batchStyleSection.Content = panel;
    }

    private static void AddBatchField(Panel panel, CheckBox apply, Control field)
    {
        apply.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(apply);
        panel.Children.Add(field);
    }

    private void RefreshBatchStyleFields(bool force = false)
    {
        var ids = _session.SelectedLayerIds.ToArray();
        if (ids.Length < 2)
        {
            _batchStyleSection.IsVisible = false;
            _batchStyleContext = null;
            return;
        }
        _batchStyleSection.IsVisible = true;
        var layers = ids.Select(_session.Document.Find).ToArray();
        var kind = layers[0].Kind;
        var supported = kind is LayerKind.Text or LayerKind.Badge &&
            layers.All(layer => layer.Kind == kind);
        if (!supported)
        {
            _batchTargetInfo.Text = $"已选择 {ids.Length} 个图层。批量样式仅支持同一类型的文字图层或难度徽标。";
            _batchMessage.Text = "混选或图片/形状图层不会提交批量样式。";
            _batchTextFields.IsVisible = false;
            _batchBadgeFields.IsVisible = false;
            _batchApplyButton.IsEnabled = false;
            _batchStyleContext = null;
            return;
        }

        var context = new BatchStyleContext(_session.Document.Copy(), ids,
            _session.UndoCount, _session.RedoCount);
        if (!force && _batchStyleContext is not null &&
            _batchStyleContext.Ids.SequenceEqual(context.Ids) &&
            _batchStyleContext.Undo == context.Undo && _batchStyleContext.Redo == context.Redo &&
            _batchStyleContext.Snapshot.StateEquals(context.Snapshot))
            return;
        _batchStyleContext = context;
        var locked = layers.Count(layer => layer.Locked);
        var hidden = layers.Count(layer => !layer.Visible);
        _batchTargetInfo.Text = $"批量作用于 {ids.Length} 个{UiText.LayerKindName(kind)}图层" +
            (hidden > 0 ? $"（含 {hidden} 个隐藏层）" : "") + "。";
        _batchMessage.Text = locked > 0
            ? $"其中 {locked} 个图层已锁定；请先解锁，批量提交会整次拒绝。"
            : "未勾选的字段不会修改。";
        _batchTextFields.IsVisible = kind == LayerKind.Text;
        _batchBadgeFields.IsVisible = kind == LayerKind.Badge;
        _batchApplyButton.IsEnabled = true;
        foreach (var check in new[]
        {
            _batchApplyFont, _batchApplyFontSize, _batchApplyColor, _batchApplyBold,
            _batchApplyItalic, _batchApplyLineSpacing, _batchApplyAlignment, _batchApplyVertical,
            _batchApplyDifficulty, _batchApplyLevel
        }) check.IsChecked = false;

        if (kind == LayerKind.Text)
        {
            SetBatchText(_batchFont, AllSame(layers, layer => layer.FontFamily), layers[0].FontFamily);
            SetBatchText(_batchFontSize, AllSame(layers, layer => Format(layer.FontSize)), Format(layers[0].FontSize));
            SetBatchText(_batchColor, AllSame(layers, layer => $"{layer.ColorArgb:X8}"), $"{layers[0].ColorArgb:X8}");
            SetBatchChoice(_batchBold, AllSame(layers, layer => layer.Bold), layers[0].Bold);
            SetBatchChoice(_batchItalic, AllSame(layers, layer => layer.Italic), layers[0].Italic);
            SetBatchText(_batchLineSpacing, AllSame(layers, layer => Format(layer.LineSpacing)),
                Format(layers[0].LineSpacing));
            SetBatchChoice(_batchAlignment, AllSame(layers, layer => layer.Alignment), layers[0].Alignment);
            SetBatchChoice(_batchVertical, AllSame(layers, layer => layer.VerticalAlignment),
                layers[0].VerticalAlignment);
        }
        else
        {
            SetBatchChoice(_batchDifficulty, AllSame(layers, layer => layer.Difficulty), layers[0].Difficulty);
            SetBatchText(_batchLevel, AllSame(layers, layer => layer.Level.ToString(CultureInfo.InvariantCulture)),
                layers[0].Level.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static bool AllSame<T>(IReadOnlyList<Layer> layers, Func<Layer, T> value) =>
        layers.Count > 0 && layers.Skip(1).All(layer => EqualityComparer<T>.Default.Equals(value(layer), value(layers[0])));

    private static void SetBatchText(TextBox field, bool same, string value)
    {
        field.Text = same ? value : "";
        field.PlaceholderText = same ? "" : "多个值";
    }

    private static void SetBatchChoice<T>(ComboBox box, bool same, T value)
    {
        box.PlaceholderText = same ? "" : "多个值";
        box.SelectedItem = same ? value : null;
    }

    private void ApplyBatchStyle()
    {
        var ids = _session.SelectedLayerIds.ToArray();
        if (ids.Length < 2) throw new InvalidOperationException("请至少选择两个图层。");
        var layers = ids.Select(_session.Document.Find).ToArray();
        var kind = layers[0].Kind;
        if (kind is not (LayerKind.Text or LayerKind.Badge) || layers.Any(layer => layer.Kind != kind))
            throw new InvalidOperationException("批量样式仅支持同一类型的文字图层或难度徽标。");
        if (_batchStyleContext is null || !_batchStyleContext.Ids.SequenceEqual(ids) ||
            _batchStyleContext.Undo != _session.UndoCount || _batchStyleContext.Redo != _session.RedoCount ||
            !_batchStyleContext.Snapshot.StateEquals(_session.Document))
        {
            RefreshBatchStyleFields(force: true);
            throw new InvalidOperationException("批量编辑目标已变化，请重新勾选要应用的字段。");
        }
        var patch = BuildBatchPatch(kind);
        if (patch.IsEmpty) throw new InvalidOperationException("请至少勾选一个要批量应用的字段。");
        _session.ApplyLayerStyleBatch(ids, patch);
        RefreshLayerOnly();
        RefreshBatchStyleFields(force: true);
        _status.Text = $"已批量更新 {ids.Length} 个{UiText.LayerKindName(kind)}图层。Ctrl+Z 可撤销整次操作。";
    }

    private LayerStylePatch BuildBatchPatch(LayerKind kind)
    {
        if (kind == LayerKind.Text)
        {
            return new LayerStylePatch(
                FontFamily: _batchApplyFont.IsChecked == true
                    ? RequiredBatchText(_batchFont, "字体") : null,
                FontSize: _batchApplyFontSize.IsChecked == true
                    ? ReadNumber(_batchFontSize, "Font size") : null,
                ColorArgb: _batchApplyColor.IsChecked == true
                    ? ReadColor(_batchColor) : null,
                Bold: _batchApplyBold.IsChecked == true
                    ? RequiredBatchChoice<bool>(_batchBold, "粗体") : null,
                Italic: _batchApplyItalic.IsChecked == true
                    ? RequiredBatchChoice<bool>(_batchItalic, "斜体") : null,
                Alignment: _batchApplyAlignment.IsChecked == true
                    ? RequiredBatchChoice<Editor.Core.HorizontalAlignment>(_batchAlignment, "水平对齐") : null,
                VerticalAlignment: _batchApplyVertical.IsChecked == true
                    ? RequiredBatchChoice<Editor.Core.VerticalAlignment>(_batchVertical, "垂直对齐") : null,
                LineSpacing: _batchApplyLineSpacing.IsChecked == true
                    ? ReadNumber(_batchLineSpacing, "Line spacing") : null);
        }
        var level = _batchApplyLevel.IsChecked == true
            ? ReadBatchLevel() : (int?)null;
        return new LayerStylePatch(
            Difficulty: _batchApplyDifficulty.IsChecked == true
                ? RequiredBatchChoice<string>(_batchDifficulty, "难度类型") : null,
            Level: level);
    }

    private static string RequiredBatchText(TextBox field, string label)
    {
        var value = (field.Text ?? "").Trim();
        if (value.Length == 0) throw new InvalidDataException($"{label}不能为空。");
        return value;
    }

    private static T RequiredBatchChoice<T>(ComboBox box, string label)
    {
        if (box.SelectedItem is not T value)
            throw new InvalidDataException($"请为“{label}”选择一个值。");
        return value;
    }

    private int ReadBatchLevel()
    {
        if (!int.TryParse(_batchLevel.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var level) ||
            level < 1)
            throw new InvalidDataException("Difficulty level must be a positive integer.");
        return level;
    }

    private async Task ImportLocalFont()
    {
        var id = SelectedLayer();
        if (_session.Document.Find(id).Kind != LayerKind.Text)
            throw new InvalidOperationException("Select a text layer first.");
        var pending = new PendingDocumentOperation(_session, () => _native.CurrentPath, includeSelection: true);
        var path = await ChooseOpenPath(EditorFileAction.ImportFont, new FilePickerOpenOptions
        {
            Title = "导入本地字体", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("字体文件（TrueType / OpenType）") { Patterns = ["*.ttf", "*.otf"] }]
        });
        if (path is null) return;
        pending.EnsureUnchanged("Project or text selection changed while choosing a font. Please import again.");
        var protectSource = _importPaths.PrepareAdd(path);
        var font = _fonts.Import(path);
        _localFontChoices.ItemsSource = _fonts.Fonts.ToArray();
        _localFontChoices.SelectedItem = font;
        _session.UpdateLayer(id, layer => layer.FontFamily = font.Reference, "更换字体");
        protectSource();
        _status.Text = $"已使用 {font.Family}。字体保存在本地字体库，工程保存字体引用。";
        Refresh();
    }

    private void ApplyLocalFont()
    {
        var id = SelectedLayer();
        if (_session.Document.Find(id).Kind != LayerKind.Text ||
            _localFontChoices.SelectedItem is not LocalFontInfo font)
            throw new InvalidOperationException("Select a text layer and a local font.");
        _session.UpdateLayer(id, layer => layer.FontFamily = font.Reference, "更换字体");
        Refresh();
    }

    private void RefreshPropertyFields(bool refreshText = true)
    {
        var layer = _session.SelectedLayerId is Guid id ? _session.Document.Find(id) : null;
        _propertyPanel.IsVisible = layer is not null;
        if (layer is null) return;
        _propertyName.Text = UiText.LayerName(layer.Name);
        _propertyOpacity.Text = Format(layer.Opacity * 100);
        _propertyRotation.Text = Format(layer.RotationDegrees);
        _propertyX.Text = Format(layer.Bounds.X);
        _propertyY.Text = Format(layer.Bounds.Y);
        _refreshingPropertyDimensions = true;
        try
        {
            _propertyWidth.Text = Format(layer.Bounds.Width);
            _propertyHeight.Text = Format(layer.Bounds.Height);
        }
        finally
        {
            _refreshingPropertyDimensions = false;
            _dimensionDriver = DimensionDriver.None;
        }
        _propertyHeight.IsEnabled = true;
        _propertyKeepAspect.IsChecked = layer.Kind == LayerKind.Badge || layer.KeepAspect;
        _propertyKeepAspect.IsEnabled = layer.Kind != LayerKind.Badge;
        _propertySlanted.IsVisible = layer.Kind is LayerKind.Image or LayerKind.Shape;
        _propertySlanted.IsChecked = layer.Slanted;
        _openImageCropButton.IsVisible = layer.Kind == LayerKind.Image;
        _colorPropertyFields.IsVisible = layer.Kind is LayerKind.Text or LayerKind.Shape;
        _propertyColor.Text = $"{layer.ColorArgb:X8}";
        _textPropertyFields.IsVisible = layer.Kind == LayerKind.Text;
        _propertyFont.Text = DisplayFontName(layer.FontFamily);
        _localFontChoices.SelectedItem = _fonts.Fonts.FirstOrDefault(font => font.Reference == layer.FontFamily);
        _propertyFontSize.Text = Format(layer.FontSize);
        _propertyLineSpacing.Text = Format(layer.LineSpacing);
        _propertyAlignment.SelectedItem = layer.Alignment;
        _propertyVerticalAlignment.SelectedItem = layer.VerticalAlignment;
        _propertyBold.IsChecked = layer.Bold;
        _propertyItalic.IsChecked = layer.Italic;
        _badgePropertyFields.IsVisible = layer.Kind == LayerKind.Badge;
        _propertyDifficulty.SelectedItem = layer.Difficulty;
        _propertyLevel.Text = layer.Level.ToString(CultureInfo.InvariantCulture);
        if (refreshText && layer.Kind == LayerKind.Text) _text.Text = layer.Text;
        RefreshBatchStyleFields();
    }

    private static string DisplayFontName(string family) => LocalFontRegistry.DisplayName(family).Replace("(local)", "（本地）", StringComparison.Ordinal);

    private static string Format(double value) => value.ToString(
        value != 0 && Math.Abs(value) < 0.001 ? "0.###E+0" : "0.###", CultureInfo.InvariantCulture);

    // Display rounding must never become a model edit when a different field is applied.
    private static double ReadPropertyNumber(TextBox field, double current, string name) =>
        field.Text == Format(current) ? current : ReadNumber(field, name);

    private void RefreshCanvasFields()
    {
        _canvasWidth.Text = _session.Document.CanvasWidth.ToString(CultureInfo.InvariantCulture);
        _canvasHeight.Text = _session.Document.CanvasHeight.ToString(CultureInfo.InvariantCulture);
        _backgroundColor.Text = $"{_session.Document.BackgroundArgb:X8}";
        RefreshBackgroundFields();
    }

    private static string FormatDarkness(double darkness) => (darkness * 100).ToString("G17", CultureInfo.InvariantCulture);

    private void RefreshBackgroundState(bool refreshInputs = false)
    {
        RefreshTitle();
        RefreshBackgroundFields(refreshInputs);
        _canvas.Refresh();
    }

    private void RefreshBackgroundFields(bool refreshInputs = true)
    {
        var settings = _session.Document.BackgroundBlend;
        _updatingBackgroundFields = true;
        try { _automaticBackground.IsChecked = settings.Enabled; }
        finally { _updatingBackgroundFields = false; }
        if (refreshInputs)
        {
            _backgroundBlur.Text = settings.BlurRadius.ToString(CultureInfo.InvariantCulture);
            _backgroundDarkness.Text = FormatDarkness(settings.Darkness);
            _backgroundRange.Text = Format(settings.Range);
            _backgroundTransition.Text = Format(settings.Transition);
        }
        var contributors = BackgroundComposer.ContributingLayers(_session.Document).Length;
        var mode = settings.ContributionMode == BackgroundContributionMode.Selected
            ? $"选择 {contributors} 张" : $"自动 {contributors} 张";
        _backgroundSource.Text = _session.Document.FixedBackgroundAssetId is not null
            ? "背景：固定图片（优先使用）"
            : settings.Enabled ? $"背景：{mode}曲绘（范围 {Format(settings.Range)}，过渡 {Format(settings.Transition)}）"
            : "背景：画布底色";
    }

    private void ApplyBlendSettings()
    {
        if (!int.TryParse(_backgroundBlur.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var blur))
            throw new InvalidDataException("Blend blur must be an integer from 0 to 20.");
        var current = _session.Document.BackgroundBlend.Darkness;
        var darkness = _backgroundDarkness.Text == FormatDarkness(current)
            ? current : ReadNumber(_backgroundDarkness, "Blend darkness") / 100;
        if (!double.TryParse(_backgroundRange.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var range) || !double.IsFinite(range) ||
            range is < 0.25 or > 2)
            throw new InvalidDataException("背景扩散范围必须在 0.25 到 2.0 之间。");
        if (!double.TryParse(_backgroundTransition.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var transition) || !double.IsFinite(transition) ||
            transition is < 0 or > 1)
            throw new InvalidDataException("背景过渡柔和度必须在 0 到 1 之间。");
        _session.UpdateDocument(document => document.BackgroundBlend =
            document.BackgroundBlend with
            {
                BlurRadius = blur, Darkness = darkness, Range = range, Transition = transition
            }, "应用背景混合设置");
        RefreshBackgroundState(refreshInputs: true);
    }

    private async Task OpenBackgroundContribution()
    {
        if (_editingBackgroundContribution) return;
        _editingBackgroundContribution = true;
        try
        {
            EnsureCanvasIdle();
            var pending = new PendingDocumentOperation(_session, () => _native.CurrentPath);
            string? Apply(BackgroundBlendSettings settings)
            {
                try
                {
                    pending.EnsureUnchanged(
                        "背景贡献设置打开期间工程已发生变化，请重新打开。");
                    _session.UpdateDocument(document => document.BackgroundBlend = settings,
                        "更新背景贡献");
                    return null;
                }
                catch (Exception error) { return UiText.Translate(error.Message); }
            }

            var dialog = new BackgroundContributionDialog(pending.Snapshot, Apply);
            var result = await dialog.ShowDialog<bool?>(this);
            if (result == true)
            {
                RefreshBackgroundState(refreshInputs: true);
                _status.Text = "背景贡献与过渡设置已应用；Ctrl+Z 可撤销整次设置。";
            }
            else _status.Text = "已取消背景贡献设置。";
        }
        finally { _editingBackgroundContribution = false; }
    }

    private async Task OpenResourcePanel()
    {
        if (_viewingResources) return;
        _viewingResources = true;
        try
        {
            EnsureCanvasIdle();
            var dialog = new ResourcePanelDialog(_session, ChooseResourceImage,
                LoadImageForCanvas, ids =>
                {
                    _session.SetSelection(ids);
                    Refresh();
                });
            await dialog.ShowDialog<bool?>(this);
            if (dialog.DocumentChanged)
            {
                RefreshBackgroundFields(refreshInputs: false);
                RefreshLayerOnly();
            }
            _status.Text = dialog.ChangeSummary ??
                (dialog.DocumentChanged ? "资源已更新；Ctrl+Z 可撤销。" : "已关闭资源与质量检查。");
        }
        finally { _viewingResources = false; }
    }

    private Task<string?> ChooseResourceImage(Window owner) =>
        ChooseOpenPath(owner, EditorFileAction.ImportImage, new FilePickerOpenOptions
        {
            Title = "选择替换资源的图片", AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tif", "*.tiff"]
                }
            ]
        });

    private static uint ReadColor(TextBox field)
    {
        if ((field.Text?.Length ?? 0) != 8 ||
            !uint.TryParse(field.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color))
            throw new InvalidDataException("Color must have eight hex digits: AARRGGBB.");
        return color;
    }

    private void ApplyCanvasSettings()
    {
        var (width, height) = ReadCanvasSize();
        var color = ReadColor(_backgroundColor);
        _session.UpdateDocument(document =>
        {
            document.CanvasWidth = width;
            document.CanvasHeight = height;
            document.BackgroundArgb = color;
        }, "应用画布设置");
        Refresh();
    }

    private (int Width, int Height) ReadCanvasSize()
    {
        if (!int.TryParse(_canvasWidth.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(_canvasHeight.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var height))
            throw new InvalidDataException("Canvas width and height must be positive integers.");
        return (width, height);
    }

    private void ResizeCanvasToFit()
    {
        var (width, height) = ReadCanvasSize();
        _session.ResizeCanvasToFit(width, height);
        _status.Text = "画布尺寸已调整，内容已围绕画布中心适配。";
        Refresh();
    }

    private void ApplyLayoutTemplate()
    {
        if (_layoutTemplate.SelectedItem is not LayoutTemplate template)
            throw new InvalidOperationException("Choose a layout template first.");
        _session.ApplyLayoutTemplate(template, SelectedLayers());
        _status.Text = $"已应用“{UiText.Translate(template.Name)}”布局。";
        Refresh();
    }

    private void SelectCustomTemplate(LayoutTemplate template)
    {
        // Keep one working custom template beside the immutable built-ins; save files to retain others.
        _layoutTemplate.ItemsSource = LayoutTemplates.All.Concat(new[] { template }).ToArray();
        _layoutTemplate.SelectedItem = template;
        _layoutName.Text = template.Name;
    }

    private void CaptureLayoutTemplate()
    {
        var template = _session.CaptureLayoutTemplate(_layoutName.Text ?? "", SelectedLayers());
        SelectCustomTemplate(template);
        _status.Text = $"已记录 {template.Slots.Count} 个布局槽位。保存模板文件可在重启后继续使用。";
    }

    private async Task OpenLayoutTemplate()
    {
        var pending = new PendingDocumentOperation(_session, () => _native.CurrentPath);
        var path = await ChooseOpenPath(EditorFileAction.OpenLayout, new FilePickerOpenOptions
        {
            Title = "打开布局模板", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("布局模板") { Patterns = new[] { "*.ppglayout" } } }
        });
        if (path is null) return;
        pending.EnsureUnchanged("Project changed while opening a layout template. Please open it again.");
        var template = LayoutTemplateStore.Read(path);
        SelectCustomTemplate(template);
        _status.Text = $"已加载“{UiText.Translate(template.Name)}”，共 {template.Slots.Count} 个槽位。选中对应图层或组后应用布局。";
    }

    private async Task SaveLayoutTemplate()
    {
        var pending = new PendingDocumentOperation(_session, () => _native.CurrentPath);
        if (_layoutTemplate.SelectedItem is not LayoutTemplate selected)
            throw new InvalidOperationException("Choose or capture a layout template first.");
        var template = selected with { Name = (_layoutName.Text ?? "").Trim() };
        template.Validate();
        var path = await ChooseSavePath(EditorFileAction.SaveLayout, new FilePickerSaveOptions
        {
            Title = "保存布局模板", SuggestedFileName = "layout.ppglayout", DefaultExtension = "ppglayout",
            FileTypeChoices = new[] { new FilePickerFileType("布局模板") { Patterns = new[] { "*.ppglayout" } } }
        });
        if (path is null) return;
        pending.EnsureUnchanged("Project changed while saving a layout template. Please save again.");
        if (!Equals(_layoutTemplate.SelectedItem, selected) || (_layoutName.Text ?? "").Trim() != template.Name)
            throw new InvalidOperationException("Layout template changed while choosing its save path. Please save again.");
        if (!path.EndsWith(LayoutTemplateStore.Extension, StringComparison.OrdinalIgnoreCase))
            path += LayoutTemplateStore.Extension;
        ProtectImportedImage(path);
        if (_native.CurrentPath is not null && FilePathIdentity.AreSame(path, _native.CurrentPath))
            throw new InvalidOperationException("A layout template cannot replace the current project.");
        LayoutTemplateStore.Save(path, template);
        _status.Text = $"布局模板已保存：{path}";
    }

    private void ReloadTemplateLibrary(string? selectId = null)
    {
        var snapshot = _templateLibrary.Load();
        _allTemplateEntries = snapshot.Entries;
        ApplyTemplateLibraryFilter(selectId);
        var text = $"本地模板库：{_allTemplateEntries.Count} 个模板。";
        if (snapshot.Diagnostics.Count > 0)
        {
            var first = snapshot.Diagnostics[0];
            text += $" {snapshot.Diagnostics.Count} 个文件未加载（{first.FileName}：{UiText.Translate(first.Message)}）";
        }
        _templateLibraryStatus.Text = text;
    }

    /// <summary>
    /// Build the secondary list only on first expansion. Keeping it out of the initial logical
    /// tree preserves the established layer-list focus/selection assumptions of the editor.
    /// </summary>
    private void BuildTemplateLibraryPanel()
    {
        if (_templateLibraryPanelBuilt) return;
        _templateLibraryPanelBuilt = true;
        _templateLibraryHost.Children.Add(new Separator());
        _templateLibraryHost.Children.Add(new TextBlock
        {
            Text = "本地模板库（重启后保留，只保存槽位）", FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        _templateLibraryList.ItemsSource = _templateLibraryItems;
        _templateLibraryList.SelectionChanged += (_, _) => UpdateTemplateLibraryPreview();
        _templateLibrarySearch.TextChanged += (_, _) => ApplyTemplateLibraryFilter();
        _templateLibraryHost.Children.Add(_templateLibrarySearch);
        _templateLibraryHost.Children.Add(_templateLibraryList);
        _templateLibraryHost.Children.Add(_templatePreview);
        _templateLibraryHost.Children.Add(_templateLibraryStatus);
        AddButton(_templateLibraryHost, "将当前模板加入库", AddCurrentTemplateToLibrary,
            "AddLayoutToLibrary");
        AddAsyncButton(_templateLibraryHost, "从文件导入到库", ImportLayoutToLibrary);
        AddButton(_templateLibraryHost, "应用库模板", ApplySelectedLibraryTemplate,
            "ApplyLibraryTemplate");
        AddAsyncButton(_templateLibraryHost, "重命名库模板", RenameLibraryTemplate);
        AddAsyncButton(_templateLibraryHost, "导出库模板", ExportLibraryTemplate);
        AddAsyncButton(_templateLibraryHost, "移除库模板", RemoveLibraryTemplate);
        ApplyTemplateLibraryFilter();
    }

    private void ApplyTemplateLibraryFilter(string? selectId = null)
    {
        var selectedId = selectId ??
            (_templateLibraryList.SelectedItem as TemplateLibraryEntry)?.Id;
        var query = (_templateLibrarySearch.Text ?? "").Trim();
        var filtered = _allTemplateEntries.Where(entry =>
            query.Length == 0 || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _templateLibraryItems.Clear();
        foreach (var entry in filtered) _templateLibraryItems.Add(entry);
        var selected = selectedId is null ? null :
            filtered.FirstOrDefault(entry => entry.Id == selectedId);
        _templateLibraryList.SelectedItem = selected ?? filtered.FirstOrDefault();
        UpdateTemplateLibraryPreview();
    }

    private void UpdateTemplateLibraryPreview()
    {
        var entry = _templateLibraryList.SelectedItem as TemplateLibraryEntry;
        _templatePreview.Template = entry is null
            ? null
            : new LayoutTemplate(entry.Id, entry.Name, entry.Slots);
    }

    private TemplateLibraryEntry SelectedLibraryEntry() =>
        _templateLibraryList.SelectedItem as TemplateLibraryEntry
        ?? throw new InvalidOperationException("请先在本地模板库中选择一个模板。");

    private void AddCurrentTemplateToLibrary()
    {
        if (_layoutTemplate.SelectedItem is not LayoutTemplate template)
            throw new InvalidOperationException("Choose or capture a layout template first.");
        var result = _templateLibrary.Add(template);
        ReloadTemplateLibrary(result.Entry.Id);
        _status.Text = result.Added
            ? $"已将“{result.Entry.Name}”加入本地模板库，重启后仍可使用。"
            : "该模板已在本地模板库中，未重复写入。";
    }

    private async Task ImportLayoutToLibrary()
    {
        var path = await ChooseOpenPath(EditorFileAction.OpenLayout, new FilePickerOpenOptions
        {
            Title = "从布局模板文件导入到本地库", AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("布局模板")
                {
                    Patterns = ["*" + TemplateLibrary.FileExtension]
                }
            ]
        });
        if (path is null) return;
        var result = _templateLibrary.Import(path);
        ReloadTemplateLibrary(result.Entry.Id);
        _status.Text = result.Added
            ? $"已将“{result.Entry.Name}”的受控副本加入本地模板库；原文件未修改。"
            : "相同名称和槽位的模板已在库中，未重复写入。";
    }

    private void ApplySelectedLibraryTemplate()
    {
        var entry = SelectedLibraryEntry();
        var template = _templateLibrary.Read(entry.Id);
        _session.ApplyLayoutTemplate(template, SelectedLayers());
        Refresh();
        _status.Text = $"已应用本地模板“{entry.Name}”（{entry.SlotCount} 个槽位）。Ctrl+Z 可撤销整次应用。";
    }

    private async Task RenameLibraryTemplate()
    {
        var entry = SelectedLibraryEntry();
        var name = await new NamePromptDialog("重命名库模板",
            "名称只用于库中显示，不会改变受控文件名或工程内容。", entry.Name)
            .ShowDialog<string?>(this);
        if (name is null) return;
        var renamed = _templateLibrary.Rename(entry.Id, name);
        ReloadTemplateLibrary(renamed.Id);
        _status.Text = $"库模板已重命名为“{renamed.Name}”。";
    }

    private async Task ExportLibraryTemplate()
    {
        var entry = SelectedLibraryEntry();
        var path = await ChooseSavePath(EditorFileAction.SaveLayout, new FilePickerSaveOptions
        {
            Title = "导出本地库模板", SuggestedFileName = entry.Name + TemplateLibrary.FileExtension,
            DefaultExtension = "ppglayout",
            FileTypeChoices =
            [
                new FilePickerFileType("布局模板")
                {
                    Patterns = ["*" + TemplateLibrary.FileExtension]
                }
            ]
        });
        if (path is null) return;
        if (!path.EndsWith(TemplateLibrary.FileExtension, StringComparison.OrdinalIgnoreCase))
            path += TemplateLibrary.FileExtension;
        ProtectImportedImage(path);
        if (_native.CurrentPath is not null && FilePathIdentity.AreSame(path, _native.CurrentPath))
            throw new InvalidOperationException("A layout template cannot replace the current project.");
        _templateLibrary.Export(entry.Id, path);
        _status.Text = $"库模板已导出：{path}";
    }

    private async Task RemoveLibraryTemplate()
    {
        var entry = SelectedLibraryEntry();
        var accepted = await new ConfirmDialog("移除库模板",
            $"仅从本地模板库移除“{entry.Name}”的副本；用户原来的 .ppglayout 文件不会被删除。",
            "移除").ShowDialog<bool>(this);
        if (!accepted) return;
        _templateLibrary.Remove(entry.Id);
        ReloadTemplateLibrary();
        _status.Text = $"已从本地模板库移除“{entry.Name}”。";
    }

    private static double ReadNumber(TextBox field, string name)
    {
        if (!double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value))
            throw new InvalidDataException($"{name} must be a finite number (use '.' for decimals).");
        return value;
    }

    private void ApplyProperties()
    {
        var id = SelectedLayer();
        var source = _session.Document.Find(id);
        var nameInput = _propertyName.Text ?? "";
        var name = nameInput == UiText.LayerName(source.Name) ? source.Name : nameInput;
        var x = ReadPropertyNumber(_propertyX, source.Bounds.X, "X");
        var y = ReadPropertyNumber(_propertyY, source.Bounds.Y, "Y");
        var width = ReadPropertyNumber(_propertyWidth, source.Bounds.Width, "Width");
        var height = ReadPropertyNumber(_propertyHeight, source.Bounds.Height, "Height");
        var opacityPercent = ReadPropertyNumber(_propertyOpacity, source.Opacity * 100, "Opacity");
        if (opacityPercent < 0 || opacityPercent > 100)
            throw new InvalidDataException("不透明度必须在 0 到 100 之间。");
        var rotation = ReadPropertyNumber(_propertyRotation, source.RotationDegrees, "旋转角度");
        var keepAspect = source.Kind == LayerKind.Badge || _propertyKeepAspect.IsChecked == true;
        if (keepAspect)
        {
            var ratio = source.Kind == LayerKind.Badge ? EditorDocument.BadgeRatio :
                source.Bounds.Width / source.Bounds.Height;
            var widthChanged = width != source.Bounds.Width;
            var heightChanged = height != source.Bounds.Height;
            if (heightChanged && (!widthChanged || _dimensionDriver == DimensionDriver.Height))
                width = height * ratio;
            else if (widthChanged)
                height = width / ratio;
        }
        var color = source.Kind is LayerKind.Text or LayerKind.Shape
            ? ReadColor(_propertyColor) : source.ColorArgb;
        var fontSize = source.Kind == LayerKind.Text
            ? ReadPropertyNumber(_propertyFontSize, source.FontSize, "Font size") : source.FontSize;
        var lineSpacing = source.Kind == LayerKind.Text
            ? ReadPropertyNumber(_propertyLineSpacing, source.LineSpacing, "Line spacing") : source.LineSpacing;
        var level = source.Level;
        if (source.Kind == LayerKind.Badge &&
            (!int.TryParse(_propertyLevel.Text, NumberStyles.None, CultureInfo.InvariantCulture, out level) || level < 1))
            throw new InvalidDataException("Difficulty level must be a positive integer.");
        _session.UpdateLayer(id, layer =>
        {
            layer.Name = name;
            layer.RotationDegrees = rotation;
            layer.Bounds = new RectD(x, y, width, height);
            layer.Opacity = opacityPercent / 100;
            layer.KeepAspect = keepAspect;
            if (layer.Kind is LayerKind.Image or LayerKind.Shape)
                layer.Slanted = _propertySlanted.IsChecked == true;
            layer.ColorArgb = color;
            if (layer.Kind == LayerKind.Text)
            {
                var fontInput = _propertyFont.Text ?? "";
                layer.FontFamily = fontInput == DisplayFontName(source.FontFamily)
                    ? source.FontFamily : fontInput;
                layer.FontSize = fontSize;
                layer.LineSpacing = lineSpacing;
                layer.Alignment = (Editor.Core.HorizontalAlignment)(_propertyAlignment.SelectedItem ?? Editor.Core.HorizontalAlignment.Left);
                layer.VerticalAlignment = (Editor.Core.VerticalAlignment)
                    (_propertyVerticalAlignment.SelectedItem ?? Editor.Core.VerticalAlignment.Top);
                layer.Bold = _propertyBold.IsChecked == true;
                layer.Italic = _propertyItalic.IsChecked == true;
            }
            if (layer.Kind == LayerKind.Badge)
            {
                layer.Difficulty = (string)(_propertyDifficulty.SelectedItem ?? "IN");
                layer.Level = level;
            }
        }, "编辑图层属性");
        Refresh(refreshProperties: true, refreshText: false);
    }

    private async Task OpenImageCrop()
    {
        var id = SelectedLayer();
        var layer = _session.Document.Find(id);
        if (layer.Kind != LayerKind.Image)
            throw new InvalidOperationException("请先选择图片图层。");
        var settings = await new ImageCropDialog(layer).ShowDialog<ImageCropSettings?>(this);
        if (settings is null) return;
        _session.UpdateLayer(id, value =>
        {
            value.ImageFit = settings.Fit;
            value.ImageCrop = settings.Crop;
        }, "裁剪图片");
        Refresh();
        _status.Text = "图片裁切与适配已更新。Ctrl+Z 可撤销本次修改。";
    }

    private Guid SelectedLayer() => _session.SelectedLayerId
        ?? throw new InvalidOperationException("Select a layer first.");

    private Guid[] SelectedLayers() => _session.SelectedLayerIds.Count > 0
        ? _session.SelectedLayerIds.ToArray()
        : throw new InvalidOperationException("Select a layer first.");

    private void RunLayerAction(Action action)
    {
        var document = _session.Document;
        var before = document.Copy();
        var selection = _session.SelectedLayerIds.ToArray();
        action();
        // Full history and an already-dirty save point do not reveal whether an edit changed anything.
        if (!ReferenceEquals(document, _session.Document) ||
            !selection.SequenceEqual(_session.SelectedLayerIds) || !before.StateEquals(_session.Document))
            Refresh();
    }

    private void ToggleSelected(Func<Layer, bool> current, Action<Layer, bool> set, string label)
    {
        var ids = SelectedLayers();
        var value = !current(_session.Document.Find(ids[0]));
        _session.UpdateDocument(document =>
        {
            foreach (var id in ids) set(document.Find(id), value);
        }, label);
        Refresh();
    }

    private void SetSelectedGroupVisible(bool visible)
    {
        var id = SelectedLayer();
        var group = _session.Document.Find(id).GroupId;
        RunLayerAction(() => _session.UpdateDocument(document =>
        {
            foreach (var layer in document.Layers.Where(layer =>
                group is Guid value ? layer.GroupId == value : layer.Id == id))
                layer.Visible = visible;
        }, visible ? "显示整组" : "隐藏整组"));
    }

    private void ReorderSelected(LayerOrderAction action)
    {
        RunLayerAction(() => _session.ReorderLayers(SelectedLayers(), action));
    }

    private void ProtectImportedImage(string path)
    {
        _importPaths.EnsureNotProtected(path);
    }

    private static void SetAccessibility(Control control, string name, string? tooltip = null)
    {
        AutomationProperties.SetName(control, name);
        if (tooltip is not null) ToolTip.SetTip(control, tooltip);
    }

    private Button AddButton(Panel panel, string label, Action action, string? name = null)
    {
        var button = new Button { Name = name, Content = UiText.Translate(label), Margin = new Thickness(2) };
        button.Click += (_, _) =>
        {
            try { EnsureCanvasIdle(); action(); }
            catch (Exception error) { ShowStatusError(error.Message); }
        };
        panel.Children.Add(button);
        return button;
    }

    private void AddAsyncButton(Panel panel, string label, Func<Task> action, string? name = null)
    {
        var button = new Button { Name = name, Content = UiText.Translate(label),
            Margin = new Thickness(2) };
        button.Click += async (_, _) =>
        {
            try { await RunFileOperation(action); }
            catch (Exception error) { ShowStatusError(error.Message); }
        };
        panel.Children.Add(button);
    }

    private async Task RunFileOperation(Func<Task> action)
    {
        if (_fileOperationPending)
        {
            _status.Text = "请先完成当前文件操作。";
            return;
        }
        EnsureCanvasIdle();
        _fileOperationPending = true;
        try { await action(); }
        finally { _fileOperationPending = false; }
    }

    private void EnsureCanvasIdle()
    {
        if (_canvas.IsInteracting) throw new InvalidOperationException("Finish the current gesture first.");
    }

    private string? CompletePathChoice(string? path)
    {
        if (path is not null) EnsureCanvasIdle();
        return path;
    }

    private async Task<LoadedImage> LoadImageForCanvas(string path)
    {
        var image = await _imageLoader(path);
        EnsureCanvasIdle();
        return image;
    }

    private Task<string?> ChooseOpenPath(EditorFileAction action, FilePickerOpenOptions options) =>
        ChooseOpenPath(this, action, options);

    private async Task<string?> ChooseOpenPath(Window owner,
        EditorFileAction action, FilePickerOpenOptions options)
    {
        if (_filePathPicker is not null) return CompletePathChoice(await _filePathPicker(action));
        var files = await owner.StorageProvider.OpenFilePickerAsync(options);
        return CompletePathChoice(files.Count == 0 ? null : LocalPath(files[0]));
    }

    private async Task<string?> ChooseSavePath(EditorFileAction action, FilePickerSaveOptions options)
    {
        if (_filePathPicker is not null) return CompletePathChoice(await _filePathPicker(action));
        var file = await StorageProvider.SaveFilePickerAsync(options);
        return CompletePathChoice(file is null ? null : LocalPath(file));
    }
}
