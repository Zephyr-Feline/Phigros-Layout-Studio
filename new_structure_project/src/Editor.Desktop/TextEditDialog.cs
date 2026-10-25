using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Editor.Desktop;

/// <summary>A multiline edit commits once on confirmation; cancel never touches the document.</summary>
public sealed class TextEditDialog : Window
{
    public TextEditDialog(string text)
    {
        Title = "编辑文字";
        Width = 520; Height = 320; MinWidth = 320; MinHeight = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var input = new TextBox
        {
            Name = "TextEditValue", Text = text, AcceptsReturn = true, MaxLength = 4000,
            TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top
        };
        var cancel = new Button { Name = "CancelTextEdit", Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var confirm = new Button { Name = "ConfirmTextEdit", Content = "确定" };
        confirm.Click += (_, _) => Close(input.Text ?? "");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Thickness(0, 12, 0, 0), Children = { cancel, confirm }
        };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var help = new TextBlock { Text = "可输入多行文字，最多 4000 字。确定后可用 Ctrl+Z 撤销。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(help, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(help); panel.Children.Add(buttons); panel.Children.Add(input);
        Content = panel;
        Opened += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
