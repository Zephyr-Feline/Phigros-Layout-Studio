using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Application;

namespace Editor.Desktop;

public sealed record ProjectBatchRenderRequest(
    string OutputParent, IReadOnlyList<string> ProjectPaths, IReadOnlyList<PngExportTask> Tasks,
    ExportQualityPolicy QualityPolicy = ExportQualityPolicy.Notify);

internal sealed record ProjectBatchTaskItem(PngExportTask Task)
{
    public override string ToString() =>
        $"{Task.Width} 像素 · {(Task.Transparent ? "透明背景" : "普通背景")}";
}

/// <summary>Collects multiple native projects and output tasks without opening or switching a project.</summary>
public sealed class ProjectBatchRenderDialog : Window
{
    private readonly Func<Window, Task<IReadOnlyList<string>>> _chooseProjects;
    private readonly Func<Window, Task<string?>> _chooseDirectory;
    private readonly Button _chooseProjectsButton;
    private readonly Button _chooseDirectoryButton;
    private bool _choosingPaths;
    private bool _closed;
    private readonly ObservableCollection<ProjectBatchTaskItem> _taskItems = [];
    private readonly ListBox _projects = new() { Name = "ProjectBatchProjectList", Height = 130 };
    private readonly ListBox _tasks = new() { Name = "ProjectBatchTaskList", Height = 130 };
    private readonly TextBox _width = new() { Name = "ProjectBatchWidth", Text = "1920" };
    private readonly ComboBox _mode = new()
        { Name = "ProjectBatchMode", ItemsSource = new[] { "普通背景", "透明背景" }, SelectedIndex = 0 };
    private readonly TextBlock _count;
    private readonly TextBlock _error;
    private readonly TextBlock _directory = new()
        { Name = "ProjectBatchDirectory", Text = "尚未选择输出父目录。", TextWrapping = TextWrapping.Wrap };
    private readonly Button _start;
    private readonly CheckBox _blockLowResolution;
    private readonly ExportPresetLibrary _presetLibrary;
    private readonly ComboBox _presetSelector;
    private readonly TextBlock _presetStatus;
    private string? _parent;

    public ProjectBatchRenderDialog(Func<Task<IReadOnlyList<string>>> chooseProjects,
        Func<Task<string?>> chooseDirectory,
        ExportQualityPolicy defaultPolicy = ExportQualityPolicy.Notify,
        ExportPresetLibrary? presetLibrary = null)
        : this(WithOwner(chooseProjects, nameof(chooseProjects)),
            WithOwner(chooseDirectory, nameof(chooseDirectory)), defaultPolicy, presetLibrary)
    {
    }

    public ProjectBatchRenderDialog(Func<Window, Task<IReadOnlyList<string>>> chooseProjects,
        Func<Window, Task<string?>> chooseDirectory,
        ExportQualityPolicy defaultPolicy = ExportQualityPolicy.Notify,
        ExportPresetLibrary? presetLibrary = null)
    {
        _chooseProjects = chooseProjects ?? throw new ArgumentNullException(nameof(chooseProjects));
        _chooseDirectory = chooseDirectory ?? throw new ArgumentNullException(nameof(chooseDirectory));
        Closed += (_, _) => _closed = true;
        _presetLibrary = presetLibrary ?? new ExportPresetLibrary(ExportPresetLibrary.DefaultPath);
        _presetLibrary.Load();
        Title = "跨工程批量导出 PNG";
        Width = 760;
        Height = 700;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _count = new TextBlock { Name = "ProjectBatchTaskCount", Text = "任务数：0" };
        _tasks.ItemsSource = _taskItems;
        _error = new TextBlock
        {
            Name = "ProjectBatchError", TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 120))
        };
        _start = new Button
        {
            Name = "StartProjectBatch", Content = "开始批量导出", IsDefault = true, IsEnabled = false
        };
        _start.Click += (_, _) => Start();
        _blockLowResolution = new CheckBox
        {
            Name = "ProjectBatchBlockLowResolution",
            Content = "发现源图放大提示时阻止该工程导出",
            IsChecked = defaultPolicy == ExportQualityPolicy.BlockLowResolution
        };
        _presetSelector = new ComboBox
        {
            Name = "ProjectBatchPresetSelector",
            ItemsSource = _presetLibrary.Presets,
            MinWidth = 220
        };
        _presetStatus = new TextBlock
        {
            Name = "ProjectBatchPresetStatus", TextWrapping = TextWrapping.Wrap, FontSize = 11
        };
        var applyPreset = new Button { Name = "ApplyProjectBatchPreset", Content = "应用预设" };
        applyPreset.Click += (_, _) => ApplyPreset();
        var savePreset = new Button { Name = "SaveProjectBatchPreset", Content = "保存当前为预设" };
        savePreset.Click += async (_, _) => await SavePreset();
        var deletePreset = new Button { Name = "DeleteProjectBatchPreset", Content = "删除预设" };
        deletePreset.Click += (_, _) => DeletePreset();
        var presetActions = new WrapPanel();
        presetActions.Children.Add(_presetSelector);
        presetActions.Children.Add(applyPreset);
        presetActions.Children.Add(savePreset);
        presetActions.Children.Add(deletePreset);
        _chooseProjectsButton = new Button
        {
            Name = "ChooseProjectBatchProjects", Content = "选择原生工程（可多选）"
        };
        _chooseProjectsButton.Click += async (_, _) => await ChooseProjects();
        var addTask = new Button { Name = "AddProjectBatchTask", Content = "添加任务" };
        addTask.Click += (_, _) => AddTask();
        var removeTask = new Button { Name = "RemoveProjectBatchTask", Content = "移除选中任务" };
        removeTask.Click += (_, _) => RemoveTask();
        var dedupe = new Button { Name = "DeduplicateProjectBatchTasks", Content = "去除重复任务" };
        dedupe.Click += (_, _) => RemoveDuplicates();
        _chooseDirectoryButton = new Button
        {
            Name = "ChooseProjectBatchDirectory", Content = "选择输出父目录"
        };
        _chooseDirectoryButton.Click += async (_, _) => await ChooseDirectory();
        var cancel = new Button { Name = "CancelProjectBatch", Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var taskRow = new Grid { ColumnDefinitions = new ColumnDefinitions("110,130,Auto,Auto,Auto") };
        taskRow.Children.Add(_width);
        Grid.SetColumn(_mode, 1); taskRow.Children.Add(_mode);
        Grid.SetColumn(addTask, 2); taskRow.Children.Add(addTask);
        Grid.SetColumn(removeTask, 3); taskRow.Children.Add(removeTask);
        Grid.SetColumn(dedupe, 4); taskRow.Children.Add(dedupe);
        _width.Margin = new Thickness(0, 0, 6, 0);
        _mode.Margin = new Thickness(0, 0, 6, 0);
        addTask.Margin = new Thickness(0, 0, 6, 0);
        removeTask.Margin = new Thickness(0, 0, 6, 0);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, _start }
        };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "不会切换当前编辑工程。每份工程单独读取、验证和渲染；单份工程失败会记录并继续其他工程。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(_chooseProjectsButton);
        panel.Children.Add(_projects);
        panel.Children.Add(new TextBlock { Text = "输出任务（应用于每份工程）", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(taskRow);
        panel.Children.Add(_count);
        panel.Children.Add(_tasks);
        panel.Children.Add(new TextBlock { Text = "导出预设", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(presetActions);
        panel.Children.Add(_presetStatus);
        panel.Children.Add(_error);
        panel.Children.Add(_blockLowResolution);
        panel.Children.Add(new TextBlock
        {
            Text = "策略在执行时逐工程检查；存在放大提示的工程会失败并跳过，其他工程继续。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        });
        panel.Children.Add(new TextBlock { Text = "输出位置", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(_directory);
        panel.Children.Add(_chooseDirectoryButton);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel };
        ReloadPresets(null);
        if (_presetLibrary.LoadWarning is not null)
            _presetStatus.Text = _presetLibrary.LoadWarning;
        AddTask();
    }

    private static Func<Window, Task<T>> WithOwner<T>(Func<Task<T>> picker, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(picker, parameterName);
        return _ => picker();
    }

    private void AddTask()
    {
        try
        {
            if (!int.TryParse(_width.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var width))
                throw new InvalidDataException("输出宽度必须是正整数。");
            var duplicate = _taskItems.Any(item =>
                item.Task.Width == width && item.Task.Transparent == (_mode.SelectedIndex == 1));
            _taskItems.Add(new(new PngExportTask(width, _mode.SelectedIndex == 1)));
            _tasks.SelectedItem = _taskItems[^1];
            UpdateCount();
            if (duplicate) _error.Text = "检测到重复任务；可点击“去除重复任务”。";
        }
        catch (Exception error) { _error.Text = UiText.Translate(error.Message); }
    }

    private void RemoveTask()
    {
        if (_tasks.SelectedItem is ProjectBatchTaskItem item) _taskItems.Remove(item);
        else _error.Text = "请先选择要移除的任务。";
        UpdateCount();
    }

    private void RemoveDuplicates()
    {
        var seen = new HashSet<(int, bool)>();
        for (var index = _taskItems.Count - 1; index >= 0; index--)
            if (!seen.Add((_taskItems[index].Task.Width, _taskItems[index].Task.Transparent)))
                _taskItems.RemoveAt(index);
        UpdateCount();
        _error.Text = "已去除重复任务。";
    }

    private void ApplyPreset()
    {
        if (_presetSelector.SelectedItem is not ExportPreset preset)
        {
            _presetStatus.Text = "请先选择一个导出预设。";
            return;
        }
        _taskItems.Clear();
        foreach (var task in preset.Tasks)
            _taskItems.Add(new(new PngExportTask(task.Width, task.Transparent)));
        _tasks.SelectedItem = _taskItems.LastOrDefault();
        _blockLowResolution.IsChecked =
            preset.QualityPolicy == ExportQualityPolicy.BlockLowResolution;
        UpdateCount();
        _presetStatus.Text = $"已应用预设“{preset.Name}”。";
    }

    private async Task SavePreset()
    {
        var selected = _presetSelector.SelectedItem as ExportPreset;
        var name = await new NamePromptDialog("保存导出预设", "预设名称",
            selected?.Name ?? $"导出预设 {_presetLibrary.Presets.Count + 1}")
            .ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var saved = _presetLibrary.Save(selected?.Id, name,
                _taskItems.Select(item => item.Task),
                _blockLowResolution.IsChecked == true
                    ? ExportQualityPolicy.BlockLowResolution : ExportQualityPolicy.Notify);
            ReloadPresets(saved.Id);
            _presetStatus.Text = $"已保存导出预设“{saved.Name}”。";
        }
        catch (Exception error) { _presetStatus.Text = UiText.Translate(error.Message); }
    }

    private void DeletePreset()
    {
        if (_presetSelector.SelectedItem is not ExportPreset preset)
        {
            _presetStatus.Text = "请先选择一个导出预设。";
            return;
        }
        try
        {
            _presetLibrary.Delete(preset.Id);
            ReloadPresets(null);
            _presetStatus.Text = $"已删除导出预设“{preset.Name}”。";
        }
        catch (Exception error) { _presetStatus.Text = UiText.Translate(error.Message); }
    }

    private void ReloadPresets(string? selectedId)
    {
        _presetSelector.ItemsSource = null;
        _presetSelector.ItemsSource = _presetLibrary.Presets;
        _presetSelector.SelectedItem = selectedId is null
            ? _presetLibrary.Presets.FirstOrDefault()
            : _presetLibrary.Presets.FirstOrDefault(preset => preset.Id == selectedId)
                ?? _presetLibrary.Presets.FirstOrDefault();
    }

    private void UpdateCount()
    {
        _count.Text = $"任务数：{_taskItems.Count}";
        _start.IsEnabled = !_closed && !_choosingPaths &&
            _parent is not null && _taskItems.Count > 0 && _projects.Items.Count > 0;
    }

    private async Task ChooseProjects()
    {
        if (_closed || _choosingPaths) return;
        SetChoosingPaths(true);
        try
        {
            var paths = await _chooseProjects(this);
            if (_closed || paths.Count == 0) return;
            var normalized = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _projects.Items.Clear();
            foreach (var path in normalized) _projects.Items.Add(new ProjectPathItem(path));
        }
        catch (Exception error)
        {
            if (!_closed) _error.Text = UiText.Translate(error.Message);
        }
        finally { SetChoosingPaths(false); }
    }

    private async Task ChooseDirectory()
    {
        if (_closed || _choosingPaths) return;
        SetChoosingPaths(true);
        try
        {
            var path = await _chooseDirectory(this);
            if (_closed || path is null) return;
            _parent = Path.GetFullPath(path);
            _directory.Text = "父目录：" + _parent;
        }
        catch (Exception error)
        {
            if (!_closed) _error.Text = UiText.Translate(error.Message);
        }
        finally { SetChoosingPaths(false); }
    }

    private void SetChoosingPaths(bool choosing)
    {
        _choosingPaths = choosing;
        if (_closed) return;
        _chooseProjectsButton.IsEnabled = !choosing;
        _chooseDirectoryButton.IsEnabled = !choosing;
        UpdateCount();
    }

    private void Start()
    {
        if (_closed || _choosingPaths) return;
        _error.Text = "";
        try
        {
            if (_parent is null) throw new InvalidOperationException("请先选择输出父目录。");
            var paths = _projects.Items.OfType<ProjectPathItem>().Select(item => item.Path).ToArray();
            if (paths.Length == 0) throw new InvalidOperationException("请至少选择一份原生工程。");
            var tasks = PngExportQueue.NormalizeTaskList(_taskItems.Select(item => item.Task), false);
            Close(new ProjectBatchRenderRequest(_parent, paths, tasks,
                _blockLowResolution.IsChecked == true
                    ? ExportQualityPolicy.BlockLowResolution : ExportQualityPolicy.Notify));
        }
        catch (Exception error) { _error.Text = UiText.Translate(error.Message); }
    }

    private sealed record ProjectPathItem(string Path)
    {
        public override string ToString() => Path;
    }
}
