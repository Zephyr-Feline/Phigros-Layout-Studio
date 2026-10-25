using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Editor.Application;

namespace Editor.Desktop;

/// <summary>Shows coarse batch progress and cancels between complete image reads.</summary>
public sealed class BatchImageProgressDialog : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly ProgressBar _bar;
    private readonly TextBlock _text;
    private readonly Button _cancelButton;
    private bool _finished;
    public Exception? Failure { get; private set; }
    public CancellationToken Token => _cancel.Token;
    public IProgress<BatchImageImportProgress> Progress { get; }

    public BatchImageProgressDialog(string title = "批量导入图片")
    {
        Title = title;
        Width = 460;
        Height = 175;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _text = new TextBlock
        {
            Name = "BatchImportProgressText",
            Text = "准备读取所选图片…",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        _bar = new ProgressBar
        {
            Name = "BatchImportProgressBar", Minimum = 0, Maximum = 1, Value = 0, Height = 18
        };
        _cancelButton = new Button { Name = "CancelBatchImport", Content = "取消", IsCancel = true };
        _cancelButton.Click += (_, _) =>
        {
            _cancel.Cancel();
            _cancelButton.IsEnabled = false;
            _text.Text = "正在取消；已经读取的图片不会导入。";
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _cancelButton }
        };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(_text);
        panel.Children.Add(_bar);
        panel.Children.Add(new TextBlock
        {
            Text = "按文件选择器返回的顺序导入；全部读取成功后才写入工程。",
            FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        panel.Children.Add(buttons);
        Content = panel;
        Progress = new Progress<BatchImageImportProgress>(value =>
        {
            _bar.Maximum = Math.Max(1, value.Total);
            _bar.Value = value.Completed;
            _text.Text = $"正在读取 {value.Completed}/{value.Total}：{value.FileName}";
        });
        Closing += (_, e) =>
        {
            if (_finished) return;
            e.Cancel = true;
            _cancel.Cancel();
            _cancelButton.IsEnabled = false;
            _text.Text = "正在取消；已经读取的图片不会导入。";
        };
    }

    public void Finish(int result)
    {
        _finished = true;
        Close(result);
    }

    public void Fail(Exception error)
    {
        Failure = error;
        _finished = true;
        Close(-2);
    }
}
