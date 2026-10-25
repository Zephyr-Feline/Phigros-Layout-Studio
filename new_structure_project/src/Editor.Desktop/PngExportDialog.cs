using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Editor.Application;
using Editor.Core;
using Editor.Rendering;

namespace Editor.Desktop;

/// <summary>Output-only sizing; edits here never enter the document or undo history.</summary>
public sealed class PngExportDialog : Window
{
    public PngExportDialog(EditorDocument document,
        Func<AssetBlob, (int Width, int Height)>? readImageDimensions = null,
        bool transparent = false,
        ExportQualityPolicy defaultPolicy = ExportQualityPolicy.Notify)
    {
        var snapshot = document.Copy();
        readImageDimensions ??= ImageAssetValidator.ReadEncodedDimensions;
        Title = "PNG 导出尺寸";
        Width = 430; SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var width = new TextBox { Name = "PngOutputWidth",
            Text = snapshot.CanvasWidth.ToString(CultureInfo.InvariantCulture) };
        var result = new TextBlock { Name = "PngOutputSize", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var quality = new TextBlock
        {
            Name = "PngOutputQuality", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxHeight = 120, FontSize = 11
        };
        var block = new CheckBox
        {
            Name = "PngBlockLowResolution",
            Content = "发现源图放大提示时禁止继续",
            IsChecked = defaultPolicy == ExportQualityPolicy.BlockLowResolution
        };
        var next = new Button { Name = "ConfirmPngSize", Content = "继续", IsDefault = true };
        var cancel = new Button { Name = "CancelPngSize", Content = "取消", IsCancel = true };
        var original = new Button { Name = "ResetPngSize", Content = "使用画布尺寸" };
        original.Click += (_, _) => width.Text = snapshot.CanvasWidth.ToString(CultureInfo.InvariantCulture);
        cancel.Click += (_, _) => Close();
        next.Click += (_, _) =>
        {
            if (next.IsEnabled && TrySize(out var size)) Close((int?)size.Width);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(cancel); buttons.Children.Add(next);
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "导出宽度（像素），高度按画布比例自动计算" });
        panel.Children.Add(width); panel.Children.Add(result);
        panel.Children.Add(new ScrollViewer { MaxHeight = 130, Content = quality });
        panel.Children.Add(block);
        panel.Children.Add(original);
        panel.Children.Add(new TextBlock { Text = "仅调整导出图片的尺寸，画布和图层保持原样。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(buttons); Content = panel;
        width.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) Update(); };
        block.IsCheckedChanged += (_, _) => Update();
        Update();

        bool TrySize(out PngExportSize size)
        {
            size = default;
            try
            {
                if (!int.TryParse(width.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                    throw new InvalidDataException("请输入大于 0 的整数宽度。");
                size = PngExportSize.Create(snapshot, value);
                result.Text = $"{size.Width} × {size.Height} 像素";
                return true;
            }
            catch (InvalidDataException error)
            {
                result.Text = error.Message switch
                {
                    "PNG output exceeds the 80 million pixel limit." => "导出图片超过 8000 万像素，请减小宽度。",
                    _ when error.Message.StartsWith("PNG width must be between", StringComparison.Ordinal) =>
                        $"导出宽度应为 1 至 {PngExportSize.MaxDimension} 像素。",
                    _ when error.Message.StartsWith("PNG height must be between", StringComparison.Ordinal) =>
                        $"计算后的高度应为 1 至 {PngExportSize.MaxDimension} 像素，请调整宽度。",
                    _ => error.Message
                };
                return false;
            }
        }
        void Update()
        {
            var valid = TrySize(out var selected);
            IReadOnlyList<ExportWarning> warnings = [];
            string? qualityError = null;
            if (valid)
            {
                try
                {
                    warnings = ExportPreflight.Inspect(snapshot, _ => false, selected.Width,
                            readImageDimensions, transparentBackground: transparent)
                        .Where(warning => warning.Kind == ExportWarningKind.LowResolutionArtwork)
                        .ToArray();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    qualityError = error.Message;
                }
            }
            var blocked = block.IsChecked == true && warnings.Count > 0;
            next.IsEnabled = valid && !blocked;
            quality.Text = !valid ? "" : qualityError is not null
                ? "有效分辨率检查暂不可用：" + qualityError
                : QualityText(warnings, blocked);
        }

        static string QualityText(IReadOnlyList<ExportWarning> warnings, bool blocked)
        {
            if (warnings.Count == 0) return "有效分辨率：未发现源图放大提示。";
            return (blocked ? "已按质量策略阻止继续。\n" : "") +
                $"有效分辨率提示 {warnings.Count} 项：\n" +
                string.Join("\n", warnings.Take(6).Select(warning =>
                    $"{UiText.LayerName(warning.LayerName)}：{UiText.Translate(warning.Message)}")) +
                (warnings.Count > 6 ? $"\n…另有 {warnings.Count - 6} 项" : "");
        }
    }
}
