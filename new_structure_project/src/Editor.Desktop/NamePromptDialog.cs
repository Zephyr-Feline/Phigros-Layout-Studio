using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Editor.Desktop;

public sealed class NamePromptDialog : Window
{
    public NamePromptDialog(string title, string prompt, string value, int maxLength = 120)
    {
        if (maxLength is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        Title = title;
        Width = 430;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var input = new TextBox { Name = "NamePromptValue", Text = value, MaxLength = maxLength };
        var cancel = new Button { Name = "CancelNamePrompt", Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var confirm = new Button { Name = "ConfirmNamePrompt", Content = "确定", IsDefault = true };
        confirm.Click += (_, _) => Close(input.Text ?? "");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, confirm }
        };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        Content = panel;
        Opened += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
