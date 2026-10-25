using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Application;

namespace Editor.Desktop;

/// <summary>Displays serial queue progress and retains the manifest location after completion.</summary>
public sealed class PngExportProgressDialog : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly ProgressBar _bar;
    private readonly TextBlock _text;
    private readonly ObservableCollection<string> _resultItems = [];
    private readonly ListBox _results;
    private readonly Button _cancelButton;
    private readonly Button _closeButton;
    private bool _finished;
    public Exception? Failure { get; private set; }
    public PngExportQueueResult? Result { get; private set; }
    public CancellationToken Token => _cancel.Token;
    public IProgress<PngExportQueueProgress> Progress { get; }

    public PngExportProgressDialog()
    {
        Title = "正在批量导出 PNG";
        Width = 620;
        Height = 480;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _text = new TextBlock
        {
            Name = "PngQueueProgressText", Text = "准备导出…", TextWrapping = TextWrapping.Wrap
        };
        _bar = new ProgressBar
        {
            Name = "PngQueueProgressBar", Minimum = 0, Maximum = 1, Value = 0, Height = 18
        };
        _results = new ListBox { Name = "PngQueueResultList", ItemsSource = _resultItems };
        _cancelButton = new Button { Name = "CancelPngQueueProgress", Content = "取消" };
        _cancelButton.Click += (_, _) =>
        {
            _cancel.Cancel();
            _cancelButton.IsEnabled = false;
            _text.Text = "正在取消；已完成文件会保留，当前文件不会留下半张 PNG。";
        };
        _closeButton = new Button
        {
            Name = "ClosePngQueueProgress", Content = "关闭", IsEnabled = false, IsDefault = true
        };
        _closeButton.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { _cancelButton, _closeButton }
        };
        var panel = new DockPanel { Margin = new Thickness(18), LastChildFill = true };
        var top = new StackPanel { Spacing = 10 };
        top.Children.Add(_text);
        top.Children.Add(_bar);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(top);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = _results });
        Content = panel;
        Progress = new Progress<PngExportQueueProgress>(value =>
        {
            if (_finished) return;
            _bar.Maximum = Math.Max(1, value.Total);
            _bar.Value = value.Completed;
            _text.Text = $"{value.Completed}/{value.Total}：{value.Status}";
        });
        Closing += (_, e) =>
        {
            if (_finished) return;
            e.Cancel = true;
            _cancel.Cancel();
            _cancelButton.IsEnabled = false;
            _text.Text = "正在取消；已完成文件会保留。";
        };
    }

    public void Complete(PngExportQueueResult result)
    {
        _finished = true;
        _bar.Maximum = Math.Max(1, result.Items.Count);
        _bar.Value = result.Items.Count;
        var completed = result.Items.Count(item => item.Status == PngExportTaskStatus.Completed);
        var failed = result.Items.Count(item => item.Status == PngExportTaskStatus.Failed);
        var skipped = result.Items.Count - completed - failed;
        _text.Text = $"{(result.Cancelled ? "已取消" : "已完成")}：成功 {completed}，失败 {failed}，取消/未执行 {skipped}。\n" +
            $"输出位置：{result.Directory}";
        foreach (var item in result.Items)
            _resultItems.Add($"{StatusLabel(item.Status)} · {item.FileName}" +
                (item.Size.Width > 0 ? $" · {item.Size.Width} × {item.Size.Height}" : "") +
                (item.Error is null ? "" : " · " + UiText.Translate(item.Error)));
        if (result.ManifestError is not null)
            _resultItems.Add("结果清单写入失败：" + result.ManifestError);
        else _resultItems.Add("结果清单：" + result.ManifestPath);
        _cancelButton.IsEnabled = false;
        _closeButton.IsEnabled = true;
        Result = result;
    }

    public void Stop(string message)
    {
        _finished = true;
        _text.Text = message;
        _cancelButton.IsEnabled = false;
        _closeButton.IsEnabled = true;
    }

    public void Fail(Exception error)
    {
        Failure = error;
        Stop("批量导出失败：" + error.Message);
    }

    private static string StatusLabel(PngExportTaskStatus status) => status switch
    {
        PngExportTaskStatus.Completed => "成功",
        PngExportTaskStatus.Failed => "失败",
        PngExportTaskStatus.Cancelled => "已取消",
        _ => "未执行"
    };
}
