using System.Text.Json;
using Editor.Core;

namespace Editor.Formats;

/// <summary>Versioned layout-only files; never serializes document content or assets.</summary>
public static class LayoutTemplateStore
{
    public const string Extension = ".ppglayout";
    public const int MaxBytes = 65536;

    public static void Save(string path, LayoutTemplate template)
    {
        template.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 1, id = template.Id, name = template.Name,
            slots = template.Slots.Select(slot => new { x = slot.X, y = slot.Y, width = slot.Width, height = slot.Height })
        }, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Layout template exceeds 64 KiB.");
        AtomicFile.Write(path, stream => stream.Write(bytes));
    }

    public static LayoutTemplate Read(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > MaxBytes) throw new InvalidDataException("Layout template exceeds 64 KiB.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            RequireFields(root, "formatVersion", "id", "name", "slots");
            if (root.GetProperty("formatVersion").GetInt32() != 1)
                throw new InvalidDataException("Unsupported layout template version; this editor reads version 1.");
            var slots = root.GetProperty("slots");
            if (slots.ValueKind != JsonValueKind.Array || slots.GetArrayLength() is < 1 or > 200)
                throw new InvalidDataException("A layout template needs between 1 and 200 slots.");
            var boxes = slots.EnumerateArray().Select(slot =>
            {
                RequireFields(slot, "x", "y", "width", "height");
                return new RectD(slot.GetProperty("x").GetDouble(), slot.GetProperty("y").GetDouble(),
                    slot.GetProperty("width").GetDouble(), slot.GetProperty("height").GetDouble());
            }).ToArray();
            var result = new LayoutTemplate(root.GetProperty("id").GetString()!,
                root.GetProperty("name").GetString()!, Array.AsReadOnly(boxes));
            result.Validate();
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid layout template: " + error.Message, error);
        }
    }

    private static void RequireFields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected a template object.");
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != fields.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new InvalidDataException("Missing, duplicate or unknown template fields.");
    }
}
