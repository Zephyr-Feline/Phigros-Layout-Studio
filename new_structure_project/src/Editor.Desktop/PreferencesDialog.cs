using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Editor.Application;

namespace Editor.Desktop;

/// <summary>Small user-level settings editor; never writes into a project.</summary>
public sealed class PreferencesDialog : Window
{
    private readonly CheckBox _showRulers;
    private readonly CheckBox _moveWholeGroup;
    private readonly CheckBox _showSafeArea;
    private readonly ComboBox _safeAreaMargin;
    private readonly ComboBox _qualityPolicy;

    public PreferencesDialog(EditorPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Title = "首选项";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _showRulers = new CheckBox
        {
            Name = "PreferenceShowRulers", Content = "显示标尺",
            IsChecked = preferences.ShowRulers
        };
        _moveWholeGroup = new CheckBox
        {
            Name = "PreferenceMoveWholeGroup", Content = "默认整组编辑",
            IsChecked = preferences.MoveWholeGroup
        };
        _showSafeArea = new CheckBox
        {
            Name = "PreferenceShowSafeArea", Content = "显示安全区",
            IsChecked = preferences.ShowSafeArea
        };
        var margins = new[] { "0%", "5%", "10%", "15%", "20%" };
        _safeAreaMargin = new ComboBox
        {
            Name = "PreferenceSafeAreaMargin",
            ItemsSource = margins,
            SelectedItem = margins.OrderBy(value => Math.Abs(ParseMargin(value) -
                preferences.SafeAreaMarginPercent)).First()
        };
        _qualityPolicy = new ComboBox
        {
            Name = "PreferenceQualityPolicy",
            ItemsSource = new[] { ExportQualityPolicy.Notify, ExportQualityPolicy.BlockLowResolution },
            SelectedItem = preferences.DefaultExportQualityPolicy
        };
        _qualityPolicy.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ExportQualityPolicy>(
            (value, _) => new TextBlock
            {
                Text = value switch
                {
                    ExportQualityPolicy.BlockLowResolution => "发现源图放大提示时阻止导出",
                    _ => "提示后继续"
                }
            });
        var save = new Button { Name = "SavePreferences", Content = "保存", IsDefault = true };
        save.Click += (_, _) => Close(CurrentPreferences());
        var cancel = new Button { Name = "CancelPreferences", Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var reset = new Button { Name = "ResetPreferences", Content = "恢复默认" };
        reset.Click += (_, _) =>
        {
            var defaults = EditorPreferences.Default;
            _showRulers.IsChecked = defaults.ShowRulers;
            _moveWholeGroup.IsChecked = defaults.MoveWholeGroup;
            _showSafeArea.IsChecked = defaults.ShowSafeArea;
            _safeAreaMargin.SelectedItem = margins.OrderBy(value => Math.Abs(ParseMargin(value) -
                defaults.SafeAreaMarginPercent)).First();
            _qualityPolicy.SelectedItem = defaults.DefaultExportQualityPolicy;
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { reset, cancel, save }
        };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "这些设置只影响本机编辑器，不写入 .ppgproj 工程。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        panel.Children.Add(_showRulers);
        panel.Children.Add(_moveWholeGroup);
        panel.Children.Add(_showSafeArea);
        panel.Children.Add(new TextBlock { Text = "安全区边距" });
        panel.Children.Add(_safeAreaMargin);
        panel.Children.Add(new TextBlock { Text = "默认导出质量策略" });
        panel.Children.Add(_qualityPolicy);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private EditorPreferences CurrentPreferences() => new()
    {
        ShowRulers = _showRulers.IsChecked == true,
        MoveWholeGroup = _moveWholeGroup.IsChecked == true,
        ShowSafeArea = _showSafeArea.IsChecked == true,
        SafeAreaMarginPercent = _safeAreaMargin.SelectedItem is string text
            ? ParseMargin(text) : 10,
        DefaultExportQualityPolicy = _qualityPolicy.SelectedItem is ExportQualityPolicy policy
            ? policy : ExportQualityPolicy.Notify
    };

    private static double ParseMargin(string value) =>
        double.TryParse(value.TrimEnd('%'), out var margin) ? margin : 10;
}
