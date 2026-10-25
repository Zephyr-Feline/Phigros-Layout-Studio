using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core;

namespace Editor.Desktop;

public sealed record ImageCropSettings(ImageFitMode Fit, RectD Crop);

/// <summary>
/// Edits the normalized source sampling rectangle and fit mode. Asset bytes and logical Bounds
/// are deliberately not changed here.
/// </summary>
public sealed class ImageCropDialog : Window
{
    private readonly ComboBox _fit;
    private readonly TextBox _x;
    private readonly TextBox _y;
    private readonly TextBox _width;
    private readonly TextBox _height;
    private readonly TextBlock _error;
    private readonly TemplatePreview _preview;
    private readonly Button _apply;

    public ImageCropDialog(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        Title = "图片裁切与适配";
        Width = 500;
        Height = 470;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _fit = new ComboBox
        {
            Name = "ImageCropFit",
            ItemsSource = Enum.GetValues<ImageFitMode>(),
            SelectedItem = layer.EffectiveImageFit
        };
        _fit.ItemTemplate = new FuncDataTemplate<ImageFitMode>((value, _) =>
            new TextBlock { Text = value switch
            {
                ImageFitMode.Stretch => "拉伸（填满逻辑框）",
                ImageFitMode.Cover => "覆盖裁切（保持比例）",
                _ => "完整适配（留透明边）"
            } });
        _x = Field("ImageCropX", layer.ImageCrop.X);
        _y = Field("ImageCropY", layer.ImageCrop.Y);
        _width = Field("ImageCropWidth", layer.ImageCrop.Width);
        _height = Field("ImageCropHeight", layer.ImageCrop.Height);
        _error = new TextBlock
        {
            Name = "ImageCropError", TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 120))
        };
        _preview = new TemplatePreview { Name = "ImageCropPreview", Height = 120 };
        _apply = new Button { Name = "ApplyImageCrop", Content = "应用裁切", IsDefault = true };
        _apply.Click += (_, _) => Apply();
        var reset = new Button { Name = "ResetImageCrop", Content = "重置为完整图片" };
        reset.Click += (_, _) =>
        {
            _x.Text = "0"; _y.Text = "0"; _width.Text = "1"; _height.Text = "1";
            RefreshPreview();
        };
        var cancel = new Button { Name = "CancelImageCrop", Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, _apply }
        };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "裁切只改变图片层的采样区域；原图字节、图层 Bounds、旋转和编组保持不变。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock { Text = "适配方式", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(_fit);
        panel.Children.Add(new TextBlock
        {
            Text = "采样区域使用归一化坐标 0–1：X、Y 为左上角，宽高必须为正且不超出原图。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        });
        var crop = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        AddCell(crop, 0, 0, "裁切 X", _x);
        AddCell(crop, 1, 0, "裁切 Y", _y);
        AddCell(crop, 0, 1, "裁切宽度", _width);
        AddCell(crop, 1, 1, "裁切高度", _height);
        panel.Children.Add(crop);
        panel.Children.Add(_preview);
        panel.Children.Add(reset);
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel };
        foreach (var field in new[] { _x, _y, _width, _height })
            field.TextChanged += (_, _) => RefreshPreview();
        _fit.SelectionChanged += (_, _) => RefreshPreview();
        RefreshPreview();
    }

    private static TextBox Field(string name, double value) =>
        new() { Name = name, Text = value.ToString("0.###", CultureInfo.InvariantCulture) };

    private static void AddCell(Grid grid, int column, int row, string label, Control field)
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 6, 4) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11 });
        stack.Children.Add(field);
        Grid.SetColumn(stack, column); Grid.SetRow(stack, row);
        grid.Children.Add(stack);
    }

    private void Apply()
    {
        if (!TrySettings(out var settings, out var error))
        {
            _error.Text = error ?? "裁切设置无效。";
            return;
        }
        Close(settings);
    }

    private void RefreshPreview()
    {
        _error.Text = "";
        if (!TrySettings(out var settings, out var error))
        {
            _preview.Template = null;
            _apply.IsEnabled = false;
            _error.Text = error ?? "裁切设置无效。";
            return;
        }
        _preview.Template = new LayoutTemplate("crop-preview", "裁切区域",
            [settings.Crop]);
        _apply.IsEnabled = true;
    }

    private bool TrySettings(out ImageCropSettings settings, out string? error)
    {
        settings = new(ImageFitMode.Stretch, new RectD(0, 0, 1, 1));
        error = null;
        try
        {
            if (_fit.SelectedItem is not ImageFitMode fit)
                throw new InvalidDataException("请选择图片适配方式。");
            var x = Read(_x, "裁切 X");
            var y = Read(_y, "裁切 Y");
            var width = Read(_width, "裁切宽度");
            var height = Read(_height, "裁切高度");
            if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
                x + width > 1 + 1e-9 || y + height > 1 + 1e-9)
                throw new InvalidDataException("裁切区域必须位于 0–1 范围内，且宽高为正。");
            settings = new(fit, new RectD(x, y, width, height));
            return true;
        }
        catch (Exception failure)
        {
            error = failure.Message;
            return false;
        }
    }

    private static double Read(TextBox field, string label)
    {
        if (!double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
            out var value) || !double.IsFinite(value))
            throw new InvalidDataException(label + "必须是有效数字。");
        return value;
    }
}
