using System.Text.Json;
using System.Text.Json.Serialization;
using Editor.Formats;

namespace Editor.Application;

public sealed record EditorPreferences
{
    public bool ShowRulers { get; init; } = true;
    public bool MoveWholeGroup { get; init; } = true;
    public bool ShowSafeArea { get; init; }
    public double SafeAreaMarginPercent { get; init; } = 10;
    public ExportQualityPolicy DefaultExportQualityPolicy { get; init; } =
        ExportQualityPolicy.Notify;

    public static EditorPreferences Default { get; } = new();
}

public sealed record EditorPreferencesLoadResult(
    EditorPreferences Preferences, string? Warning = null);

/// <summary>Independent user preferences file; never stored in a project or its extensions.</summary>
public sealed class EditorPreferencesStore
{
    public const int CurrentVersion = 2;
    private const long MaxBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static string DefaultPath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(
                "PPG_INTERNAL_VERIFY_PREFERENCES_PATH");
            return string.IsNullOrWhiteSpace(overridePath)
                ? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PhigrosPreviewEditor", "Preferences.json")
                : overridePath;
        }
    }

    public string Path { get; }

    public EditorPreferencesStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public EditorPreferencesLoadResult Load()
    {
        if (!File.Exists(Path))
            return new(EditorPreferences.Default);
        try
        {
            var file = new FileInfo(Path);
            if (file.Length <= 0 || file.Length > MaxBytes)
                return new(EditorPreferences.Default, "首选项文件尺寸无效，已使用默认设置。");
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path),
                new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("formatVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var versionNumber) ||
                versionNumber is < 1 or > CurrentVersion)
                return new(EditorPreferences.Default, "首选项版本不受支持，已使用默认设置。");
            var stored = JsonSerializer.Deserialize<Envelope>(root.GetRawText(), JsonOptions)
                ?? throw new InvalidDataException("空的首选项。");
            if (!Enum.IsDefined(stored.DefaultExportQualityPolicy))
                throw new InvalidDataException("无效的默认导出质量策略。");
            if (versionNumber == 1 && (stored.ShowSafeArea is not null ||
                stored.SafeAreaMarginPercent is not null))
                throw new InvalidDataException("首选项 v1 不能包含安全区字段。");
            if (versionNumber >= 2 && (stored.ShowSafeArea is null ||
                stored.SafeAreaMarginPercent is null))
                throw new InvalidDataException("首选项 v2 需要安全区字段。");
            var margin = stored.SafeAreaMarginPercent ?? 10;
            if (!double.IsFinite(margin) || margin is < 0 or > 20)
                throw new InvalidDataException("安全区边距必须在 0 到 20 之间。");
            return new(new EditorPreferences
            {
                ShowRulers = stored.ShowRulers,
                MoveWholeGroup = stored.MoveWholeGroup,
                ShowSafeArea = stored.ShowSafeArea ?? false,
                SafeAreaMarginPercent = margin,
                DefaultExportQualityPolicy = stored.DefaultExportQualityPolicy
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(EditorPreferences.Default, "首选项读取失败，已使用默认设置：" + error.Message);
        }
    }

    public void Save(EditorPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!Enum.IsDefined(preferences.DefaultExportQualityPolicy))
            throw new InvalidDataException("默认导出质量策略无效。");
        if (!double.IsFinite(preferences.SafeAreaMarginPercent) ||
            preferences.SafeAreaMarginPercent is < 0 or > 20)
            throw new InvalidDataException("安全区边距必须在 0 到 20 之间。");
        var envelope = new Envelope(CurrentVersion, preferences.ShowRulers,
            preferences.MoveWholeGroup, preferences.ShowSafeArea,
            preferences.SafeAreaMarginPercent, preferences.DefaultExportQualityPolicy);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("首选项路径缺少目录。");
        Directory.CreateDirectory(directory);
        AtomicFile.Write(Path, stream => stream.Write(bytes));
    }

    private sealed record Envelope(
        int FormatVersion, bool ShowRulers, bool MoveWholeGroup,
        bool? ShowSafeArea, double? SafeAreaMarginPercent,
        ExportQualityPolicy DefaultExportQualityPolicy);
}
