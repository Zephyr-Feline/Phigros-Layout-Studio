using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Application;
using Editor.Core;

namespace Editor.Desktop;

public sealed record SongGridRequest(SongGridSettings Settings, IReadOnlyList<string> Paths);

/// <summary>Configure a grid, preview the normalized slots, and confirm file order before reading.</summary>
public sealed class SongGridDialog : Window
{
    private readonly EditorDocument _document;
    private readonly Func<Window, Task<IReadOnlyList<string>>> _chooseImages;
    private readonly TextBox _rows;
    private readonly TextBox _columns;
    private readonly TextBox _marginX;
    private readonly TextBox _marginY;
    private readonly TextBox _gapX;
    private readonly TextBox _gapY;
    private readonly TemplatePreview _preview = new() { Name = "SongGridPreview", Height = 180 };
    private readonly TextBlock _count;
    private readonly TextBlock _error;
    private readonly ListBox _files;
    private readonly Button _chooseImagesButton;
    private readonly Button _generate;
    private IReadOnlyList<string> _paths = [];
    private bool _closed;
    private bool _choosingImages;

    public SongGridDialog(EditorDocument document,
        Func<Window, Task<IReadOnlyList<string>>> chooseImages)
    {
        _document = document.Copy();
        _chooseImages = chooseImages ?? throw new ArgumentNullException(nameof(chooseImages));
        Closed += (_, _) => _closed = true;
        Title = "生成 N 曲网格";
        Width = 760;
        Height = 700;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _rows = Field("SongGridRows", "2");
        _columns = Field("SongGridColumns", "2");
        _marginX = Field("SongGridMarginX", "40");
        _marginY = Field("SongGridMarginY", "40");
        _gapX = Field("SongGridGapX", "24");
        _gapY = Field("SongGridGapY", "24");
        _count = new TextBlock { Name = "SongGridCount", TextWrapping = TextWrapping.Wrap };
        _error = new TextBlock
        {
            Name = "SongGridError", TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 120))
        };
        _files = new ListBox { Name = "SongGridFiles", Height = 110 };
        _generate = new Button { Name = "GenerateSongGrid", Content = "读取并生成", IsDefault = true };
        _generate.Click += (_, _) => Generate();
        _chooseImagesButton = new Button { Name = "ChooseSongGridImages", Content = "选择图片并预览顺序" };
        _chooseImagesButton.Click += async (_, _) => await ChooseImages();
        var cancel = new Button { Name = "CancelSongGrid", Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(null);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        var rowField = new StackPanel { Spacing = 4 };
        AddField(rowField, "行数", _rows);
        AddField(rowField, "列数", _columns);
        var gapField = new StackPanel { Spacing = 4 };
        AddField(gapField, "水平边距", _marginX);
        AddField(gapField, "垂直边距", _marginY);
        var paddingField = new StackPanel { Spacing = 4 };
        AddField(paddingField, "水平间距", _gapX);
        AddField(paddingField, "垂直间距", _gapY);
        Grid.SetColumn(rowField, 0); grid.Children.Add(rowField);
        Grid.SetColumn(gapField, 1); grid.Children.Add(gapField);
        Grid.SetColumn(paddingField, 2); grid.Children.Add(paddingField);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, _generate }
        };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "网格会复用现有六层歌曲卡；原三曲/五点预设不变。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(grid);
        panel.Children.Add(_count);
        panel.Children.Add(_preview);
        panel.Children.Add(_chooseImagesButton);
        panel.Children.Add(new TextBlock
        {
            Text = "图片按选择顺序放入槽位：从左到右、从上到下。生成前只显示文件名，不读取图片。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        });
        panel.Children.Add(_files);
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel };
        foreach (var box in new[] { _rows, _columns, _marginX, _marginY, _gapX, _gapY })
            box.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();
    }

    private static TextBox Field(string name, string text) => new() { Name = name, Text = text };

    private static void AddField(Panel panel, string label, Control field)
    {
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11 });
        panel.Children.Add(field);
    }

    private void RefreshPreview()
    {
        _error.Text = "";
        if (!TrySettings(out var settings, out var error))
        {
            _preview.Template = null;
            _count.Text = "网格配置无效。";
            _generate.IsEnabled = false;
            if (error is not null) _error.Text = error;
            return;
        }
        _preview.Template = SongGrid.CreateTemplate(_document.CanvasWidth, _document.CanvasHeight, settings);
        _count.Text = $"共 {settings.Count} 首歌曲；每个槽位一张曲绘。";
        _generate.IsEnabled = !_closed && !_choosingImages && _paths.Count == settings.Count;
        if (_paths.Count > 0 && _paths.Count != settings.Count)
            _error.Text = $"当前选择了 {_paths.Count} 张图片，网格需要 {settings.Count} 张。";
    }

    private async Task ChooseImages()
    {
        if (_closed || _choosingImages) return;
        _choosingImages = true;
        _chooseImagesButton.IsEnabled = false;
        _generate.IsEnabled = false;
        try
        {
            var paths = await _chooseImages(this);
            if (_closed || paths.Count == 0) return;
            // Validate the complete result before replacing the last confirmed file order.
            var normalized = paths.Select(Path.GetFullPath).ToArray();
            _paths = normalized;
            _files.Items.Clear();
            for (var index = 0; index < _paths.Count; index++)
                _files.Items.Add($"{index + 1}. {Path.GetFileName(_paths[index])}");
            RefreshPreview();
        }
        catch (Exception error)
        {
            if (!_closed) _error.Text = UiText.Translate(error.Message);
        }
        finally
        {
            _choosingImages = false;
            if (!_closed)
            {
                _chooseImagesButton.IsEnabled = true;
                _generate.IsEnabled = TrySettings(out var settings, out _) && _paths.Count == settings.Count;
            }
        }
    }

    private void Generate()
    {
        if (_closed || _choosingImages) return;
        if (!TrySettings(out var settings, out var error))
        {
            _error.Text = error ?? "网格配置无效。";
            return;
        }
        if (_paths.Count != settings.Count)
        {
            _error.Text = $"请选择 {settings.Count} 张曲绘；当前为 {_paths.Count} 张。";
            return;
        }
        Close(new SongGridRequest(settings, _paths));
    }

    private bool TrySettings(out SongGridSettings settings, out string? error)
    {
        settings = new SongGridSettings(0, 0);
        error = null;
        try
        {
            if (!int.TryParse(_rows.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var rows) ||
                !int.TryParse(_columns.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var columns))
                throw new InvalidDataException("行数和列数必须是正整数。");
            if (!double.TryParse(_marginX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var marginX) ||
                !double.TryParse(_marginY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var marginY) ||
                !double.TryParse(_gapX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gapX) ||
                !double.TryParse(_gapY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gapY))
                throw new InvalidDataException("边距和间距必须是数字，小数使用英文句点。");
            settings = new SongGridSettings(rows, columns, marginX, marginY, gapX, gapY);
            settings.Validate(_document.CanvasWidth, _document.CanvasHeight);
            return true;
        }
        catch (Exception failure)
        {
            error = failure.Message;
            return false;
        }
    }
}
