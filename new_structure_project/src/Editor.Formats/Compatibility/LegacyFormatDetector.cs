using System.Text.Json;
using System.Xml;

namespace Editor.Formats.Compatibility;

/// <summary>Encoded-document limits checked before whole-document allocation.</summary>
public static class CompatibilityInputLimits
{
    public const long ClassicBytes = 160L * 1024 * 1024;
    public const long FreeBytes = 300L * 1024 * 1024;
    public const long TemporaryJsonBytes = 100L * 1024 * 1024;

    public static void Check(long length, LegacyFormatKind format = LegacyFormatKind.Unknown)
    {
        var limit = format switch
        {
            LegacyFormatKind.ClassicPreview => ClassicBytes,
            LegacyFormatKind.TemporaryJson => TemporaryJsonBytes,
            _ => FreeBytes
        };
        if (length < 0 || length > limit)
            throw new CompatibilityException(CompatibilityErrorKind.TooLarge,
                $"Legacy project exceeds supported size limit ({limit / (1024 * 1024)} MiB for {format}).");
    }
}

/// <summary>Content routing only; recognised native packages never fall back to legacy.</summary>
public static class LegacyFormatDetector
{
    public static LegacyFormatKind Detect(ReadOnlySpan<byte> content)
    {
        CompatibilityInputLimits.Check(content.Length);
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        return DetectStream(stream);
    }

    public static LegacyFormatKind DetectFile(string path)
    {
        using var stream = File.OpenRead(path);
        return DetectStream(stream);
    }

    internal static LegacyFormatKind DetectStream(Stream stream)
    {
        CompatibilityInputLimits.Check(stream.Length);
        stream.Position = 0;
        Span<byte> signature = stackalloc byte[4];
        var count = stream.Read(signature);
        stream.Position = 0;
        if (count == 4 && signature[0] == 0x50 && signature[1] == 0x4b &&
            (signature[2], signature[3]) is (0x03, 0x04) or (0x05, 0x06) or (0x07, 0x08))
            return LegacyFormatKind.Native;

        // Bounded stream scanning handles whitespace/BOMs without a large prefix allocation.
        char head;
        using (var text = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            int value;
            do { value = text.Read(); } while (value >= 0 && char.IsWhiteSpace((char)value));
            head = value < 0 ? '\0' : (char)value;
        }
        stream.Position = 0;
        if (head is '{' or '[')
        {
            CompatibilityInputLimits.Check(stream.Length, LegacyFormatKind.TemporaryJson);
            try
            {
                using var text = new StreamReader(stream, leaveOpen: true);
                using var json = JsonDocument.Parse(text.ReadToEnd());
                var root = json.RootElement;
                return root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("FormatVersion", out var version) &&
                    version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out _) &&
                    root.TryGetProperty("Document", out var document) && document.ValueKind == JsonValueKind.Object
                    ? LegacyFormatKind.TemporaryJson : LegacyFormatKind.Unknown;
            }
            catch (JsonException) { return LegacyFormatKind.Unknown; }
        }
        if (head != '<') return LegacyFormatKind.Unknown;
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = CompatibilityInputLimits.FreeBytes, CloseInput = false
            });
            reader.MoveToContent();
            var format = reader.Name switch
            {
                "Project" => LegacyFormatKind.ClassicPreview,
                "FreeProject" => LegacyFormatKind.FreeLayout,
                _ => LegacyFormatKind.Unknown
            };
            CompatibilityInputLimits.Check(stream.Length, format);
            return format;
        }
        catch (XmlException) { return LegacyFormatKind.Unknown; }
    }
}
