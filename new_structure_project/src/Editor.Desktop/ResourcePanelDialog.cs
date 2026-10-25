using Avalonia;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Editor.Application;
using Editor.Core;
using Editor.Rendering;

namespace Editor.Desktop;

/// <summary>Read-only resource inventory with explicit reference location and safe replacement.</summary>
public sealed class ResourcePanelDialog : Window
{
    private readonly EditSession _session;
    private readonly Func<Window, Task<string?>> _chooseImage;
    private readonly Func<string, Task<LoadedImage>> _loadImage;
    private readonly Action<IReadOnlyList<Guid>> _locate;
    private readonly ListBox _list = new()
        { Name = "ResourceList", Height = 250, SelectionMode = SelectionMode.Single };
    private readonly ObservableCollection<AssetResource> _items = [];
    private readonly TextBlock _summary = new()
        { Name = "ResourceSummary", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock _details = new()
        { Name = "ResourceDetails", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new()
        { Name = "ResourceStatus", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock _error = new()
    {
        Name = "ResourceError", TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 120))
    };
    private bool _busy;
    private bool _closed;

    public bool DocumentChanged { get; private set; }
    public string? ChangeSummary { get; private set; }
    public bool BackgroundChanged { get; private set; }

    public ResourcePanelDialog(EditSession session, Func<Window, Task<string?>> chooseImage,
        Func<string, Task<LoadedImage>> loadImage, Action<IReadOnlyList<Guid>> locate)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _chooseImage = chooseImage ?? throw new ArgumentNullException(nameof(chooseImage));
        _loadImage = loadImage ?? throw new ArgumentNullException(nameof(loadImage));
        _locate = locate ?? throw new ArgumentNullException(nameof(locate));
        Title = "资源与质量检查";
        Width = 760;
        Height = 650;
        MinWidth = 560;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Closed += (_, _) => _closed = true;

        _list.ItemsSource = _items;
        _list.ItemTemplate = new FuncDataTemplate<AssetResource>((value, _) =>
            new TextBlock
            {
                Text = value?.Label ?? "",
                TextWrapping = TextWrapping.NoWrap,
                FontWeight = value?.HasWarning == true ? FontWeight.SemiBold : FontWeight.Normal
            });
        _list.SelectionChanged += (_, _) => UpdateDetails();

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "资源与质量检查",
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "展示内嵌图片的尺寸、编码占用和当前图层/固定背景引用。定位只改变当前选择；" +
                "安全替换会为所有当前引用分配新的资源 ID，并保留撤销历史中的旧资源。" +
                "未引用资源需要显式清理，清理本身可撤销。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(_summary);
        panel.Children.Add(_list);
        panel.Children.Add(_details);
        panel.Children.Add(_status);
        panel.Children.Add(_error);

        var actions = new WrapPanel();
        AddButton(actions, "定位引用", Locate, "LocateResourceReference");
        AddButton(actions, "安全替换…", ReplaceAsync, "ReplaceResource");
        AddButton(actions, "清理未引用资源…", CleanupUnreferencedAsync, "CleanupUnreferencedAssets");
        AddButton(actions, "刷新", () => Reload(), "RefreshResources");
        panel.Children.Add(actions);
        var close = new Button { Name = "CloseResourcePanel", Content = "关闭", IsCancel = true };
        close.Click += (_, _) => Close(DocumentChanged);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Children = { close }
        });
        Content = new ScrollViewer { Content = panel };
        Reload();
    }

    private void AddButton(Panel panel, string label, Action action, string name)
    {
        var button = new Button { Name = name, Content = label, Margin = new Thickness(2) };
        button.Click += (_, _) =>
        {
            if (_busy || _closed) return;
            try
            {
                _error.Text = "";
                action();
            }
            catch (Exception error) { if (!_closed) _error.Text = error.Message; }
        };
        panel.Children.Add(button);
    }

    private void AddButton(Panel panel, string label, Func<Task> action, string name)
    {
        var button = new Button { Name = name, Content = label, Margin = new Thickness(2) };
        button.Click += async (_, _) =>
        {
            if (_busy || _closed) return;
            _busy = true;
            try
            {
                _error.Text = "";
                await action();
            }
            catch (Exception error) { if (!_closed) _error.Text = error.Message; }
            finally { _busy = false; }
        };
        panel.Children.Add(button);
    }

    private void Reload(string? selectId = null)
    {
        var selected = selectId ?? (_list.SelectedItem as AssetResource)?.AssetId
            .ToString("N");
        var resources = AssetResources.Inspect(_session.Document,
            ImageAssetValidator.ReadEncodedDimensions);
        _items.Clear();
        foreach (var resource in resources) _items.Add(resource);
        _summary.Text = $"内嵌资源 {resources.Count} 个；未引用 {resources.Count(resource => resource.IsUnreferenced)} 个；" +
            $"共享 {resources.Count(resource => resource.IsShared)} 个；质量提示 " +
            $"{resources.Count(resource => resource.HasWarning)} 个。不会自动压缩；未引用资源需显式清理。";
        _list.SelectedItem = selected is null ? _items.FirstOrDefault() :
            _items.FirstOrDefault(resource => resource.AssetId.ToString("N") == selected) ??
            _items.FirstOrDefault();
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        var resource = _list.SelectedItem as AssetResource;
        if (resource is null)
        {
            _details.Text = "没有可显示的资源。";
            return;
        }
        var lines = new List<string>
        {
            $"资源 ID：{resource.AssetId}",
            $"SHA-256：{resource.Sha256}",
            $"编码大小：{resource.Length} 字节" +
                (resource.Width is int width && resource.Height is int height
                    ? $"；尺寸：{width} × {height}（{resource.Width * (long)resource.Height} 像素）"
                    : ""),
            resource.IsFixedBackground ? "固定背景：是" : "固定背景：否",
            resource.LayerReferences.Count == 0
                ? "图层引用：无"
                : "图层引用：" + string.Join("；", resource.LayerReferences.Select(reference =>
                    $"{UiText.LayerName(reference.LayerName)}" +
                    (reference.Visible ? "" : "（隐藏）") +
                    (reference.Locked ? "（锁定）" : "")))
        };
        if (resource.DimensionDiagnostic is not null)
            lines.Add("尺寸读取失败：" + resource.DimensionDiagnostic);
        if (resource.IsUnreferenced)
            lines.Add("提示：当前工程没有图层或固定背景引用此资源。");
        if (resource.IsLargeEncoded)
            lines.Add("提示：编码文件超过 8 MiB。");
        if (resource.IsHighResolution)
            lines.Add("提示：解码像素超过 1600 万。");
        _details.Text = string.Join("\n", lines);
    }

    private void Locate()
    {
        var resource = SelectedResource();
        var ids = resource.LayerReferences.Select(reference => reference.LayerId).ToArray();
        if (ids.Length == 0)
        {
            _status.Text = resource.IsFixedBackground
                ? "该资源只作为固定背景使用；当前没有可定位的图层引用。"
                : "该资源没有当前图层引用。";
            return;
        }
        _locate(ids);
        _status.Text = $"已选择 {ids.Length} 个引用图层。";
    }

    private async Task ReplaceAsync()
    {
        var resource = SelectedResource();
        var pending = new PendingDocumentOperation(_session);
        var path = await _chooseImage(this);
        if (_closed || path is null) return;
        pending.EnsureUnchanged("选择图片期间工程发生变化；请重新执行资源替换。");
        var loaded = await _loadImage(path);
        if (_closed) return;
        pending.EnsureUnchanged("读取图片期间工程发生变化；请重新执行资源替换。");
        var replacement = _session.ReplaceAsset(resource.AssetId, loaded.Image.ToArray(),
            NativeImaging.ValidateImages);
        DocumentChanged = true;
        BackgroundChanged |= resource.IsFixedBackground;
        ChangeSummary = "资源已安全替换；Ctrl+Z 可撤销。";
        Reload(replacement.ReplacementAssetId.ToString("N"));
        _status.Text = $"已安全替换资源；{replacement.LayerIds.Count} 个图层引用已更新，" +
            "固定背景引用（如有）同步更新。" +
            " Ctrl+Z 可撤销。";
    }

    private async Task CleanupUnreferencedAsync()
    {
        var pending = new PendingDocumentOperation(_session);
        var resources = AssetResources.Inspect(pending.Snapshot,
            ImageAssetValidator.ReadEncodedDimensions);
        var orphans = resources.Where(resource => resource.IsUnreferenced).ToArray();
        if (orphans.Length == 0)
        {
            _status.Text = "当前工程没有未引用资源。";
            return;
        }
        var totalBytes = orphans.Sum(resource => resource.Length);
        var listed = string.Join("\n", orphans.Take(5).Select(resource => "· " + resource.Label)) +
            (orphans.Length > 5 ? $"\n…另有 {orphans.Length - 5} 个" : "");
        var message = $"将删除 {orphans.Length} 个未被当前图层或固定背景引用的内嵌资源，" +
            $"释放约 {AssetResources.FormatBytes(totalBytes)}。\n\n{listed}\n\n" +
            "只影响当前工程；Ctrl+Z 可撤销整次清理。";
        var confirmed = await new ConfirmDialog("清理未引用资源", message, "清理")
            .ShowDialog<bool>(this);
        if (_closed) return;
        if (!confirmed)
        {
            _status.Text = "已取消资源清理。";
            return;
        }
        pending.EnsureUnchanged("确认清理期间工程发生变化；请重新检查资源清单。");
        var result = _session.CleanUnreferencedAssets();
        if (!result.Changed)
        {
            _status.Text = "当前工程没有未引用资源。";
            Reload();
            return;
        }
        DocumentChanged = true;
        ChangeSummary = $"已清理 {result.Count} 个未引用资源；Ctrl+Z 可撤销。";
        Reload();
        _status.Text = $"已清理 {result.Count} 个未引用资源，释放 " +
            $"{AssetResources.FormatBytes(result.Bytes)}。Ctrl+Z 可撤销。";
    }

    private AssetResource SelectedResource() =>
        _list.SelectedItem as AssetResource
        ?? throw new InvalidOperationException("请先选择一个内嵌资源。");
}
