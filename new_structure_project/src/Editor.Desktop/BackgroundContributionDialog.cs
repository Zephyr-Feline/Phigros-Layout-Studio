using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Core;

namespace Editor.Desktop;

/// <summary>
/// Explicit background contributor selection plus range/transition controls. The dialog receives
/// a snapshot; applying passes one complete BackgroundBlendSettings value through its callback.
/// </summary>
public sealed class BackgroundContributionDialog : Window
{
    private readonly EditorDocument _document;
    private readonly Func<BackgroundBlendSettings, string?> _apply;
    private readonly ComboBox _mode = new()
    {
        Name = "BackgroundContributionMode",
        ItemsSource = new[] { "自动：全部可见曲绘", "仅使用勾选的曲绘" }
    };
    private readonly TextBox _range = new() { Name = "BackgroundRange" };
    private readonly TextBox _transition = new() { Name = "BackgroundTransition" };
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly TextBlock _status = new()
        { Name = "BackgroundContributionStatus", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock _error = new()
    {
        Name = "BackgroundContributionError", TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 120))
    };
    private readonly List<Choice> _choices = [];
    private bool _updatingMode;

    public bool Applied { get; private set; }

    public BackgroundContributionDialog(EditorDocument document,
        Func<BackgroundBlendSettings, string?> apply)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        var settings = document.BackgroundBlend;
        Title = "背景贡献与过渡";
        Width = 720;
        Height = 620;
        MinWidth = 560;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _mode.SelectedIndex = settings.ContributionMode == BackgroundContributionMode.Selected ? 1 : 0;
        _mode.SelectionChanged += (_, _) => RefreshMode();
        _range.Text = settings.Range.ToString("0.###", CultureInfo.InvariantCulture);
        _transition.Text = settings.Transition.ToString("0.###", CultureInfo.InvariantCulture);

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "背景贡献",
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "自动模式使用全部可见的斜切曲绘；开启“包含平面图片”后才包含非斜切图片。" +
                "选择模式只使用下方勾选且可见、透明度大于 0 的图片。范围控制颜色扩散距离，" +
                "过渡柔和度越小边缘越明显。",
            TextWrapping = TextWrapping.Wrap
        });
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        modeRow.Children.Add(new TextBlock
        {
            Text = "贡献范围",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        modeRow.Children.Add(_mode);
        panel.Children.Add(modeRow);
        panel.Children.Add(_rows);
        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        AddField(fields, 0, "扩散范围（0.25–2.0）", _range);
        AddField(fields, 1, "过渡柔和度（0–1）", _transition);
        panel.Children.Add(fields);
        panel.Children.Add(_status);
        panel.Children.Add(_error);

        var applyButton = new Button
        {
            Name = "ApplyBackgroundContribution", Content = "应用背景贡献设置",
            IsDefault = true
        };
        applyButton.Click += (_, _) => Apply();
        var cancel = new Button
        {
            Name = "CancelBackgroundContribution", Content = "取消", IsCancel = true
        };
        cancel.Click += (_, _) => Close(false);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, applyButton }
        });
        Content = new ScrollViewer { Content = panel };

        foreach (var layer in document.Layers.Where(layer => layer.Kind == LayerKind.Image))
        {
            var eligible = layer.Visible && layer.Opacity > 0 &&
                (layer.Slanted || settings.IncludeFlatImages);
            var reason = !layer.Visible ? "已隐藏"
                : layer.Opacity <= 0 ? "透明度为 0"
                : !layer.Slanted && !settings.IncludeFlatImages ? "平面图片未启用"
                : null;
            var choice = new CheckBox
            {
                Name = "BackgroundContributor_" + _choices.Count,
                Content = UiText.LayerName(layer.Name) +
                    (reason is null ? "" : $"（{reason}，不参与）"),
                IsChecked = eligible &&
                    (settings.ContributionMode != BackgroundContributionMode.Selected ||
                     (settings.SelectedArtworkLayerIds ?? []).Contains(layer.Id)),
                IsEnabled = eligible,
                Tag = layer.Id
            };
            _choices.Add(new Choice(layer.Id, layer.Name, eligible, choice));
            _rows.Children.Add(choice);
        }
        RefreshMode();
    }

    private static void AddField(Grid grid, int column, string label, Control field)
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 6, 0) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 11 });
        stack.Children.Add(field);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private void RefreshMode()
    {
        if (_updatingMode) return;
        _updatingMode = true;
        try
        {
            var selected = _mode.SelectedIndex == 1;
            foreach (var choice in _choices)
            {
                if (selected)
                {
                    choice.View.IsEnabled = choice.Eligible;
                    if (!choice.Eligible) choice.View.IsChecked = false;
                }
                else
                {
                    choice.View.IsChecked = choice.Eligible;
                    choice.View.IsEnabled = false;
                }
            }
            var count = selected
                ? _choices.Count(choice => choice.Eligible && choice.View.IsChecked == true)
                : _choices.Count(choice => choice.Eligible);
            _status.Text = selected
                ? $"选择模式：当前勾选 {count} 张可见曲绘。未勾选、隐藏、透明度为 0 或未启用平面图片的图层不会参与。"
                : $"自动模式：当前有 {count} 张曲绘符合贡献条件。";
        }
        finally { _updatingMode = false; }
    }

    private void Apply()
    {
        _error.Text = "";
        if (!double.TryParse(_range.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var range) || !double.IsFinite(range) || range is < 0.25 or > 2)
        {
            _error.Text = "扩散范围必须是 0.25 到 2.0 之间的数字。";
            return;
        }
        if (!double.TryParse(_transition.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var transition) || !double.IsFinite(transition) ||
            transition is < 0 or > 1)
        {
            _error.Text = "过渡柔和度必须是 0 到 1 之间的数字。";
            return;
        }
        var selected = _mode.SelectedIndex == 1;
        var ids = selected
            ? _choices.Where(choice => choice.Eligible && choice.View.IsChecked == true)
                .Select(choice => choice.Id).Distinct().ToArray()
            : null;
        if (selected && ids!.Length == 0)
        {
            _error.Text = "选择模式至少需要勾选一张可参与背景的曲绘。";
            return;
        }
        var settings = _document.BackgroundBlend with
        {
            ContributionMode = selected ? BackgroundContributionMode.Selected :
                BackgroundContributionMode.Automatic,
            SelectedArtworkLayerIds = ids,
            Range = range,
            Transition = transition
        };
        var error = _apply(settings);
        if (error is not null)
        {
            _error.Text = error;
            return;
        }
        Applied = true;
        Close(true);
    }

    private sealed record Choice(Guid Id, string Name, bool Eligible, CheckBox View);
}
