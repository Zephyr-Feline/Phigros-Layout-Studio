using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Application;
using Editor.Core;
using Editor.Rendering;

namespace Editor.Desktop;

public sealed record PngExportQueueRequest(
    IReadOnlyList<PngExportTask> Tasks, string ParentDirectory,
    ExportQualityPolicy QualityPolicy = ExportQualityPolicy.Notify,
    PngExportRegion? CropRegion = null);

internal sealed record PngQueueTaskItem(PngExportTask Task, PngExportSize Size,
    IReadOnlyList<ExportWarning> Warnings)
{
    public int LowResolutionCount => Warnings.Count(warning =>
        warning.Kind == ExportWarningKind.LowResolutionArtwork);
    public override string ToString() =>
        $"{Size.Width} × {Size.Height} · {(Task.Transparent ? "透明背景" : "普通背景")} · {Task.FileName}" +
        (LowResolutionCount == 0 ? "" : $" · 放大提示 {LowResolutionCount}");
}

/// <summary>Collects repeated width/mode tasks and warnings before any directory or PNG is created.</summary>
public sealed class PngExportQueueDialog : Window
{
    private readonly EditorDocument _document;
    private readonly Func<Window, Task<string?>> _chooseDirectory;
    private readonly Button _chooseDirectoryButton;
    private bool _choosingDirectory;
    private bool _closed;
    private readonly ObservableCollection<PngQueueTaskItem> _items = [];
    private readonly TextBox _width;
    private readonly ComboBox _mode;
    private readonly ListBox _tasks;
    private readonly TextBlock _taskCount;
    private readonly TextBlock _error;
    private readonly TextBlock _directory;
    private readonly TextBlock _resolution;
    private readonly TextBlock _qualitySummary;
    private readonly TextBlock _policyNote;
    private readonly CheckBox _blockLowResolution;
    private readonly ExportPresetLibrary _presetLibrary;
    private readonly ComboBox _presetSelector;
    private readonly TextBlock _presetStatus;
    private readonly Button _start;
    private readonly PngExportRegion? _selectionCropRegion;
    private readonly CheckBox? _cropToSelection;
    private PngExportRegion? _activeRegion;
    private bool _reprojectingTasks;
    private string? _parentDirectory;

    public PngExportQueueDialog(EditorDocument document,
        IReadOnlyList<ExportWarning> warnings, Func<Task<string?>> chooseDirectory,
        ExportQualityPolicy defaultPolicy = ExportQualityPolicy.Notify,
        ExportPresetLibrary? presetLibrary = null,
        PngExportRegion? selectionCropRegion = null,
        string? selectionCropNote = null)
        : this(document, warnings, WithOwner(chooseDirectory), defaultPolicy, presetLibrary,
            selectionCropRegion, selectionCropNote)
    {
    }

    public PngExportQueueDialog(EditorDocument document,
        IReadOnlyList<ExportWarning> warnings, Func<Window, Task<string?>> chooseDirectory,
        ExportQualityPolicy defaultPolicy = ExportQualityPolicy.Notify,
        ExportPresetLibrary? presetLibrary = null,
        PngExportRegion? selectionCropRegion = null,
        string? selectionCropNote = null)
    {
        _document = document.Copy();
        _chooseDirectory = chooseDirectory ?? throw new ArgumentNullException(nameof(chooseDirectory));
        Closed += (_, _) => _closed = true;
        _selectionCropRegion = selectionCropRegion;
        _presetLibrary = presetLibrary ?? new ExportPresetLibrary(ExportPresetLibrary.DefaultPath);
        _presetLibrary.Load();
        Title = "批量导出 PNG";
        Width = 680;
        Height = 620;
        MinWidth = 520;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _width = new TextBox
        {
            Name = "PngQueueWidth",
            Text = _document.CanvasWidth.ToString(CultureInfo.InvariantCulture)
        };
        _mode = new ComboBox
        {
            Name = "PngQueueMode",
            ItemsSource = new[] { "普通背景", "透明背景" },
            SelectedIndex = 0
        };
        if (_selectionCropRegion is not null)
        {
            _cropToSelection = new CheckBox
            {
                Name = "PngQueueCropToSelection",
                Content = "裁切到选中范围"
            };
            _cropToSelection.IsCheckedChanged += (_, _) => ReprojectTasks();
        }
        var add = new Button { Name = "AddPngQueueTask", Content = "添加任务" };
        add.Click += (_, _) => AddTask();
        var remove = new Button { Name = "RemovePngQueueTask", Content = "移除选中" };
        remove.Click += (_, _) => RemoveSelected();
        var dedupe = new Button { Name = "DeduplicatePngQueueTasks", Content = "去除重复任务" };
        dedupe.Click += (_, _) => RemoveDuplicates();
        _tasks = new ListBox
        {
            Name = "PngQueueTaskList", Height = 150, ItemsSource = _items,
            SelectionMode = SelectionMode.Single
        };
        _taskCount = new TextBlock { Name = "PngQueueTaskCount", Text = "任务数：0" };
        _resolution = new TextBlock
        {
            Name = "PngQueueResolution", Text = "选择任务可查看有效分辨率提示。",
            TextWrapping = TextWrapping.Wrap, MaxHeight = 100, FontSize = 11
        };
        _qualitySummary = new TextBlock
        {
            Name = "PngQueueQualitySummary", Text = "质量策略：未发现源图放大提示。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        };
        _presetSelector = new ComboBox
        {
            Name = "PngPresetSelector",
            ItemsSource = _presetLibrary.Presets,
            MinWidth = 220
        };
        _presetStatus = new TextBlock
        {
            Name = "PngPresetStatus", TextWrapping = TextWrapping.Wrap, FontSize = 11
        };
        var applyPreset = new Button { Name = "ApplyPngPreset", Content = "应用预设" };
        applyPreset.Click += (_, _) => ApplyPreset();
        var savePreset = new Button { Name = "SavePngPreset", Content = "保存当前为预设" };
        savePreset.Click += async (_, _) => await SavePreset();
        var deletePreset = new Button { Name = "DeletePngPreset", Content = "删除预设" };
        deletePreset.Click += (_, _) => DeletePreset();
        var presetActions = new WrapPanel();
        presetActions.Children.Add(_presetSelector);
        presetActions.Children.Add(applyPreset);
        presetActions.Children.Add(savePreset);
        presetActions.Children.Add(deletePreset);
        _policyNote = new TextBlock
        {
            Name = "PngQueuePolicyNote", TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 120))
        };
        _blockLowResolution = new CheckBox
        {
            Name = "PngQueueBlockLowResolution",
            Content = "发现源图放大提示时阻止导出",
            IsChecked = defaultPolicy == ExportQualityPolicy.BlockLowResolution
        };
        _blockLowResolution.IsCheckedChanged += (_, _) => UpdateStartState();
        _tasks.SelectionChanged += (_, _) => UpdateResolution();
        _error = new TextBlock
        {
            Name = "PngQueueError", TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 120))
        };
        _directory = new TextBlock
        {
            Name = "PngQueueDirectory", Text = "尚未选择输出目录。",
            TextWrapping = TextWrapping.Wrap
        };
        _chooseDirectoryButton = new Button { Name = "ChoosePngQueueDirectory", Content = "选择输出目录" };
        _chooseDirectoryButton.Click += async (_, _) => await ChooseDirectory();
        _start = new Button
        {
            Name = "StartPngQueueExport", Content = "开始导出", IsDefault = true, IsEnabled = false
        };
        _start.Click += (_, _) => Start();
        var cancel = new Button { Name = "CancelPngQueue", Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(null);

        var taskRow = new Grid { ColumnDefinitions = new ColumnDefinitions("110,130,Auto,Auto,Auto") };
        taskRow.Children.Add(_width);
        Grid.SetColumn(_mode, 1);
        taskRow.Children.Add(_mode);
        Grid.SetColumn(add, 2);
        taskRow.Children.Add(add);
        Grid.SetColumn(remove, 3);
        taskRow.Children.Add(remove);
        Grid.SetColumn(dedupe, 4);
        taskRow.Children.Add(dedupe);
        _width.Margin = new Thickness(0, 0, 6, 0);
        _mode.Margin = new Thickness(0, 0, 6, 0);
        add.Margin = new Thickness(0, 0, 6, 0);
        remove.Margin = new Thickness(0, 0, 6, 0);

        var warningText = warnings.Count == 0
            ? "预检：未发现提示。"
            : $"预检提示 {warnings.Count} 项：\n" + string.Join("\n",
                warnings.Take(12).Select(warning =>
                    $"{UiText.LayerName(warning.LayerName)}：{UiText.Translate(warning.Message)}")) +
                (warnings.Count > 12 ? $"\n…另有 {warnings.Count - 12} 项" : "");
        var warningBlock = new TextBlock
        {
            Name = "PngQueueWarning", Text = warningText, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 130, FontSize = 11
        };
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
            Text = "每个任务使用同一个已提交工程快照。文件写入本次专用的新子目录，已存在文件不会被覆盖。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock { Text = "输出宽度 / 背景模式", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(taskRow);
        if (selectionCropNote is not null)
            panel.Children.Add(new TextBlock
            {
                Name = "PngQueueCropNote",
                Text = selectionCropNote,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            });
        if (_cropToSelection is not null)
            panel.Children.Add(_cropToSelection);
        panel.Children.Add(_taskCount);
        panel.Children.Add(_tasks);
        panel.Children.Add(new TextBlock { Text = "导出预设", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(presetActions);
        panel.Children.Add(_presetStatus);
        panel.Children.Add(_error);
        panel.Children.Add(warningBlock);
        panel.Children.Add(_qualitySummary);
        panel.Children.Add(_resolution);
        panel.Children.Add(_blockLowResolution);
        panel.Children.Add(_policyNote);
        panel.Children.Add(new TextBlock { Text = "输出位置", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(_directory);
        panel.Children.Add(_chooseDirectoryButton);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel };
        ReloadPresets(null);
        if (_presetLibrary.LoadWarning is not null)
            _presetStatus.Text = _presetLibrary.LoadWarning;
        AddTask(_document.CanvasWidth, transparent: false);
    }

    private static Func<Window, Task<string?>> WithOwner(Func<Task<string?>> chooseDirectory)
    {
        ArgumentNullException.ThrowIfNull(chooseDirectory);
        return _ => chooseDirectory();
    }

    private PngExportRegion? CurrentRegion => _activeRegion;

    private PngQueueTaskItem ProjectTask(PngExportTask task, PngExportRegion? region)
    {
        var size = PngExportSize.Create(_document, task.Width, region);
        var warnings = ExportPreflight.Inspect(_document, LocalFontRegistry.IsMissing, task.Width,
            ImageAssetValidator.ReadEncodedDimensions, task.Transparent, region);
        return new(task, size, warnings);
    }

    private void AddTask() => AddTask(ReadWidth(), _mode.SelectedIndex == 1);

    private void AddTask(int width, bool transparent)
    {
        _error.Text = "";
        try
        {
            var projected = ProjectTask(new PngExportTask(width, transparent), CurrentRegion);
            var duplicate = _items.Any(item => item.Task.Width == width && item.Task.Transparent == transparent);
            _items.Add(projected);
            _tasks.SelectedItem = _items[^1];
            UpdateTaskCount();
            if (duplicate) _error.Text = "检测到重复任务；可点击“去除重复任务”。";
        }
        catch (Exception error) { _error.Text = UiText.Translate(error.Message); }
    }

    private void ReprojectTasks()
    {
        if (_reprojectingTasks) return;
        var region = _cropToSelection?.IsChecked == true ? _selectionCropRegion : null;
        PngQueueTaskItem[] projected;
        try
        {
            // Validate every task before replacing any row. A rejected mode change must keep
            // the original queue, selection and enabled state available to the user.
            projected = _items.Select(item => ProjectTask(item.Task, region)).ToArray();
        }
        catch (Exception error)
        {
            _reprojectingTasks = true;
            try { _cropToSelection!.IsChecked = CurrentRegion is not null; }
            finally { _reprojectingTasks = false; }
            _error.Text = "无法切换裁切模式，原任务已保留：" + UiText.Translate(error.Message);
            return;
        }
        var selectedIndex = _tasks.SelectedIndex;
        _activeRegion = region;
        _items.Clear();
        foreach (var item in projected) _items.Add(item);
        _tasks.SelectedIndex = selectedIndex;
        _error.Text = "";
        UpdateTaskCount();
    }

    private int ReadWidth()
    {
        if (!int.TryParse(_width.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var width))
            throw new InvalidDataException("PNG 宽度必须是正整数。");
        return width;
    }

    private void RemoveSelected()
    {
        if (_tasks.SelectedItem is PngQueueTaskItem item) _items.Remove(item);
        else _error.Text = "请先选择要移除的任务。";
        UpdateTaskCount();
    }

    private void RemoveDuplicates()
    {
        var seen = new HashSet<(int, bool)>();
        for (var index = _items.Count - 1; index >= 0; index--)
            if (!seen.Add((_items[index].Task.Width, _items[index].Task.Transparent)))
                _items.RemoveAt(index);
        UpdateTaskCount();
        _error.Text = "已去除重复的宽度和背景模式。";
    }

    private void UpdateTaskCount()
    {
        _taskCount.Text = $"任务数：{_items.Count}";
        UpdateResolution();
        UpdateStartState();
    }

    private void UpdateResolution()
    {
        if (_tasks.SelectedItem is not PngQueueTaskItem item)
        {
            _resolution.Text = "选择任务可查看有效分辨率提示。";
            return;
        }
        try
        {
            var warnings = item.Warnings
                .Where(warning => warning.Kind == ExportWarningKind.LowResolutionArtwork)
                .ToArray();
            _resolution.Text = warnings.Length == 0
                ? "当前任务有效分辨率：未发现源图放大提示。"
                : $"当前任务有效分辨率提示 {warnings.Length} 项：\n" +
                    string.Join("\n", warnings.Take(5).Select(warning =>
                        $"{UiText.LayerName(warning.LayerName)}：{UiText.Translate(warning.Message)}")) +
                    (warnings.Length > 5 ? $"\n…另有 {warnings.Length - 5} 项" : "");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _resolution.Text = "有效分辨率检查暂不可用：" + error.Message;
        }
    }

    private void ApplyPreset()
    {
        if (_presetSelector.SelectedItem is not ExportPreset preset)
        {
            _presetStatus.Text = "请先选择一个导出预设。";
            return;
        }
        PngQueueTaskItem[] projected;
        try
        {
            projected = preset.Tasks.Select(task => ProjectTask(task, CurrentRegion)).ToArray();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _presetStatus.Text = "无法应用预设，原任务已保留：" + UiText.Translate(error.Message);
            return;
        }
        _items.Clear();
        foreach (var item in projected) _items.Add(item);
        _tasks.SelectedIndex = _items.Count - 1;
        _blockLowResolution.IsChecked =
            preset.QualityPolicy == ExportQualityPolicy.BlockLowResolution;
        _error.Text = "";
        UpdateTaskCount();
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
                _items.Select(item => item.Task),
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

    private void UpdateStartState()
    {
        var lowResolution = _items.Count(item => item.LowResolutionCount > 0);
        _qualitySummary.Text = lowResolution == 0
            ? "质量策略：未发现源图放大提示。"
            : $"质量策略：{lowResolution} / {_items.Count} 个任务存在源图放大提示。";
        var blocked = _blockLowResolution.IsChecked == true && lowResolution > 0;
        _start.IsEnabled = !_closed && !_choosingDirectory &&
            _parentDirectory is not null && _items.Count > 0 && !blocked;
        _policyNote.Text = blocked
            ? "已按质量策略阻止开始导出；取消勾选或减小任务输出宽度后可继续。"
            : "";
    }

    private async Task ChooseDirectory()
    {
        if (_closed || _choosingDirectory) return;
        _choosingDirectory = true;
        _chooseDirectoryButton.IsEnabled = false;
        UpdateStartState();
        try
        {
            var path = await _chooseDirectory(this);
            if (_closed || path is null) return;
            _parentDirectory = Path.GetFullPath(path);
            _directory.Text = "父目录：" + _parentDirectory + "\n将自动创建新的 PhigrosExport-日期-时间 子目录。";
        }
        catch (Exception error)
        {
            if (!_closed) _error.Text = UiText.Translate(error.Message);
        }
        finally
        {
            _choosingDirectory = false;
            if (!_closed)
            {
                _chooseDirectoryButton.IsEnabled = true;
                UpdateStartState();
            }
        }
    }

    private void Start()
    {
        if (_closed || _choosingDirectory) return;
        _error.Text = "";
        try
        {
            if (_parentDirectory is null) throw new InvalidOperationException("请先选择输出目录。");
            if (_blockLowResolution.IsChecked == true && _items.Any(item =>
                    item.LowResolutionCount > 0))
                throw new InvalidOperationException(
                    "当前有源图放大提示；请取消“阻止导出”或调整任务后再开始。");
            var region = CurrentRegion;
            var tasks = PngExportQueue.Normalize(_document, _items.Select(item => item.Task), false,
                region);
            Close(new PngExportQueueRequest(tasks, _parentDirectory,
                _blockLowResolution.IsChecked == true
                    ? ExportQualityPolicy.BlockLowResolution : ExportQualityPolicy.Notify,
                region));
        }
        catch (Exception error) { _error.Text = UiText.Translate(error.Message); }
    }
}
