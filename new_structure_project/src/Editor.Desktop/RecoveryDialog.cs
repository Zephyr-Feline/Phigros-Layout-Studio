using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Application;

namespace Editor.Desktop;

public enum RecoveryAction { Restore, Discard }
public sealed record RecoveryDialogChoice(RecoveryAction Action, RecoveryCandidate Candidate);

/// <summary>Startup review of crash/exit snapshots; nothing is restored without an explicit choice.</summary>
public sealed class RecoveryDialog : Window
{
    private readonly ListBox _list;
    private readonly TextBlock _details;
    private readonly TextBlock _error;

    public RecoveryDialog(IReadOnlyList<RecoveryCandidate> candidates,
        IReadOnlyList<RecoveryDiagnostic> diagnostics)
    {
        Title = "发现自动恢复快照";
        Width = 720;
        Height = 520;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _list = new ListBox
        {
            Name = "RecoveryCandidateList", ItemsSource = candidates.ToArray(),
            SelectionMode = SelectionMode.Single
        };
        _details = new TextBlock
        {
            Name = "RecoveryDetails", TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gainsboro
        };
        _error = new TextBlock
        {
            Name = "RecoveryError", TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 120))
        };
        var restore = new Button
        {
            Name = "RestoreRecovery", Content = "恢复为未保存工程", IsDefault = true
        };
        var discard = new Button { Name = "DiscardRecovery", Content = "丢弃选中快照" };
        var later = new Button { Name = "LaterRecovery", Content = "稍后处理", IsCancel = true };
        restore.Click += (_, _) => Choose(RecoveryAction.Restore);
        discard.Click += (_, _) => Choose(RecoveryAction.Discard);
        later.Click += (_, _) => Close(null);
        _list.SelectionChanged += (_, _) => UpdateDetails();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { later, discard, restore }
        };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "上次编辑没有正常保存。请选择恢复为未保存工程、丢弃某份快照，或稍后处理。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "恢复只包含已经应用到工程的图层内容；侧栏尚未应用的草稿、撤销历史、选择、缩放和平移视图不会恢复。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        });
        panel.Children.Add(_list);
        panel.Children.Add(_details);
        if (diagnostics.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = $"{diagnostics.Count} 个恢复条目无法读取：" +
                    string.Join("；", diagnostics.Take(4).Select(item =>
                        $"{item.FileName}：{item.Message}")),
                TextWrapping = TextWrapping.Wrap, FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 120))
            });
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel };
        if (candidates.Count > 0) _list.SelectedIndex = 0;
        UpdateDetails();
        Opened += (_, _) => _list.Focus();
    }

    private void Choose(RecoveryAction action)
    {
        if (_list.SelectedItem is not RecoveryCandidate candidate)
        {
            _error.Text = "请先选择一个恢复快照。";
            return;
        }
        Close(new RecoveryDialogChoice(action, candidate));
    }

    private void UpdateDetails()
    {
        if (_list.SelectedItem is not RecoveryCandidate candidate)
        {
            _details.Text = "未选择快照。";
            return;
        }
        _details.Text = $"来源：{candidate.SourceLabel}\n" +
            $"保存时间：{candidate.SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
            $"画布：{candidate.CanvasWidth} × {candidate.CanvasHeight}；图层：{candidate.LayerCount}";
    }
}
