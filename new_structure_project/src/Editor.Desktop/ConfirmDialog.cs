using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Editor.Desktop;

public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmLabel = "确定")
    {
        Title = title;
        Width = 430;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Name = "CancelConfirm", Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var confirm = new Button { Name = "AcceptConfirm", Content = confirmLabel, IsDefault = true };
        confirm.Click += (_, _) => Close(true);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, confirm }
        };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        panel.Children.Add(new TextBlock
        {
            Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        panel.Children.Add(buttons);
        Content = panel;
    }
}
