using System.Text.Json;
using System.Text.Json.Serialization;
using Editor.Core;
using Editor.Formats;

namespace Editor.Application;

public sealed record ExportPreset(
    string Id, string Name, IReadOnlyList<PngExportTask> Tasks, DateTimeOffset UpdatedUtc,
    ExportQualityPolicy QualityPolicy = ExportQualityPolicy.Notify)
{
    public override string ToString() => $"{Name} · {Tasks.Count} 个任务" +
        (QualityPolicy == ExportQualityPolicy.BlockLowResolution ? " · 阻止放大" : "");
}

public sealed record ExportPresetLoadResult(
    IReadOnlyList<ExportPreset> Presets, string? Warning = null);

/// <summary>Versioned user-level export task presets; independent of projects and output paths.</summary>
public sealed class ExportPresetLibrary
{
    public const int CurrentVersion = 2;
    public const int MaxPresets = 50;
    private const int MaxTasks = 32;
    private const long MaxBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static string DefaultPath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(
                "PPG_INTERNAL_VERIFY_EXPORT_PRESETS_PATH");
            return string.IsNullOrWhiteSpace(overridePath)
                ? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PhigrosPreviewEditor", "ExportPresets.json")
                : overridePath;
        }
    }

    private readonly string _path;
    private readonly List<ExportPreset> _presets = [];

    public string Path => _path;
    public IReadOnlyList<ExportPreset> Presets => _presets;
    public string? LoadWarning { get; private set; }

    public ExportPresetLibrary(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
    }

    public ExportPresetLoadResult Load()
    {
        _presets.Clear();
        LoadWarning = null;
        if (!File.Exists(_path)) return new([]);
        try
        {
            var file = new FileInfo(_path);
            if (file.Length <= 0 || file.Length > MaxBytes)
                throw new InvalidDataException("导出预设文件尺寸无效。");
            using var json = JsonDocument.Parse(File.ReadAllBytes(_path),
                new JsonDocumentOptions { MaxDepth = 24 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("formatVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var versionNumber) ||
                versionNumber is < 1 or > CurrentVersion)
                throw new InvalidDataException("导出预设版本不受支持。");
            var stored = JsonSerializer.Deserialize<Envelope>(root.GetRawText(), JsonOptions)
                ?? throw new InvalidDataException("导出预设为空。");
            if (stored.Presets is null || stored.Presets.Count > MaxPresets)
                throw new InvalidDataException("导出预设数量无效。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in stored.Presets)
            {
                if (item is null || !Guid.TryParseExact(item.Id, "N", out _) ||
                    string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 80 ||
                    item.Tasks is null || item.Tasks.Count is < 1 or > MaxTasks)
                    throw new InvalidDataException("导出预设条目无效。");
                if (!ids.Add(item.Id))
                    throw new InvalidDataException("导出预设 ID 重复。");
                var tasks = item.Tasks.Select(task =>
                {
                    if (task.Width is < 1 or > PngExportSize.MaxDimension)
                        throw new InvalidDataException("导出预设包含无效宽度。");
                    return new PngExportTask(task.Width, task.Transparent);
                }).ToArray();
                tasks = PngExportQueue.NormalizeTaskList(tasks, removeDuplicates: true).ToArray();
                if (versionNumber == 1 && item.QualityPolicy is not null)
                    throw new InvalidDataException("导出预设 v1 不能包含质量策略。");
                if (versionNumber >= 2 && item.QualityPolicy is null)
                    throw new InvalidDataException("导出预设 v2 需要质量策略。");
                var qualityPolicy = item.QualityPolicy ?? ExportQualityPolicy.Notify;
                if (!Enum.IsDefined(qualityPolicy))
                    throw new InvalidDataException("导出预设质量策略无效。");
                _presets.Add(new(item.Id, item.Name, tasks,
                    item.UpdatedUtc == default ? DateTimeOffset.UtcNow : item.UpdatedUtc,
                    qualityPolicy));
            }
            return new(_presets.AsReadOnly());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _presets.Clear();
            LoadWarning = "导出预设读取失败，已使用空列表：" + error.Message;
            return new([], LoadWarning);
        }
    }

    public ExportPreset Save(string? existingId, string name, IEnumerable<PngExportTask> tasks,
        ExportQualityPolicy qualityPolicy = ExportQualityPolicy.Notify)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 80)
            throw new InvalidDataException("预设名称必须为 1 到 80 个字符。");
        var rawTasks = tasks.ToArray();
        if (rawTasks.Length > MaxTasks)
            throw new InvalidDataException("导出预设最多包含 32 个任务。");
        var normalized = PngExportQueue.NormalizeTaskList(rawTasks, removeDuplicates: true)
            .ToArray();
        if (normalized.Length == 0)
            throw new InvalidDataException("预设至少需要一个导出任务。");
        if (!Enum.IsDefined(qualityPolicy))
            throw new InvalidDataException("预设质量策略无效。");
        var id = existingId ?? Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("预设 ID 无效。");
        var duplicateName = _presets.Any(preset => preset.Id != id &&
            string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
        if (duplicateName)
            throw new InvalidOperationException("已有同名导出预设，请使用不同名称。");
        var updated = new ExportPreset(id, name, normalized, DateTimeOffset.UtcNow,
            qualityPolicy);
        var index = _presets.FindIndex(preset => preset.Id == id);
        if (index >= 0)
        {
            _presets[index] = updated;
        }
        else
        {
            if (_presets.Count >= MaxPresets)
                throw new InvalidOperationException("导出预设数量已达到上限。");
            _presets.Add(updated);
        }
        Write();
        return updated;
    }

    public bool Delete(string id)
    {
        var index = _presets.FindIndex(preset => preset.Id == id);
        if (index < 0) return false;
        _presets.RemoveAt(index);
        Write();
        return true;
    }

    private void Write()
    {
        var envelope = new Envelope(CurrentVersion, _presets.Select(preset => new StoredPreset(
            preset.Id, preset.Name,
            preset.Tasks.Select(task => new StoredTask(task.Width, task.Transparent)).ToList(),
            preset.UpdatedUtc, preset.QualityPolicy)).ToList());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        AtomicFile.Write(_path, stream => stream.Write(bytes));
    }

    private sealed record Envelope(int FormatVersion, List<StoredPreset>? Presets);
    private sealed record StoredPreset(string Id, string Name, List<StoredTask>? Tasks,
        DateTimeOffset UpdatedUtc, ExportQualityPolicy? QualityPolicy = null);
    private sealed record StoredTask(int Width, bool Transparent);
}
