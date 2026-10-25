using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Application;
using Editor.Core;
using Editor.Formats;

namespace Editor.Desktop;

/// <summary>
/// User-facing manager for reusable, content-free song styles. All document mutations go through
/// EditSession and remain one undo entry per apply or instantiate action.
/// </summary>
public sealed class SongStyleLibraryDialog : Window
{
    private readonly EditSession _session;
    private readonly SongStyleLibrary _library;
    private readonly Func<IReadOnlyList<Guid>> _selection;
    private readonly TextBox _search = new()
        { Name = "SongStyleSearch", PlaceholderText = "查找歌曲样式名称" };
    private readonly ListBox _list = new()
        { Name = "SongStyleList", Height = 170, SelectionMode = SelectionMode.Single };
    private readonly ObservableCollection<SongStyleLibraryEntry> _items = [];
    private readonly SongStylePreview _preview = new()
        { Name = "SongStylePreview", Height = 150 };
    private readonly TextBlock _status = new()
        { Name = "SongStyleLibraryStatus", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock _error = new()
    {
        Name = "SongStyleLibraryError", TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 120))
    };
    private IReadOnlyList<SongStyleLibraryEntry> _all = [];
    private bool _busy;

    public bool DocumentChanged { get; private set; }

    public SongStyleLibraryDialog(EditSession session, SongStyleLibrary library,
        Func<IReadOnlyList<Guid>> selection)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        Title = "歌曲样式库";
        Width = 720;
        Height = 680;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Name = "SongStyleHeader",
            Text = "歌曲样式库（独立于 .ppglayout）",
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "样式只保存角色结构、相对几何、字体引用和视觉样式，不保存正文、图片或字体字节。" +
                "应用会保留目标歌曲的文字、曲绘和难度内容；从样式新建实例会生成全新的图层、组和图片引用。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "库目录：" + _library.DirectoryPath,
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gainsboro
        });
        panel.Children.Add(_search);
        _list.ItemsSource = _items;
        _list.SelectionChanged += (_, _) => UpdatePreview();
        _search.TextChanged += (_, _) => ApplyFilter();
        panel.Children.Add(_list);
        panel.Children.Add(_preview);
        panel.Children.Add(_status);
        panel.Children.Add(_error);

        var actions = new WrapPanel();
        AddButton(actions, "捕获当前歌曲组样式…", CaptureAsync, "CaptureSongStyle");
        AddButton(actions, "应用到选中歌曲组（一次撤销）", ApplySelected,
            "ApplySongStyle");
        AddButton(actions, "用选中歌曲内容新建实例", InstantiateSelected,
            "InstantiateSongStyle");
        AddButton(actions, "刷新", Reload, "RefreshSongStyles");
        AddButton(actions, "重命名样式", RenameAsync, "RenameSongStyle");
        AddButton(actions, "移除样式", RemoveAsync, "RemoveSongStyle");
        panel.Children.Add(actions);

        var close = new Button { Name = "CloseSongStyleLibrary", Content = "关闭", IsCancel = true };
        close.Click += (_, _) => Close(DocumentChanged);
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Children = { close }
        };
        panel.Children.Add(footer);
        Content = new ScrollViewer { Content = panel };
        Reload();
    }

    private void AddButton(Panel panel, string label, Action action, string name)
    {
        var button = new Button { Name = name, Content = label, Margin = new Thickness(2) };
        button.Click += (_, _) =>
        {
            if (_busy) return;
            try
            {
                _error.Text = "";
                action();
            }
            catch (Exception error) { _error.Text = error.Message; }
        };
        panel.Children.Add(button);
    }

    private void AddButton(Panel panel, string label, Func<Task> action, string name)
    {
        var button = new Button { Name = name, Content = label, Margin = new Thickness(2) };
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            try
            {
                _error.Text = "";
                await action();
            }
            catch (Exception error) { _error.Text = error.Message; }
            finally { _busy = false; }
        };
        panel.Children.Add(button);
    }

    private void Reload() => Reload(null);

    private void Reload(string? selectId)
    {
        var selectedId = selectId ??
            (_list.SelectedItem as SongStyleLibraryEntry)?.Id;
        try
        {
            var snapshot = _library.Load();
            _all = snapshot.Entries;
            var text = $"本地歌曲样式：{_all.Count} 项。";
            if (snapshot.Diagnostics.Count > 0)
            {
                var first = snapshot.Diagnostics[0];
                text += $" {snapshot.Diagnostics.Count} 个文件未加载" +
                    $"（{first.FileName}：{UiText.Translate(first.Message)}）";
            }
            _status.Text = text;
        }
        catch (Exception error)
        {
            _all = [];
            _status.Text = "歌曲样式库无法读取。";
            _error.Text = error.Message;
        }
        ApplyFilter(selectedId);
    }

    private void ApplyFilter(string? selectId = null)
    {
        var selectedId = selectId ??
            (_list.SelectedItem as SongStyleLibraryEntry)?.Id;
        var query = (_search.Text ?? "").Trim();
        var filtered = _all.Where(entry => query.Length == 0 ||
            entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _items.Clear();
        foreach (var entry in filtered) _items.Add(entry);
        _list.SelectedItem = selectedId is null ? filtered.FirstOrDefault() :
            filtered.FirstOrDefault(entry => entry.Id == selectedId) ?? filtered.FirstOrDefault();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        _preview.Style = (_list.SelectedItem as SongStyleLibraryEntry)?.Style;
    }

    private SongStyleLibraryEntry SelectedEntry() =>
        _list.SelectedItem as SongStyleLibraryEntry
        ?? throw new InvalidOperationException("请先在歌曲样式库中选择一项。");

    private async Task CaptureAsync()
    {
        var groups = SelectedGroups();
        if (groups.Length != 1)
            throw new InvalidOperationException("捕获样式前请只选择一个歌曲组。");
        var name = await new NamePromptDialog("捕获歌曲样式",
            "名称只用于本地样式库；不会写入工程，也不会改变图层名称。",
            "我的歌曲样式").ShowDialog<string?>(this);
        if (name is null) return;
        var style = SongStyles.Capture(_session.Document, groups[0], name);
        var result = _library.Add(style);
        Reload(result.Entry.Id);
        _status.Text = result.Added
            ? $"已捕获“{result.Entry.Name}”，重启编辑器后仍可使用。"
            : "相同名称和内容的歌曲样式已存在，未重复写入。";
    }

    private void ApplySelected()
    {
        var style = SelectedEntry().Style;
        var ids = _selection();
        var groups = SelectedGroups(ids);
        if (groups.Length == 0)
            throw new InvalidOperationException("请先选择至少一个歌曲组，再应用歌曲样式。");
        _session.ApplySongStyle(style, ids);
        DocumentChanged = true;
        _status.Text = $"已将“{style.Name}”应用到 {groups.Length} 个歌曲组；" +
            "Ctrl+Z 可撤销整次应用。";
    }

    private void InstantiateSelected()
    {
        var style = SelectedEntry().Style;
        var ids = _selection();
        var groups = SelectedGroups(ids);
        if (groups.Length != 1)
            throw new InvalidOperationException("新建样式实例前请只选择一个歌曲组作为内容来源。");
        var source = ids.First(id => _session.Document.Find(id).GroupId == groups[0]);
        var created = _session.InstantiateSongStyle(style, source);
        DocumentChanged = true;
        _status.Text = $"已按“{style.Name}”创建 {created.Count} 个新角色图层；" +
            "组、图层与图片引用均为新 ID，Ctrl+Z 可撤销。";
    }

    private async Task RenameAsync()
    {
        var entry = SelectedEntry();
        var name = await new NamePromptDialog("重命名歌曲样式",
            "名称只影响本地歌曲样式库的显示。", entry.Name)
            .ShowDialog<string?>(this);
        if (name is null) return;
        var renamed = _library.Rename(entry.Id, name);
        Reload(renamed.Id);
        _status.Text = $"歌曲样式已重命名为“{renamed.Name}”。";
    }

    private async Task RemoveAsync()
    {
        var entry = SelectedEntry();
        var accepted = await new ConfirmDialog("移除歌曲样式",
            $"仅从本地歌曲样式库移除“{entry.Name}”；当前工程中的图层不会被删除。",
            "移除").ShowDialog<bool>(this);
        if (!accepted) return;
        _library.Remove(entry.Id);
        Reload();
        _status.Text = $"已从本地歌曲样式库移除“{entry.Name}”。";
    }

    private Guid[] SelectedGroups() => SelectedGroups(_selection());

    private Guid[] SelectedGroups(IEnumerable<Guid> ids)
    {
        var document = _session.Document;
        return ids.Distinct().Where(id => document.Layers.Any(layer => layer.Id == id))
            .Select(id => document.Find(id).GroupId).OfType<Guid>().Distinct().ToArray();
    }
}
