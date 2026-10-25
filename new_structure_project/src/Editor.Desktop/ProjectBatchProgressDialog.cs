using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Application;

namespace Editor.Desktop;

/// <summary>Progress and per-project/per-task result list for the cross-project batch.</summary>
public sealed class ProjectBatchProgressDialog : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly ProgressBar _bar;
    private readonly TextBlock _text;
    private readonly ObservableCollection<string> _items = [];
    private readonly Button _cancelButton;
    private readonly Button _closeButton;
    private bool _finished;
    public Exception? Failure { get; private set; }
    public ProjectRenderBatchResult? Result { get; private set; }
    public CancellationToken Token => _cancel.Token;
    public IProgress<ProjectRenderProgress> Progress { get; }

    public ProjectBatchProgressDialog()
    {
        Title = "正在跨工程批量导出";
        Width = 700;
        Height = 560;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _text = new TextBlock
        {
            Name = "ProjectBatchProgressText", Text = "准备导出…", TextWrapping = TextWrapping.Wrap
        };
        _bar = new ProgressBar
        {
            Name = "ProjectBatchProgressBar", Minimum = 0, Maximum = 1, Value = 0, Height = 18
        };
        var list = new ListBox { Name = "ProjectBatchResultList", ItemsSource = _items };
        _cancelButton = new Button { Name = "CancelProjectBatchProgress", Content = "取消" };
        _cancelButton.Click += (_, _) =>
        {
            _cancel.Cancel();
            _cancelButton.IsEnabled = false;
            _text.Text = "正在取消；已完成文件会保留，当前文件不会留下半张 PNG。";
        };
        _closeButton = new Button
        {
            Name = "CloseProjectBatchProgress", Content = "关闭", IsEnabled = false, IsDefault = true
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
        panel.Children.Add(new ScrollViewer { Content = list });
        Content = panel;
        Progress = new Progress<ProjectRenderProgress>(value =>
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

    public void Complete(ProjectRenderBatchResult result)
    {
        _finished = true;
        _bar.Maximum = Math.Max(1, result.Projects.Sum(project => project.Tasks.Count));
        _bar.Value = result.Projects.Sum(project => project.Tasks.Count);
        var completed = result.Projects.Count(project => project.Status == ProjectRenderStatus.Completed);
        var failed = result.Projects.Count(project => project.Status == ProjectRenderStatus.Failed);
        _text.Text = $"{(result.Cancelled ? "已取消" : "已完成")}：成功工程 {completed}，失败工程 {failed}。\n" +
            $"输出位置：{result.Directory}";
        foreach (var project in result.Projects)
        {
            _items.Add($"{ProjectStatusLabel(project.Status)} · {project.ProjectName}" +
                (project.Error is null ? "" : " · " + project.Error));
            foreach (var task in project.Tasks)
                _items.Add($"    {TaskStatusLabel(task.Status)} · {task.FileName}" +
                    (task.Error is null ? "" : " · " + UiText.Translate(task.Error)));
        }
        if (result.ManifestError is not null) _items.Add("结果清单写入失败：" + result.ManifestError);
        else _items.Add("结果清单：" + result.ManifestPath);
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
        Stop("跨工程批量导出失败：" + error.Message);
    }

    private static string ProjectStatusLabel(ProjectRenderStatus status) => status switch
    {
        ProjectRenderStatus.Completed => "工程成功",
        ProjectRenderStatus.Cancelled => "工程取消",
        _ => "工程失败"
    };

    private static string TaskStatusLabel(PngExportTaskStatus status) => status switch
    {
        PngExportTaskStatus.Completed => "成功",
        PngExportTaskStatus.Failed => "失败",
        PngExportTaskStatus.Cancelled => "已取消",
        _ => "未执行"
    };
}
