using System.Text.Json;
using System.Text.Json.Serialization;
using Editor.Core;

namespace Editor.Formats;

/// <summary>Disposable P1 JSON schema. No public extension or long-term compatibility promise.</summary>
public static class NativeDocumentStore
{
    public const int CurrentVersion = 8;
    private sealed record Envelope(int FormatVersion, EditorDocument Document);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new AssetBlobJsonConverter() }
    };

    public static void Save(string path, EditorDocument document)
    {
        document.Validate();
        var copy = document.Copy();
        foreach (var layer in copy.Layers) layer.ImageFit = layer.EffectiveImageFit;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(CurrentVersion, copy), JsonOptions);
        CheckSize(bytes.LongLength);
        WriteBytesAtomically(path, bytes);
    }

    public static void WriteBytesAtomically(string path, byte[] bytes)
    {
        AtomicFile.Write(path, stream => stream.Write(bytes));
    }

    public static EditorDocument Read(string path)
    {
        var file = new FileInfo(path);
        CheckSize(file.Length);
        var bytes = File.ReadAllBytes(path);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Empty P1 document.");
        if (envelope.FormatVersion is < 1 or > CurrentVersion)
            throw new InvalidDataException($"Unsupported P1 temporary schema version {envelope.FormatVersion}.");
        if (envelope.Document is null)
            throw new InvalidDataException("P1 document payload is missing.");
        if (envelope.FormatVersion < 5 && envelope.Document.Layers?.Any(layer => layer?.RotationDegrees is not (null or 0)) == true)
            throw new InvalidDataException("Temporary schema versions 1–4 cannot contain rotation.");
        if (envelope.FormatVersion < 8 && HasV8BackgroundFields(root))
            throw new InvalidDataException("Temporary schema versions 1–7 cannot contain version 8 background contribution fields.");
        if (envelope.FormatVersion >= 8 && !HasAllV8BackgroundFields(root))
            throw new InvalidDataException("Temporary schema v8 requires background contribution fields.");
        var hasSongRoles = envelope.Document.Layers?.Any(layer =>
            layer is not null && layer.SongRole != SongRole.None) == true;
        if (envelope.FormatVersion < 7 && hasSongRoles)
            throw new InvalidDataException("Temporary schema versions 1–6 cannot contain song roles.");
        if (envelope.FormatVersion < 4 && envelope.Document.BackgroundBlend is { } blend)
            envelope.Document.BackgroundBlend = blend with { IncludeFlatImages = true };
        if (envelope.FormatVersion < 8)
            envelope.Document.BackgroundBlend =
                (envelope.Document.BackgroundBlend ?? new BackgroundBlendSettings()) with
                {
                    ContributionMode = BackgroundContributionMode.Automatic,
                    SelectedArtworkLayerIds = null, Range = 1, Transition = 0.5
                };
        if (envelope.Document.Layers is not null)
            foreach (var layer in envelope.Document.Layers)
                if (layer is not null)
                {
                    if (envelope.FormatVersion < 7) layer.SongRole = SongRole.None;
                    layer.ImageFit = layer.EffectiveImageFit;
                }
        envelope.Document.Validate();
        return envelope.Document;
    }

    private static bool HasV8BackgroundFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Document", out var document) ||
            document.ValueKind != JsonValueKind.Object ||
            !document.TryGetProperty("BackgroundBlend", out var blend) ||
            blend.ValueKind != JsonValueKind.Object)
            return false;
        return blend.TryGetProperty("ContributionMode", out _) ||
            blend.TryGetProperty("SelectedArtworkLayerIds", out _) ||
            blend.TryGetProperty("Range", out _) ||
            blend.TryGetProperty("Transition", out _);
    }

    private static bool HasAllV8BackgroundFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Document", out var document) ||
            document.ValueKind != JsonValueKind.Object ||
            !document.TryGetProperty("BackgroundBlend", out var blend) ||
            blend.ValueKind != JsonValueKind.Object)
            return false;
        return blend.TryGetProperty("ContributionMode", out _) &&
            blend.TryGetProperty("SelectedArtworkLayerIds", out _) &&
            blend.TryGetProperty("Range", out _) &&
            blend.TryGetProperty("Transition", out _);
    }

    private static void CheckSize(long length)
    {
        if (length > Compatibility.CompatibilityInputLimits.TemporaryJsonBytes)
            throw new InvalidDataException("P1 document exceeds the 100 MiB size limit.");
    }

    private sealed class AssetBlobJsonConverter : JsonConverter<AssetBlob>
    {
        public override AssetBlob? Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options) => new(reader.GetBytesFromBase64());

        public override void Write(Utf8JsonWriter writer, AssetBlob value,
            JsonSerializerOptions options) => writer.WriteBase64StringValue(value.ToArray());
    }
}
