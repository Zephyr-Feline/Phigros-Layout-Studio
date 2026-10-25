using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Application;
using Editor.Core;

namespace Editor.Desktop;

/// <summary>
/// Explicit-role song editor. Suggestions are visible defaults only; every row must be confirmed
/// and the service rejects missing/ambiguous mappings rather than guessing by layer name or index.
/// </summary>
public sealed class SongInfoWorkbenchDialog : Window
{
    private readonly Func<IReadOnlyList<SongInfoEdit>, SongTextStyle?, string?> _apply;
    private readonly PendingSongArtwork _pendingArtwork;
    private readonly List<RowEditor> _rows = [];
    private readonly TextBlock _error;
    private readonly CheckBox _applyFontSize = new()
        { Name = "SongInfoApplyFontSize", Content = "统一字号" };
    private readonly TextBox _fontSize = new() { Name = "SongInfoCommonFontSize" };
    private readonly CheckBox _applyColor = new()
        { Name = "SongInfoApplyColor", Content = "统一颜色" };
    private readonly TextBox _color = new() { Name = "SongInfoCommonColor" };
    private readonly CheckBox _applyBold = new()
        { Name = "SongInfoApplyBold", Content = "统一粗体" };
    private readonly ComboBox _bold = new()
        { Name = "SongInfoCommonBold", ItemsSource = new[] { true, false } };

    public SongInfoWorkbenchDialog(EditorDocument document,
        IReadOnlyList<SongInfoSuggestion> suggestions,
        Func<Window, Task<string?>> chooseArtwork,
        Func<string, Task<LoadedImage>> loadImage,
        Func<IReadOnlyList<SongInfoEdit>, SongTextStyle?, string?> apply)
        : this(document, suggestions, chooseArtwork, loadImage, apply, AssetResourceBudget.MaxDocumentBytes) { }

    internal SongInfoWorkbenchDialog(EditorDocument document,
        IReadOnlyList<SongInfoSuggestion> suggestions,
        Func<Window, Task<string?>> chooseArtwork,
        Func<string, Task<LoadedImage>> loadImage,
        Func<IReadOnlyList<SongInfoEdit>, SongTextStyle?, string?> apply, long maximumPendingBytes)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(suggestions);
        ArgumentNullException.ThrowIfNull(chooseArtwork);
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _pendingArtwork = new PendingSongArtwork(maximumPendingBytes);
        Closed += (_, _) => _pendingArtwork.Close();
        Title = "歌曲信息工作台";
        Width = 980;
        Height = 800;
        MinWidth = 700;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _error = new TextBlock
        {
            Name = "SongInfoError", TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 120))
        };
        _bold.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<bool>((value, _) =>
            new TextBlock { Text = value ? "是" : "否" });
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        var roleNote = suggestions.Any(suggestion => suggestion.HasPersistedRoles)
            ? "角色优先使用工程内已保存的标注；仍请确认后提交。"
            : "当前工程尚未保存角色标注；请从图层列表明确确认映射。";
        panel.Children.Add(new TextBlock
        {
            Name = "SongInfoHeader",
            Text = suggestions.Count == 0
                ? "没有可编辑的歌曲组。"
                : $"已选择 {suggestions.Count} 个歌曲组。{roleNote}不会按名称或数组下标猜角色后直接覆写。",
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var suggestion in suggestions)
        {
            var row = new RowEditor(document, suggestion, () => chooseArtwork(this), loadImage, _rows.Count,
                _pendingArtwork);
            _rows.Add(row);
            panel.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(72, 91, 103)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10),
                Child = row.View
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = "公共文字样式（可选；只作用于已映射的曲名/曲师/谱师）",
            FontWeight = FontWeight.SemiBold
        });
        var styleRow = new WrapPanel();
        AddStyleField(styleRow, _applyFontSize, _fontSize);
        AddStyleField(styleRow, _applyColor, _color);
        AddStyleField(styleRow, _applyBold, _bold);
        panel.Children.Add(styleRow);
        var applyButton = new Button
        {
            Name = "ApplySongInfo", Content = "应用全部（一次撤销）", IsDefault = true
        };
        applyButton.Click += (_, _) => ApplyAll();
        var cancel = new Button { Name = "CancelSongInfo", Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, applyButton }
        };
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel };
    }

    private static void AddStyleField(Panel panel, CheckBox check, Control field)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        check.Margin = new Thickness(8, 0, 2, 0);
        stack.Children.Add(check);
        stack.Children.Add(field);
        panel.Children.Add(stack);
    }

    private void ApplyAll()
    {
        _error.Text = "";
        if (_rows.Count == 0)
        {
            _error.Text = "没有可编辑的歌曲组。";
            return;
        }
        var edits = new List<SongInfoEdit>(_rows.Count);
        foreach (var row in _rows)
        {
            var edit = row.Build(out var rowError);
            if (edit is null)
            {
                _error.Text = rowError ?? "歌曲信息行无效。";
                return;
            }
            edits.Add(edit);
        }
        SongTextStyle? style;
        try { style = BuildStyle(); }
        catch (Exception error)
        {
            _error.Text = error.Message;
            return;
        }
        var applyError = _apply(edits, style);
        if (applyError is not null)
        {
            _error.Text = applyError;
            return;
        }
        Close(true);
    }

    private SongTextStyle? BuildStyle()
    {
        if (!_applyFontSize.IsChecked.HasValue || !_applyColor.IsChecked.HasValue ||
            !_applyBold.IsChecked.HasValue)
            throw new InvalidOperationException("公共样式勾选状态无效。");
        double? size = null;
        uint? color = null;
        bool? bold = null;
        if (_applyFontSize.IsChecked == true)
        {
            if (!double.TryParse(_fontSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var parsed) || !double.IsFinite(parsed))
                throw new InvalidDataException("公共字号必须是有效数字。");
            size = parsed;
        }
        if (_applyColor.IsChecked == true)
        {
            if ((_color.Text?.Length ?? 0) != 8 ||
                !uint.TryParse(_color.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out var parsedColor))
                throw new InvalidDataException("公共颜色必须为 8 位 AARRGGBB。");
            color = parsedColor;
        }
        if (_applyBold.IsChecked == true)
        {
            if (_bold.SelectedItem is not bool parsedBold)
                throw new InvalidDataException("请选择公共粗体值。");
            bold = parsedBold;
        }
        if (size is null && color is null && bold is null) return null;
        var style = new SongTextStyle(size, color, bold);
        style.Validate();
        return style;
    }

    private sealed class RowEditor
    {
        private readonly int _index;
        private readonly ComboBox _artwork;
        private readonly ComboBox _title;
        private readonly ComboBox _artist;
        private readonly ComboBox _charter;
        private readonly ComboBox _badge;
        private readonly TextBox _titleText;
        private readonly TextBox _artistText;
        private readonly TextBox _charterText;
        private readonly ComboBox _difficulty;
        private readonly TextBox _level;
        private readonly CheckBox _confirm;
        private readonly TextBlock _preview;
        private readonly TextBlock _rowError;
        private readonly TextBlock _replacementStatus;
        private PendingSongArtwork.Entry? _replacement;
        private string? _replacementPath;
        public Control View { get; }

        public RowEditor(EditorDocument document, SongInfoSuggestion suggestion,
            Func<Task<string?>> chooseArtwork, Func<string, Task<LoadedImage>> loadImage, int index,
            PendingSongArtwork pendingArtwork)
        {
            _index = index;
            _groupId = suggestion.GroupId;
            var layers = document.Layers.Where(layer => layer.GroupId == suggestion.GroupId).ToArray();
            _artwork = Choice($"SongInfoArtwork_{index}", layers, LayerKind.Image, suggestion.ArtworkId);
            _title = Choice($"SongInfoTitle_{index}", layers, LayerKind.Text, suggestion.TitleId);
            _artist = Choice($"SongInfoArtist_{index}", layers, LayerKind.Text, suggestion.ArtistId);
            _charter = Choice($"SongInfoCharter_{index}", layers, LayerKind.Text, suggestion.CharterId);
            _badge = Choice($"SongInfoBadge_{index}", layers, LayerKind.Badge, suggestion.BadgeId);
            _titleText = Text($"SongInfoTitleText_{index}", suggestion.Title);
            _artistText = Text($"SongInfoArtistText_{index}", suggestion.Artist);
            _charterText = Text($"SongInfoCharterText_{index}", suggestion.Charter);
            _difficulty = new ComboBox
            {
                Name = $"SongInfoDifficulty_{index}",
                ItemsSource = new[] { "EZ", "HD", "IN", "AT" },
                SelectedItem = suggestion.Difficulty
            };
            _level = Text($"SongInfoLevel_{index}", suggestion.Level.ToString(CultureInfo.InvariantCulture));
            _confirm = new CheckBox
            {
                Name = $"SongInfoConfirm_{index}",
                Content = "我已确认以上角色映射"
            };
            _preview = new TextBlock { Name = $"SongInfoRowPreview_{index}", TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gainsboro };
            _rowError = new TextBlock
            {
                Name = $"SongInfoRowError_{index}", TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 120))
            };
            _replacementStatus = new TextBlock
            {
                Name = $"SongInfoReplacement_{index}", TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            };
            var replace = new Button
            {
                Name = $"SongInfoReplace_{index}", Content = "替换曲绘…"
            };
            replace.Click += async (_, _) =>
            {
                // One in-flight loader bounds temporary input across every workbench row.
                if (!pendingArtwork.TryBeginLoad()) return;
                try
                {
                    var path = await chooseArtwork();
                    if (path is null || pendingArtwork.IsClosed) return;
                    var loaded = await loadImage(path);
                    if (pendingArtwork.IsClosed) return;
                    if (loaded.Image is null || loaded.Image.Length == 0 ||
                        loaded.Width <= 0 || loaded.Height <= 0)
                        throw new InvalidDataException("图片尺寸或数据无效。");
                    var fullPath = Path.GetFullPath(path);
                    // Admission happens before retaining this row's new payload or making a copy.
                    var replacement = pendingArtwork.Replace(index, loaded.Image);
                    _replacement = replacement;
                    _replacementPath = fullPath;
                    _replacementStatus.Text = "已选择替换曲绘：" + Path.GetFileName(path);
                    _rowError.Text = "";
                }
                catch (Exception error)
                {
                    _rowError.Text = "替换曲绘失败：" + error.Message;
                }
                finally { pendingArtwork.EndLoad(); }
            };

            var mapping = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            AddCell(mapping, 0, 0, "曲绘", _artwork);
            AddCell(mapping, 1, 0, "曲名图层", _title);
            AddCell(mapping, 2, 0, "曲师图层", _artist);
            AddCell(mapping, 0, 1, "谱师图层", _charter);
            AddCell(mapping, 1, 1, "难度徽标", _badge);
            var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            AddCell(fields, 0, 0, "曲名", _titleText);
            AddCell(fields, 1, 0, "曲师", _artistText);
            AddCell(fields, 2, 0, "谱师", _charterText);
            AddCell(fields, 0, 1, "难度", _difficulty);
            AddCell(fields, 1, 1, "等级（正整数）", _level);
            var lower = new StackPanel { Spacing = 6 };
            lower.Children.Add(_confirm);
            lower.Children.Add(replace);
            lower.Children.Add(_replacementStatus);
            lower.Children.Add(_preview);
            lower.Children.Add(_rowError);
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock
            {
                Text = $"第 {index + 1} 行 · {suggestion.GroupLabel}",
                FontWeight = FontWeight.SemiBold
            });
            stack.Children.Add(mapping);
            stack.Children.Add(fields);
            stack.Children.Add(lower);
            View = stack;
            _titleText.TextChanged += (_, _) => UpdatePreview();
            _artistText.TextChanged += (_, _) => UpdatePreview();
            _charterText.TextChanged += (_, _) => UpdatePreview();
            _difficulty.SelectionChanged += (_, _) => UpdatePreview();
            _level.TextChanged += (_, _) => UpdatePreview();
            UpdatePreview();
        }

        public SongInfoEdit? Build(out string? error)
        {
            error = null;
            if (_confirm.IsChecked != true)
            {
                error = $"第 {_index + 1} 行尚未确认角色映射。";
                return null;
            }
            if (_artwork.SelectedItem is not LayerChoice artwork ||
                _title.SelectedItem is not LayerChoice title ||
                _artist.SelectedItem is not LayerChoice artist ||
                _charter.SelectedItem is not LayerChoice charter ||
                _badge.SelectedItem is not LayerChoice badge)
            {
                error = $"第 {_index + 1} 行必须为五个角色选择图层。";
                return null;
            }
            if (new[] { artwork.Id, title.Id, artist.Id, charter.Id, badge.Id }
                .Distinct().Count() != 5)
            {
                error = $"第 {_index + 1} 行的角色不能指向同一个图层。";
                return null;
            }
            if (!int.TryParse(_level.Text, NumberStyles.None, CultureInfo.InvariantCulture,
                out var level) || level < 1)
            {
                error = $"第 {_index + 1} 行等级必须是正整数。";
                return null;
            }
            var mapping = new SongInfoMapping(_groupId, artwork.Id, title.Id, artist.Id, charter.Id, badge.Id);
            return new SongInfoEdit(mapping, new SongInfoValues(
                _titleText.Text ?? "", _artistText.Text ?? "", _charterText.Text ?? "",
                _difficulty.SelectedItem as string ?? "IN", level),
                _replacement?.Bytes, _replacementPath);
        }

        private readonly Guid _groupId;

        private static ComboBox Choice(string name, IReadOnlyList<Layer> layers,
            LayerKind kind, Guid? selected)
        {
            var choices = layers.Where(layer => layer.Kind == kind)
                .Select(layer => new LayerChoice(layer.Id,
                    $"{UiText.LayerKindName(layer.Kind)}：{UiText.LayerName(layer.Name)}")).ToArray();
            var combo = new ComboBox { Name = name, ItemsSource = choices };
            combo.SelectedItem = choices.FirstOrDefault(item => item.Id == selected);
            return combo;
        }

        private static TextBox Text(string name, string text) =>
            new() { Name = name, Text = text, MaxLength = 4000 };

        private static void AddCell(Grid grid, int column, int row, string label, Control field)
        {
            var stack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 4, 4) };
            stack.Children.Add(new TextBlock { Text = label, FontSize = 11 });
            stack.Children.Add(field);
            Grid.SetColumn(stack, column);
            Grid.SetRow(stack, row);
            grid.Children.Add(stack);
        }

        private void UpdatePreview() => _preview.Text =
            $"预览：{_titleText.Text} — {_artistText.Text} / {_charterText.Text} · " +
            $"{_difficulty.SelectedItem ?? "IN"} {_level.Text}";
    }

    private sealed record LayerChoice(Guid Id, string Label)
    {
        public override string ToString() => Label;
    }
}

/// <summary>
/// Workbench-only pending resources. The final document is admitted separately by EditSession;
/// this budget prevents unsubmitted row replacements and repeated copies from accumulating.
/// </summary>
internal sealed class PendingSongArtwork
{
    private readonly long _maximumBytes;
    private readonly Dictionary<int, Entry> _rows = [];
    private bool _loading;
    public bool IsClosed { get; private set; }

    public PendingSongArtwork(long maximumBytes = AssetResourceBudget.MaxDocumentBytes)
    {
        if (maximumBytes is < 1 or > AssetResourceBudget.MaxDocumentBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }

    public Entry Replace(int row, AssetBlob incoming)
    {
        if (IsClosed) throw new InvalidOperationException("歌曲信息工作台已关闭。");
        if (row < 0) throw new ArgumentOutOfRangeException(nameof(row));
        ArgumentNullException.ThrowIfNull(incoming);
        var candidate = _rows.Where(pair => pair.Key != row).Select(pair => pair.Value.Image)
            .Append(incoming);
        // An empty baseline intentionally disallows legacy growth exceptions for pending data.
        AssetResourceBudget.ValidateChange([], candidate, _maximumBytes);
        var retained = _rows.Values.FirstOrDefault(entry => entry.Image.ContentEquals(incoming))
            ?? new Entry(incoming);
        _rows[row] = retained;
        return retained;
    }

    public bool TryBeginLoad()
    {
        if (_loading || IsClosed) return false;
        _loading = true;
        return true;
    }

    public void EndLoad() => _loading = false;
    public void Close() { IsClosed = true; _rows.Clear(); }

    internal sealed class Entry(AssetBlob image)
    {
        private byte[]? _bytes;
        public AssetBlob Image { get; } = image;
        // The current SongInfoEdit contract uses byte[]. Reuse one snapshot per unique payload,
        // including across rows, instead of cloning the same large image in each Build call.
        public byte[] Bytes => _bytes ??= Image.ToArray();
    }
}
