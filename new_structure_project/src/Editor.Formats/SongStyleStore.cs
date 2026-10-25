using System.Text.Json;
using Editor.Core;

namespace Editor.Formats;

/// <summary>
/// Versioned content-free song style files. Assets, text, difficulty values and template IDs are
/// never serialized; only role structure, relative geometry and visual style are retained.
/// </summary>
public static class SongStyleStore
{
    public const string Extension = ".ppgsongstyle";
    public const int CurrentVersion = 1;
    public const int MaxBytes = 131072;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static void Save(string path, SongStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = CurrentVersion,
            id = style.Id,
            name = style.Name,
            referenceAspectRatio = style.ReferenceAspectRatio,
            layers = style.Layers.Select(layer => new
            {
                role = layer.Role.ToString(),
                kind = layer.Kind.ToString(),
                x = layer.X,
                y = layer.Y,
                width = layer.Width,
                height = layer.Height,
                visible = layer.Visible,
                locked = layer.Locked,
                keepAspect = layer.KeepAspect,
                slanted = layer.Slanted,
                opacity = layer.Opacity,
                imageFit = layer.ImageFit?.ToString(),
                rotationDegrees = layer.RotationDegrees,
                fontSizeRatio = layer.FontSizeRatio,
                fontFamily = layer.FontFamily,
                bold = layer.Bold,
                italic = layer.Italic,
                lineSpacing = layer.LineSpacing,
                alignment = layer.Alignment.ToString(),
                verticalAlignment = layer.VerticalAlignment.ToString(),
                colorArgb = layer.ColorArgb
            }).ToArray()
        }, WriteOptions);
        if (bytes.Length > MaxBytes)
            throw new InvalidDataException("歌曲样式文件超过 128 KiB 上限。");
        AtomicFile.Write(path, stream => stream.Write(bytes));
    }

    public static SongStyle Read(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > MaxBytes)
            throw new InvalidDataException("歌曲样式文件超过 128 KiB 上限。");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            RequireFields(root, "formatVersion", "id", "name", "referenceAspectRatio", "layers");
            if (root.GetProperty("formatVersion").GetInt32() != CurrentVersion)
                throw new InvalidDataException("歌曲样式版本不受支持；本编辑器读取版本 1。");
            var layerElements = root.GetProperty("layers");
            if (layerElements.ValueKind != JsonValueKind.Array ||
                layerElements.GetArrayLength() is < 5 or > 12)
                throw new InvalidDataException("歌曲样式必须包含五个核心角色，并可选一个底板。");
            var layers = layerElements.EnumerateArray().Select(ReadLayer).ToArray();
            var style = new SongStyle(
                root.GetProperty("id").GetString() ?? "",
                root.GetProperty("name").GetString() ?? "",
                root.GetProperty("referenceAspectRatio").GetDouble(),
                Array.AsReadOnly(layers));
            style.Validate();
            return style;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            FormatException or OverflowException or InvalidDataException)
        {
            throw new InvalidDataException("歌曲样式无效：" + error.Message, error);
        }
    }

    private static SongStyleLayer ReadLayer(JsonElement layer)
    {
        RequireFields(layer, "role", "kind", "x", "y", "width", "height", "visible", "locked",
            "keepAspect", "slanted", "opacity", "imageFit", "rotationDegrees", "fontSizeRatio",
            "fontFamily", "bold", "italic", "lineSpacing", "alignment", "verticalAlignment", "colorArgb");
        var imageFitElement = layer.GetProperty("imageFit");
        ImageFitMode? imageFit = imageFitElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when Enum.TryParse<ImageFitMode>(
                imageFitElement.GetString(), ignoreCase: false, out var fit) &&
                Enum.IsDefined(fit) => fit,
            _ => throw new InvalidDataException("歌曲样式图片适配方式无效。")
        };
        return new SongStyleLayer(
            ParseEnum<SongRole>(layer, "role"),
            ParseEnum<LayerKind>(layer, "kind"),
            layer.GetProperty("x").GetDouble(),
            layer.GetProperty("y").GetDouble(),
            layer.GetProperty("width").GetDouble(),
            layer.GetProperty("height").GetDouble(),
            layer.GetProperty("visible").GetBoolean(),
            layer.GetProperty("locked").GetBoolean(),
            layer.GetProperty("keepAspect").GetBoolean(),
            layer.GetProperty("slanted").GetBoolean(),
            layer.GetProperty("opacity").GetDouble(),
            imageFit,
            layer.GetProperty("rotationDegrees").GetDouble(),
            layer.GetProperty("fontSizeRatio").GetDouble(),
            layer.GetProperty("fontFamily").GetString() ?? "",
            layer.GetProperty("bold").GetBoolean(),
            layer.GetProperty("italic").GetBoolean(),
            layer.GetProperty("lineSpacing").GetDouble(),
            ParseEnum<HorizontalAlignment>(layer, "alignment"),
            ParseEnum<VerticalAlignment>(layer, "verticalAlignment"),
            layer.GetProperty("colorArgb").GetUInt32());
    }

    private static T ParseEnum<T>(JsonElement element, string property) where T : struct, Enum
    {
        var text = element.GetProperty(property).GetString();
        if (!Enum.TryParse<T>(text, ignoreCase: false, out var value) || !Enum.IsDefined(value))
            throw new InvalidDataException($"歌曲样式字段 {property} 无效。");
        return value;
    }

    private static void RequireFields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("歌曲样式应为 JSON 对象。");
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != fields.Length ||
            !names.ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new InvalidDataException("歌曲样式字段缺失、重复或未知。");
    }
}
