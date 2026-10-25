using System.IO.Compression;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Editor.Core;

namespace Editor.Formats;

/// <summary>
/// Native container v8 with in-memory v1–v7 migration. The public extension is .ppgproj.
/// P1 temporary JSON remains readable through NativeDocumentStore.
/// </summary>
public static class NativePackageStore
{
    public const string FormatId = "phigros-preview-editor-native";
    public const int SchemaVersion = 8;
    private const long MaxFileBytes = 300L * 1024 * 1024;
    private const long MaxManifestBytes = 4L * 1024 * 1024;
    private const long MaxAssetBytes = 100L * 1024 * 1024;
    private const long MaxExpandedBytes = 300L * 1024 * 1024;
    private const string ManifestName = "project.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static void Save(string path, EditorDocument document, string applicationVersion)
    {
        document.Validate();
        if (string.IsNullOrWhiteSpace(applicationVersion) || applicationVersion.Length > 128)
            throw new InvalidDataException("An application version is required for the native package.");
        var unique = new SortedDictionary<string, AssetBlob>(StringComparer.Ordinal);
        var assets = new List<AssetRecord>();
        foreach (var (id, asset) in document.Assets.OrderBy(pair => pair.Key))
        {
            if (id == Guid.Empty || asset is null || asset.Length == 0 || asset.Length > MaxAssetBytes)
                throw new InvalidDataException($"Invalid native asset {id}.");
            var hash = asset.Sha256;
            if (unique.TryGetValue(hash, out var existing) && !existing.ContentEquals(asset))
                throw new InvalidDataException("Native asset hash collision.");
            unique[hash] = asset;
            assets.Add(new AssetRecord { Id = id, Sha256 = hash });
        }
        var expandedAssetBytes = unique.Values.Sum(asset => (long)asset.Length);
        if (expandedAssetBytes > MaxExpandedBytes)
            throw new InvalidDataException("Native assets exceed the expanded size limit.");

        var manifest = new Manifest
        {
            FormatId = FormatId,
            SchemaVersion = SchemaVersion,
            ApplicationVersion = applicationVersion,
            CanvasWidth = document.CanvasWidth,
            CanvasHeight = document.CanvasHeight,
            BackgroundArgb = document.BackgroundArgb,
            BackgroundBlend = document.BackgroundBlend,
            FixedBackgroundAssetId = document.FixedBackgroundAssetId,
            Layers = document.Layers.Select(LayerRecord.FromLayer).ToList(),
            Assets = assets,
            ExtensionData = document.ExtensionData.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (json.LongLength > MaxManifestBytes)
            throw new InvalidDataException("Native manifest exceeds the size limit.");
        if (expandedAssetBytes + json.LongLength > MaxExpandedBytes)
            throw new InvalidDataException("Native package exceeds the expanded size limit.");

        AtomicFile.Write(path, stream =>
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var entry = CreateEntry(archive, ManifestName).Open())
                    entry.Write(json);
                foreach (var (hash, asset) in unique)
                {
                    using var entry = CreateEntry(archive, AssetName(hash)).Open();
                    asset.WriteTo(entry);
                }
            }
            if (stream.Length > MaxFileBytes)
                throw new InvalidDataException("Native package exceeds the file size limit.");
        });
    }

    public static EditorDocument Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
            throw new InvalidDataException("Native package exceeds the file size limit.");
        using var file = info.OpenRead();
        CheckCentralDirectory(file);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > 201)
            throw new InvalidDataException("Native package has an invalid entry count.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(entry.FullName) ||
                (entry.FullName != ManifestName && !IsAssetName(entry.FullName)))
                throw new InvalidDataException($"Unexpected or duplicate native package entry: {entry.FullName}");
            if (entry.Length < 0 || entry.Length > (entry.FullName == ManifestName ? MaxManifestBytes : MaxAssetBytes))
                throw new InvalidDataException($"Native package entry is too large: {entry.FullName}");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes)
                throw new InvalidDataException("Native package exceeds the expanded size limit.");
        }

        var manifestEntry = archive.GetEntry(ManifestName)
            ?? throw new InvalidDataException("Native package is missing project.json.");
        var json = ReadBounded(manifestEntry, MaxManifestBytes);
        Manifest manifest;
        var hasBackgroundBlend = false;
        var hasIncludeFlatImages = false;
        var hasContributionMode = false;
        var hasSelectedArtworkLayerIds = false;
        var hasRange = false;
        var hasTransition = false;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            RejectDuplicateProperties(parsed.RootElement);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object &&
                parsed.RootElement.TryGetProperty("backgroundBlend", out var blend))
            {
                hasBackgroundBlend = true;
                hasIncludeFlatImages = blend.ValueKind == JsonValueKind.Object &&
                    blend.TryGetProperty("includeFlatImages", out _);
                hasContributionMode = blend.ValueKind == JsonValueKind.Object &&
                    blend.TryGetProperty("contributionMode", out _);
                hasSelectedArtworkLayerIds = blend.ValueKind == JsonValueKind.Object &&
                    blend.TryGetProperty("selectedArtworkLayerIds", out _);
                hasRange = blend.ValueKind == JsonValueKind.Object &&
                    blend.TryGetProperty("range", out _);
                hasTransition = blend.ValueKind == JsonValueKind.Object &&
                    blend.TryGetProperty("transition", out _);
            }
            if (parsed.RootElement.ValueKind == JsonValueKind.Object &&
                parsed.RootElement.TryGetProperty("schemaVersion", out var version) &&
                version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number > SchemaVersion)
                throw new InvalidDataException("Native package was created by a newer schema version.");
            manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions)
                ?? throw new InvalidDataException("Native manifest is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Native manifest has invalid or unknown fields.", error);
        }

        if (manifest.FormatId != FormatId)
            throw new InvalidDataException("Unknown native package format identifier.");
        if (manifest.SchemaVersion > SchemaVersion)
            throw new InvalidDataException("Native package was created by a newer schema version.");
        if (manifest.SchemaVersion is < 1 or > SchemaVersion)
            throw new InvalidDataException($"Unsupported native schema version {manifest.SchemaVersion}.");
        if (manifest.SchemaVersion == 1 && hasBackgroundBlend)
            throw new InvalidDataException("Native schema version 1 cannot contain version 2 background settings.");
        if (manifest.SchemaVersion >= 2 && manifest.BackgroundBlend is null)
            throw new InvalidDataException("Native schema version 2 and later requires background settings.");
        if (manifest.SchemaVersion < 4 && hasIncludeFlatImages)
            throw new InvalidDataException("Native schema versions 1–3 cannot contain version 4 background policy.");
        if (manifest.SchemaVersion >= 4 && !hasIncludeFlatImages)
            throw new InvalidDataException("Native schema v4 requires a background contribution policy.");
        if (string.IsNullOrWhiteSpace(manifest.ApplicationVersion) || manifest.ApplicationVersion.Length > 128 ||
            manifest.Layers is null || manifest.Assets is null || manifest.Assets.Count > 200 ||
            manifest.ExtensionData is null)
            throw new InvalidDataException("Native manifest is missing required data.");
        if (manifest.SchemaVersion < 3 && manifest.Layers.Any(layer => layer?.VerticalAlignment is not null))
            throw new InvalidDataException("Native schema versions 1–2 cannot contain vertical alignment.");
        if (manifest.SchemaVersion >= 3 && manifest.Layers.Any(layer => layer?.VerticalAlignment is null))
            throw new InvalidDataException("Native schema v3+ requires vertical alignment for every layer.");
        if (manifest.SchemaVersion < 4 && manifest.Layers.Any(layer => layer?.Slanted is not null))
            throw new InvalidDataException("Native schema versions 1–3 cannot contain slanted layer geometry.");
        if (manifest.SchemaVersion >= 4 && manifest.Layers.Any(layer => layer?.Slanted is null))
            throw new InvalidDataException("Native schema v4 requires slanted state for every layer.");
        if (manifest.SchemaVersion < 5 && manifest.Layers.Any(layer => layer?.RotationDegrees is not (null or 0)))
            throw new InvalidDataException("Native schema versions 1–4 cannot contain rotation.");
        if (manifest.SchemaVersion < 6 && manifest.Layers.Any(layer =>
            layer?.Opacity is not null || layer?.ImageFit is not null || layer?.Crop is not null))
            throw new InvalidDataException("Native schema versions 1–5 cannot contain opacity or image crop fields.");
        if (manifest.SchemaVersion >= 6 && manifest.Layers.Any(layer => layer?.Opacity is null))
            throw new InvalidDataException("Native schema v6 requires opacity for every layer.");
        if (manifest.SchemaVersion >= 6 && manifest.Layers.Any(layer =>
            layer is not null && layer.Kind == LayerKind.Image && (layer.ImageFit is null || layer.Crop is null)))
            throw new InvalidDataException("Native schema v6 requires image fit and crop for every image layer.");
        if (manifest.SchemaVersion >= 6 && manifest.Layers.Any(layer =>
            layer is not null && layer.Kind != LayerKind.Image && (layer.ImageFit is not null || layer.Crop is not null)))
            throw new InvalidDataException("Native schema v6 image fit/crop fields are only valid on image layers.");
        if (manifest.SchemaVersion < 7 && manifest.Layers.Any(layer => layer?.SongRole is not null))
            throw new InvalidDataException("Native schema versions 1–6 cannot contain song roles.");
        if (manifest.SchemaVersion < 8 && (hasContributionMode || hasSelectedArtworkLayerIds ||
            hasRange || hasTransition))
            throw new InvalidDataException("Native schema versions 1–7 cannot contain version 8 background contribution fields.");
        if (manifest.SchemaVersion >= 8 && (!hasContributionMode || !hasSelectedArtworkLayerIds ||
            !hasRange || !hasTransition))
            throw new InvalidDataException("Native schema v8 requires background contribution fields.");

        var expected = new HashSet<string>(StringComparer.Ordinal) { ManifestName };
        var decodedAssets = new Dictionary<string, AssetBlob>(StringComparer.Ordinal);
        var document = new EditorDocument
        {
            CanvasWidth = manifest.CanvasWidth,
            CanvasHeight = manifest.CanvasHeight,
            BackgroundArgb = manifest.BackgroundArgb,
            BackgroundBlend = manifest.SchemaVersion < 4
                ? (manifest.BackgroundBlend ?? new BackgroundBlendSettings()) with
                    { IncludeFlatImages = true, ContributionMode = BackgroundContributionMode.Automatic,
                        SelectedArtworkLayerIds = null, Range = 1, Transition = 0.5 }
                : manifest.SchemaVersion < 8
                    ? (manifest.BackgroundBlend ?? new BackgroundBlendSettings()) with
                        { ContributionMode = BackgroundContributionMode.Automatic,
                            SelectedArtworkLayerIds = null, Range = 1, Transition = 0.5 }
                    : manifest.BackgroundBlend!,
            FixedBackgroundAssetId = manifest.FixedBackgroundAssetId,
            ExtensionData = new Dictionary<string, string>(manifest.ExtensionData, StringComparer.Ordinal),
            Layers = manifest.Layers.Select(layer => layer?.ToLayer()
                ?? throw new InvalidDataException("Native manifest contains a null layer.")).ToList()
        };
        foreach (var asset in manifest.Assets)
        {
            if (asset is null)
                throw new InvalidDataException("Native manifest contains a null asset index entry.");
            if (asset.Id == Guid.Empty || !IsHash(asset.Sha256) || document.Assets.ContainsKey(asset.Id))
                throw new InvalidDataException("Native asset index has an invalid or duplicate ID/hash.");
            var name = AssetName(asset.Sha256);
            expected.Add(name);
            var entry = archive.GetEntry(name)
                ?? throw new InvalidDataException($"Native package is missing asset {asset.Id}.");
            if (!decodedAssets.TryGetValue(asset.Sha256, out var blob))
            {
                blob = new AssetBlob(ReadBounded(entry, MaxAssetBytes));
                if (blob.Sha256 != asset.Sha256)
                    throw new InvalidDataException($"Native asset {asset.Id} failed SHA-256 verification.");
                decodedAssets.Add(asset.Sha256, blob);
            }
            document.Assets.Add(asset.Id, blob);
        }
        if (!names.SetEquals(expected))
            throw new InvalidDataException("Native package has an unreferenced asset entry.");
        document.Validate();
        return document;
    }

    private static string AssetName(string hash) => $"assets/{hash}.bin";

    private static ZipArchiveEntry CreateEntry(ZipArchive archive, string name)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return entry;
    }

    private static bool IsAssetName(string name) =>
        name.StartsWith("assets/", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.Ordinal) &&
        name.Length == "assets/".Length + 64 + ".bin".Length &&
        IsHash(name["assets/".Length..^".bin".Length]);

    private static bool IsHash(string? hash) => hash is { Length: 64 } &&
        hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] ReadBounded(ZipArchiveEntry entry, long limit)
    {
        using var input = entry.Open();
        if (entry.Length > limit || entry.Length > int.MaxValue)
            throw new InvalidDataException($"Native package entry exceeds its size limit: {entry.FullName}");
        var bytes = new byte[(int)entry.Length];
        try { input.ReadExactly(bytes); }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException($"Native package entry is truncated: {entry.FullName}", error);
        }
        if (input.ReadByte() != -1)
            throw new InvalidDataException($"Native package entry length changed: {entry.FullName}");
        return bytes;
    }

    private static void CheckCentralDirectory(FileStream file)
    {
        var tailLength = (int)Math.Min(file.Length, 65_557); // EOCD plus maximum ZIP comment.
        var tail = new byte[tailLength];
        file.Position = file.Length - tailLength;
        file.ReadExactly(tail);
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) != 0x06054b50) continue;
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20, 2));
            if (i + 22 + commentLength != tail.Length) continue;
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 4, 2));
            var centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 6, 2));
            var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 8, 2));
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10, 2));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12, 4));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16, 4));
            if (disk != 0 || centralDisk != 0 || entries != diskEntries || entries is < 1 or > 201 ||
                size > 8 * 1024 * 1024 || (long)offset + size > file.Length - tailLength + i)
                throw new InvalidDataException("Native package central directory is invalid or exceeds limits.");
            file.Position = 0;
            return;
        }
        throw new InvalidDataException("Native package has no supported ZIP central directory.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Duplicate native manifest field: {property.Name}");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) RejectDuplicateProperties(value);
    }

    private sealed class Manifest
    {
        public required string FormatId { get; init; }
        public required int SchemaVersion { get; init; }
        public required string ApplicationVersion { get; init; }
        public required int CanvasWidth { get; init; }
        public required int CanvasHeight { get; init; }
        public required uint BackgroundArgb { get; init; }
        public BackgroundBlendSettings? BackgroundBlend { get; init; }
        public Guid? FixedBackgroundAssetId { get; init; }
        public required List<LayerRecord> Layers { get; init; }
        public required List<AssetRecord> Assets { get; init; }
        public required Dictionary<string, string> ExtensionData { get; init; }
    }

    private sealed class AssetRecord
    {
        public required Guid Id { get; init; }
        public required string Sha256 { get; init; }
    }

    private sealed class LayerRecord
    {
        public required Guid Id { get; init; }
        public required LayerKind Kind { get; init; }
        public required RectD Bounds { get; init; }
        public string Name { get; init; } = "Layer";
        public bool Visible { get; init; } = true;
        public bool Locked { get; init; }
        public Guid? GroupId { get; init; }
        public bool KeepAspect { get; init; }
        public bool? Slanted { get; init; }
        public double? RotationDegrees { get; init; }
        public double? Opacity { get; init; }
        public ImageFitMode? ImageFit { get; init; }
        public RectD? Crop { get; init; }
        public SongRole? SongRole { get; init; }
        public string Text { get; init; } = "";
        public string FontFamily { get; init; } = "Microsoft YaHei UI";
        public double FontSize { get; init; } = 36;
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public double LineSpacing { get; init; } = 1;
        public HorizontalAlignment Alignment { get; init; }
        public VerticalAlignment? VerticalAlignment { get; init; }
        public uint ColorArgb { get; init; } = 0xFFFFFFFF;
        public string Difficulty { get; init; } = "IN";
        public int Level { get; init; } = 15;
        public Guid? AssetId { get; init; }

        public static LayerRecord FromLayer(Layer layer) => new()
        {
            Id = layer.Id, Kind = layer.Kind, Bounds = layer.Bounds, Name = layer.Name,
            Visible = layer.Visible, Locked = layer.Locked, GroupId = layer.GroupId,
            KeepAspect = layer.KeepAspect, Slanted = layer.Slanted, Text = layer.Text,
            RotationDegrees = layer.RotationDegrees,
            Opacity = layer.Opacity,
            ImageFit = layer.Kind == LayerKind.Image ? layer.EffectiveImageFit : null,
            Crop = layer.Kind == LayerKind.Image ? layer.ImageCrop : null,
            SongRole = layer.SongRole == Editor.Core.SongRole.None ? null : layer.SongRole,
            FontFamily = layer.FontFamily, FontSize = layer.FontSize, Bold = layer.Bold,
            Italic = layer.Italic, LineSpacing = layer.LineSpacing, Alignment = layer.Alignment,
            VerticalAlignment = layer.VerticalAlignment,
            ColorArgb = layer.ColorArgb, Difficulty = layer.Difficulty, Level = layer.Level,
            AssetId = layer.AssetId
        };

        public Layer ToLayer() => new()
        {
            Id = Id, Kind = Kind, Bounds = Bounds, Name = Name, Visible = Visible,
            Locked = Locked, GroupId = GroupId, KeepAspect = KeepAspect,
            Slanted = Slanted ?? false, Text = Text,
            RotationDegrees = RotationDegrees ?? 0,
            Opacity = Opacity ?? 1,
            ImageFit = ImageFit,
            ImageCrop = Crop ?? new RectD(0, 0, 1, 1),
            SongRole = SongRole ?? Editor.Core.SongRole.None,
            FontFamily = FontFamily, FontSize = FontSize,
            Bold = Bold, Italic = Italic, LineSpacing = LineSpacing, Alignment = Alignment,
            VerticalAlignment = VerticalAlignment ?? Editor.Core.VerticalAlignment.Top,
            ColorArgb = ColorArgb, Difficulty = Difficulty, Level = Level, AssetId = AssetId
        };
    }
}
