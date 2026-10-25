using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Editor.Core;

namespace Editor.Formats.Compatibility;

// ---------------------------------------------------------------------------------------------
// Parsed legacy data (format layer only). Nothing here is a parallel domain model: these records
// exist to keep parsing separate from mapping and are dropped as soon as the canonical V2
// document has been produced.
// ---------------------------------------------------------------------------------------------

internal sealed record ClassicLabelDto(string Name, string Difficulty, int Level, string Author,
    bool ShowBadge, bool ShowAuthor, double BadgeX, double BadgeY, double AuthorX, double AuthorY,
    double Scale, double AuthorWidth);

internal sealed record ClassicProjectDto(int Version, string SourceName, bool NumberOnTop,
    byte[] Background, IReadOnlyList<ClassicLabelDto> Labels, IReadOnlyList<string> UnknownRootFields,
    IReadOnlyList<string> UnknownLabelFields);

internal sealed record FreeLayerDto(string Id, string Group, string Name, string Kind, double X,
    double Y, double Width, double Height, double FontSize, string FontName, bool Bold, bool Italic,
    double LineSpacing, string VerticalAlign, string Text, string Align, string Difficulty, int Level,
    uint ColorArgb, bool Visible, bool Locked, bool Slanted, bool KeepAspect, byte[] Image,
    IReadOnlyList<string> UnknownFields);

internal sealed record FreeProjectDto(int Version, int Width, int Height, uint BackgroundColor,
    bool SoftBackground, byte[] Background, IReadOnlyList<FreeLayerDto> Layers,
    IReadOnlyList<string> UnknownRootFields);

internal sealed record ParsedLegacy(LegacyFormatKind Format, int Version, ClassicProjectDto? Classic,
    FreeProjectDto? Free);

/// <summary>
/// XML parsing for the two V1 formats with the same defensive posture as the native reader:
/// DTD/external entities prohibited, bounded document size, bounded element counts, bounded string
/// lengths, invariant-culture numbers and explicit enums. No V2 semantics leak in here.
/// </summary>
internal static class LegacyDocumentParser
{
    private const long ClassicLimitBytes = CompatibilityInputLimits.ClassicBytes;
    private const long FreeLimitBytes = CompatibilityInputLimits.FreeBytes;
    private const int MaxLabels = 100;
    private const int MaxLayers = 200;
    private const int MaxNameLength = 200;
    private const int MaxAuthorLength = 500;
    private const int MaxTextLength = 4000;
    private const int MaxFontLength = 200;
    private const int MaxGroupLength = 128;

    private static readonly string[] Difficulties = ["EZ", "HD", "IN", "AT"];
    private static readonly string[] Kinds = ["Text", "Image", "Shape", "Badge", "Tag"];
    private static readonly string[] Alignments = ["Left", "Center", "Right"];
    private static readonly string[] VerticalAlignments = ["Top", "Center", "Bottom"];

    private static readonly HashSet<string> ClassicRoot =
        ["Version", "SourceName", "NumberOnTop", "Background", "Labels"];
    private static readonly HashSet<string> ClassicLabel =
        ["Name", "Difficulty", "Level", "Author", "ShowBadge", "ShowAuthor", "BadgeX", "BadgeY",
         "AuthorX", "AuthorY", "Scale", "AuthorWidth"];
    private static readonly HashSet<string> FreeRoot =
        ["Version", "Width", "Height", "BackgroundColor", "SoftBackground", "Background", "Layers"];
    private static readonly HashSet<string> FreeLayer =
        ["Id", "Group", "Name", "Kind", "X", "Y", "Width", "Height", "FontSize", "FontName", "Bold",
         "Italic", "LineSpacing", "VerticalAlign", "Text", "Align", "Difficulty", "Level",
         "ColorArgb", "Visible", "Locked", "Slanted", "KeepAspect", "Image"];

    public static ParsedLegacy Parse(byte[] content, LegacyFormatKind format)
    {
        var limit = format == LegacyFormatKind.ClassicPreview ? ClassicLimitBytes : FreeLimitBytes;
        if (content.LongLength > limit)
            throw new CompatibilityException(CompatibilityErrorKind.TooLarge,
                $"Legacy file exceeds the {limit / (1024 * 1024)} MB read limit.");

        XDocument xml;
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = limit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
            xml = XDocument.Load(reader);
        }
        catch (XmlException error)
        {
            throw new CompatibilityException(CompatibilityErrorKind.Malformed,
                "The legacy project is not well-formed XML.", error);
        }

        var root = xml.Root ?? throw new CompatibilityException(CompatibilityErrorKind.Malformed,
            "The legacy project has no root element.");
        return root.Name.LocalName switch
        {
            "Project" => new ParsedLegacy(LegacyFormatKind.ClassicPreview,
                Integer(root, "Version", 1, VersionPolicy), ReadClassic(root), null),
            "FreeProject" => new ParsedLegacy(LegacyFormatKind.FreeLayout,
                Integer(root, "Version", 1, VersionPolicy), null, ReadFree(root)),
            _ => throw new CompatibilityException(CompatibilityErrorKind.UnsupportedFeature,
                $"Unsupported legacy root element {root.Name.LocalName}.")
        };
    }

    private static ClassicProjectDto ReadClassic(XElement root)
    {
        var unknownRoot = Unknown(root, ClassicRoot);
        var unknownLabels = new HashSet<string>(StringComparer.Ordinal);
        var background = Bytes(root, "Background");
        if (background.Length == 0)
            throw new CompatibilityException(CompatibilityErrorKind.MissingAsset,
                "The classic project has no embedded background image.");
        var labels = new List<ClassicLabelDto>();
        var elements = root.Element("Labels")?.Elements("ChartLabel").ToList() ?? [];
        if (elements.Count > MaxLabels)
            throw new CompatibilityException(CompatibilityErrorKind.InvalidGeometry,
                $"The classic project has {elements.Count} labels; the limit is {MaxLabels}.");
        foreach (var element in elements)
        {
            foreach (var field in Unknown(element, ClassicLabel)) unknownLabels.Add(field);
            labels.Add(new ClassicLabelDto(
                Text(element, "Name", "曲目", MaxNameLength),
                Difficulty(element, "Difficulty", "IN"),
                Integer(element, "Level", 15, Positive),
                Text(element, "Author", "", MaxAuthorLength),
                Bool(element, "ShowBadge", true), Bool(element, "ShowAuthor", true),
                Number(element, "BadgeX", 1450, 0, 1920), Number(element, "BadgeY", 800, 0, 1080),
                Number(element, "AuthorX", 1000, 0, 1920), Number(element, "AuthorY", 900, 0, 1080),
                Number(element, "Scale", 1, 0.25, 3), Number(element, "AuthorWidth", 360, 80, 1600)));
        }
        return new ClassicProjectDto(0, Text(root, "SourceName", "预览", MaxNameLength),
            Bool(root, "NumberOnTop", true), background, labels, unknownRoot,
            unknownLabels.OrderBy(name => name, StringComparer.Ordinal).ToList());
    }

    private static FreeProjectDto ReadFree(XElement root)
    {
        var unknownRoot = Unknown(root, FreeRoot);
        // Root fields are read before the layer list, matching the V1 serialization order so the
        // first reported problem is the first field in the file.
        var canvasWidth = Integer(root, "Width", 2740, value => value is >= 320 and <= 7680);
        var canvasHeight = Integer(root, "Height", 1080, value => value is >= 180 and <= 4320);
        var background = Bytes(root, "Background");
        var backgroundColor = unchecked((uint)Integer(root, "BackgroundColor", -14468281, _ => true));
        var softBackground = Bool(root, "SoftBackground", true);
        var layers = new List<FreeLayerDto>();
        var elements = root.Element("Layers")?.Elements("FreeLayer").ToList() ?? [];
        if (elements.Count > MaxLayers)
            throw new CompatibilityException(CompatibilityErrorKind.InvalidGeometry,
                $"The free-layout project has {elements.Count} layers; the limit is {MaxLayers}.");
        var ids = new HashSet<Guid>();
        foreach (var element in elements)
        {
            var kind = Choice(element, "Kind", "Text", Kinds, $"Unsupported FreeLayer Kind {{0}}.");
            var width = Number(element, "Width", 400, 1, 20000);
            var height = Number(element, "Height", 70, kind == "Badge" ? 0.8 : 1, 20000);
            var layer = new FreeLayerDto(
                Text(element, "Id", "", 64), Text(element, "Group", "", MaxGroupLength),
                Text(element, "Name", "组件", MaxNameLength), kind,
                Number(element, "X", 0, -20000, 20000), Number(element, "Y", 0, -20000, 20000),
                width, height,
                Number(element, "FontSize", 40, 1, 1000),
                Text(element, "FontName", "Microsoft YaHei UI", MaxFontLength),
                Bool(element, "Bold", false), Bool(element, "Italic", false),
                Number(element, "LineSpacing", 1, 0.5, 4),
                Choice(element, "VerticalAlign", "Center", VerticalAlignments),
                Text(element, "Text", "文字", MaxTextLength),
                Choice(element, "Align", "Left", Alignments),
                Difficulty(element, "Difficulty", "IN"),
                Integer(element, "Level", 15, Positive),
                unchecked((uint)Integer(element, "ColorArgb", -1, _ => true)),
                Bool(element, "Visible", true), Bool(element, "Locked", false),
                Bool(element, "Slanted", false), Bool(element, "KeepAspect", true),
                Bytes(element, "Image"), Unknown(element, FreeLayer));
            if (!ids.Add(StableId(layer.Id)))
                throw new CompatibilityException(CompatibilityErrorKind.InvalidGeometry,
                    $"Duplicate free layer id {layer.Id}.");
            layers.Add(layer);
        }
        return new FreeProjectDto(0, canvasWidth, canvasHeight, backgroundColor, softBackground, background,
            layers, unknownRoot);
    }

    // --- value helpers -------------------------------------------------------------------------

    private static string Text(XElement parent, string name, string fallback, int maxLength)
    {
        var raw = parent.Element(name)?.Value;
        if (raw is null) return fallback;
        if (raw.Length > maxLength)
            throw new CompatibilityException(CompatibilityErrorKind.Malformed,
                $"Field {name} exceeds the {maxLength} character limit.");
        return raw;
    }

    private static double Number(XElement parent, string name, double fallback, double min, double max)
    {
        var raw = parent.Element(name)?.Value;
        var value = fallback;
        if (raw is not null)
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new CompatibilityException(CompatibilityErrorKind.Malformed,
                    $"Invalid numeric field {name}.");
            if (!double.IsFinite(value))
                throw new CompatibilityException(CompatibilityErrorKind.InvalidGeometry,
                    $"Field {name} is not a finite number.");
        }
        if (value < min || value > max)
            throw new CompatibilityException(CompatibilityErrorKind.InvalidGeometry,
                $"Field {name} value {value} is outside the supported range {min}..{max}.");
        return value;
    }

    private static int Integer(XElement parent, string name, int fallback, Func<int, bool> accept)
    {
        var raw = parent.Element(name)?.Value;
        var value = fallback;
        if (raw is not null)
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new CompatibilityException(CompatibilityErrorKind.Malformed,
                    $"Invalid integer field {name}.");
            if (!accept(value))
                throw new CompatibilityException(CompatibilityErrorKind.InvalidGeometry,
                    $"Field {name} value {value} is outside the supported range.");
        }
        return value;
    }

    private static bool Bool(XElement parent, string name, bool fallback)
    {
        var raw = parent.Element(name)?.Value;
        if (raw is null) return fallback;
        if (!bool.TryParse(raw, out var value))
            throw new CompatibilityException(CompatibilityErrorKind.Malformed,
                $"Invalid Boolean field {name}.");
        return value;
    }

    private static string Choice(XElement parent, string name, string fallback, string[] allowed,
        string? messageFormat = null)
    {
        var raw = parent.Element(name)?.Value;
        if (string.IsNullOrEmpty(raw)) return fallback;
        if (!allowed.Contains(raw, StringComparer.Ordinal))
            throw new CompatibilityException(CompatibilityErrorKind.UnsupportedFeature,
                messageFormat is null
                    ? $"Unsupported {name} value {raw}."
                    : string.Format(CultureInfo.InvariantCulture, messageFormat, raw));
        return raw;
    }

    private static string Difficulty(XElement parent, string name, string fallback)
    {
        var raw = parent.Element(name)?.Value;
        if (string.IsNullOrEmpty(raw)) return fallback;
        if (!Difficulties.Contains(raw, StringComparer.Ordinal))
            throw new CompatibilityException(CompatibilityErrorKind.UnsupportedFeature,
                $"Unsupported difficulty {raw}.");
        return raw;
    }

    private static byte[] Bytes(XElement parent, string name)
    {
        var raw = parent.Element(name)?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return Convert.FromBase64String(raw.Trim());
        }
        catch (FormatException error)
        {
            throw new CompatibilityException(CompatibilityErrorKind.Malformed,
                $"Field {name} is not valid Base64.", error);
        }
    }

    private static List<string> Unknown(XElement element, HashSet<string> known)
    {
        var result = new List<string>();
        void Attributes(XElement node, string prefix)
        {
            foreach (var attribute in node.Attributes().Where(a => !a.IsNamespaceDeclaration))
                result.Add(prefix + "@" + attribute.Name);
        }
        Attributes(element, "");
        foreach (var child in element.Elements())
        {
            var name = child.Name.ToString();
            if (!known.Contains(name))
            {
                result.Add(name);
                continue;
            }
            if (element.Elements(child.Name).Skip(1).Any())
                throw new CompatibilityException(CompatibilityErrorKind.Malformed,
                    $"Duplicate legacy field {name}.");
            Attributes(child, name + "/");
            var entry = name == "Layers" ? "FreeLayer" : name == "Labels" ? "ChartLabel" : null;
            foreach (var nested in child.Elements())
                if (entry is null || nested.Name.ToString() != entry)
                    result.Add(name + "/" + nested.Name);
        }
        return result;
    }

    private static bool Positive(int value) => value >= 1;

    private static bool VersionPolicy(int value) => value switch
    {
        < 1 => throw new CompatibilityException(CompatibilityErrorKind.UnsupportedVersion,
            $"Unsupported legacy project version {value}."),
        > 1 => throw new CompatibilityException(CompatibilityErrorKind.UnsupportedVersion,
            $"Unsupported legacy project version {value}. This file was created by a newer version."),
        _ => true
    };

    /// <summary>Stable identity for legacy string ids and group names (never an array index).</summary>
    internal static Guid StableId(string value)
    {
        if (Guid.TryParseExact(value, "N", out var parsed) && parsed != Guid.Empty) return parsed;
        if (Guid.TryParse(value, out parsed) && parsed != Guid.Empty) return parsed;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// Maps parsed legacy data onto the canonical V2 document. This is the only place where legacy
/// semantics are interpreted; the V2 core model and renderer stay untouched.
/// </summary>
internal static class LegacyCompatibilityMapper
{
    public static CompatibilityImport Map(ParsedLegacy parsed, CompatibilityOptions options, MigrationIds ids) =>
        parsed.Format switch
        {
            LegacyFormatKind.ClassicPreview => MapClassic(parsed.Classic!, options, ids),
            LegacyFormatKind.FreeLayout => MapFree(parsed.Free!, options, ids),
            _ => throw new CompatibilityException(CompatibilityErrorKind.UnsupportedFeature,
                $"{parsed.Format} is not a legacy project format.")
        };

    private static CompatibilityImport MapClassic(ClassicProjectDto source, CompatibilityOptions options, MigrationIds ids)
    {
        var findings = new List<ConversionDiagnostic>();
        var background = new AssetBlob(source.Background);
        var (backgroundWidth, backgroundHeight) = Probe(options, background, findings, "background");

        // The canonical canvas is the classic default export size, normalised into the native range
        // when the legacy bitmap sits outside it.
        var canvasWidth = 1920.0;
        var canvasHeight = 1080.0;
        if (backgroundWidth > 0 && backgroundHeight > 0)
        {
            canvasWidth = backgroundWidth;
            canvasHeight = backgroundHeight;
            var minFactor = Math.Max(320.0 / canvasWidth, 180.0 / canvasHeight);
            var maxFactor = Math.Min(7680.0 / canvasWidth, 4320.0 / canvasHeight);
            if (minFactor > maxFactor + 1e-9)
                throw new CompatibilityException(CompatibilityErrorKind.InvalidGeometry,
                    "The classic background aspect ratio cannot be represented by the native canvas range.");
            var factor = Math.Clamp(1.0, minFactor, maxFactor);
            if (Math.Abs(factor - 1) > 1e-9)
            {
                var original = $"{canvasWidth} x {canvasHeight}";
                canvasWidth = Math.Clamp(Math.Round(canvasWidth * factor), 320, 7680);
                canvasHeight = Math.Clamp(Math.Round(canvasHeight * factor), 180, 4320);
                findings.Add(new ConversionDiagnostic("LEGACY_CANVAS_RESOLUTION_NORMALIZED",
                    DiagnosticSeverity.Normalized,
                    $"Classic export size {original} is outside the native canvas range and was " +
                    $"normalised to {(int)canvasWidth} x {(int)canvasHeight}.", "document"));
            }
        }
        else
        {
            findings.Add(new ConversionDiagnostic("LEGACY_CLASSIC_BACKGROUND_SIZE_UNAVAILABLE",
                DiagnosticSeverity.Deviation,
                "The background bitmap size could not be probed, so the 1920 x 1080 reference canvas " +
                "was used and classic geometry is not rescaled to the background.", "document"));
        }

        var document = new EditorDocument
        {
            CanvasWidth = (int)canvasWidth,
            CanvasHeight = (int)canvasHeight,
            BackgroundArgb = 0xFF000000,
            FixedBackgroundAssetId = ids.For("background")
        };
        document.AddAsset(document.FixedBackgroundAssetId!.Value, background);
        if (ProbeOpacity(options, background, findings) == false)
            findings.Add(new ConversionDiagnostic("LEGACY_CLASSIC_BACKGROUND_ALPHA_DEVIATION",
                DiagnosticSeverity.Deviation,
                "The source rendering rules preserve transparency in the background export; the native renderer " +
                "composites the same image over the canvas colour. Alpha pixels can therefore differ.", "renderer"));

        if (source.UnknownRootFields.Count > 0)
            findings.Add(UnknownField("Project", source.UnknownRootFields));
        if (source.UnknownLabelFields.Count > 0)
            findings.Add(UnknownField("ChartLabel", source.UnknownLabelFields));
        if (source.NumberOnTop)
            findings.Add(new ConversionDiagnostic("LEGACY_CLASSIC_NUMBER_ON_TOP_NOT_REPRESENTED",
                DiagnosticSeverity.Loss,
                "NumberOnTop=true draws the level above the difficulty in the source layout; the native badge paints " +
                "difficulty above level.", "document"));

        var anyAuthorBar = false;
        var anyBadge = false;
        if (!string.IsNullOrEmpty(source.SourceName))
            findings.Add(new ConversionDiagnostic("LEGACY_SOURCE_NAME_NOT_REPRESENTED",
                DiagnosticSeverity.Loss, "The legacy SourceName is not stored in the native document.", "document"));
        var labelIndex = 0;
        foreach (var label in source.Labels)
        {
            var labelKey = "label/" + (labelIndex++).ToString(CultureInfo.InvariantCulture);
            if (!label.ShowBadge || !label.ShowAuthor)
                findings.Add(new ConversionDiagnostic("LEGACY_HIDDEN_LABEL_FIELDS_NOT_REPRESENTED",
                    DiagnosticSeverity.Loss,
                    $"Hidden badge/author fields for {label.Name} have no editable native layer; their stored properties are not carried.",
                    $"label:{label.Name}"));
            var geometry = new ClassicLabelGeometry(label.BadgeX, label.BadgeY, label.AuthorX,
                label.AuthorY, label.Scale, label.AuthorWidth);
            var placement = ClassicGeometry.Place(geometry, canvasWidth, canvasHeight);
            if (placement.Clamped)
                findings.Add(new ConversionDiagnostic("LEGACY_CLASSIC_GEOMETRY_CLAMPED",
                    DiagnosticSeverity.Normalized,
                    $"Label {label.Name} exceeded the classic canvas bounds and was clamped exactly as " +
                    "the source renderer clamps it before drawing.", $"label:{label.Name}"));
            if (label.ShowBadge)
            {
                anyBadge = true;
                var width = placement.Badge.Width;
                var height = placement.Badge.Height;
                if (width < 8)
                {
                    var scaleUp = 8 / width;
                    width = 8;
                    height *= scaleUp;
                    findings.Add(new ConversionDiagnostic("LEGACY_BADGE_BELOW_NATIVE_MINIMUM",
                        DiagnosticSeverity.Loss,
                        $"Badge {label.Name} is {placement.Badge.Width:0.###} px wide; the native model " +
                        "requires at least 8 px, so it was enlarged to the minimum.", $"label:{label.Name}"));
                }
                document.Layers.Add(new Layer
                {
                    Id = ids.For(labelKey + "/badge"), Kind = LayerKind.Badge, Name = label.Name, Difficulty = label.Difficulty,
                    Level = label.Level, KeepAspect = true,
                    Bounds = new RectD(placement.Badge.X, placement.Badge.Y, width, height)
                });
            }
            if (label.ShowAuthor && !string.IsNullOrWhiteSpace(label.Author))
            {
                anyAuthorBar = true;
                var plate = new Layer
                {
                    Id = ids.For(labelKey + "/plate"), Kind = LayerKind.Shape, Name = $"{label.Name} author bar", KeepAspect = false,
                    ColorArgb = 0xD20E1821, Bounds = placement.Author
                };
                document.Layers.Add(plate);
                document.Layers.Add(new Layer
                {
                    Id = ids.For(labelKey + "/text"), Kind = LayerKind.Text, Name = $"{label.Name} author", Text = label.Author,
                    FontSize = placement.AuthorFontSize,
                    Bounds = new RectD(placement.Author.X + 18 * label.Scale * placement.Scale,
                        placement.Author.Y, Math.Max(1, placement.Author.Width -
                        36 * label.Scale * placement.Scale), placement.Author.Height)
                });
            }
        }

        if (anyBadge)
            findings.Add(new ConversionDiagnostic("LEGACY_CLASSIC_BADGE_STYLE_DEVIATION",
                DiagnosticSeverity.Deviation,
                "The source renderer paints a slanted badge with a drop shadow and per-line fitted text; the native " +
                "badge paints an axis-aligned rectangle with its own text metrics.", "renderer"));
        if (anyAuthorBar)
            findings.Add(new ConversionDiagnostic("LEGACY_CLASSIC_AUTHOR_BAR_SLANT_DEVIATION",
                DiagnosticSeverity.Deviation,
                "The source author bar is a slanted plate with ellipsis-fitted centred text; the native " +
                "document keeps the same rectangle, colour and text but paints an axis-aligned plate.",
                "renderer"));
        if (document.Layers.Any(layer => layer.Kind == LayerKind.Text))
            findings.Add(TextMetricsDeviation());
        findings.Add(new ConversionDiagnostic("LEGACY_CLASSIC_REFERENCE_SPACE",
            DiagnosticSeverity.Info,
            $"Classic coordinates were mapped from the 1920 x 1080 reference space into a " +
            $"{document.CanvasWidth} x {document.CanvasHeight} canvas (isotropic scale " +
            $"{ClassicGeometry.FinalScale(document.CanvasHeight):0.#####}).", "document"));

        return Build(LegacyFormatKind.ClassicPreview, 1, document, findings,
            visualLayout: findings.Any(item => item.Severity is DiagnosticSeverity.Deviation
                or DiagnosticSeverity.Loss));
    }

    private static CompatibilityImport MapFree(FreeProjectDto source, CompatibilityOptions options, MigrationIds ids)
    {
        var findings = new List<ConversionDiagnostic>();
        var document = new EditorDocument
        {
            CanvasWidth = source.Width,
            CanvasHeight = source.Height,
            BackgroundArgb = source.BackgroundColor
        };
        var background = source.Background;
        if (background.Length > 0)
        {
            var blob = new AssetBlob(background);
            document.FixedBackgroundAssetId = ids.For("background");
            document.AddAsset(document.FixedBackgroundAssetId!.Value, blob);
            if (ProbeOpacity(options, blob, findings) == false)
                findings.Add(new ConversionDiagnostic("LEGACY_FREE_BACKGROUND_ALPHA_DEVIATION",
                    DiagnosticSeverity.Deviation,
                    "The fixed background contains transparent pixels. The source renderer composites them over its " +
                    "diagonal gradient; the native renderer composites them over the flat canvas colour. " +
                    "Centre cover geometry remains represented, but the underlying colours can differ.", "renderer"));
            var (width, height) = Probe(options, blob, findings, "background");
            if (width > 0 && height > 0 &&
                Math.Abs((double)width / height - (double)source.Width / source.Height) > 1e-3)
                findings.Add(new ConversionDiagnostic("LEGACY_FREE_BACKGROUND_CENTER_CROP",
                    DiagnosticSeverity.Info,
                    $"The source and native renderers both use a centre cover crop for the fixed background " +
                    $"({width} x {height} into {source.Width} x {source.Height}). This geometry mapping " +
                    "does not establish cross-renderer filtering or pixel equality.", "renderer"));
        }
        else
        {
            findings.Add(new ConversionDiagnostic("LEGACY_FREE_BACKGROUND_GRADIENT_DEVIATION",
                DiagnosticSeverity.Deviation,
                "With no background image the source renderer paints a diagonal gradient from BackgroundColor to " +
                "RGB(9,20,31); the native document keeps BackgroundColor as a flat fill.", "renderer"));
        }
        if (source.SoftBackground)
            findings.Add(new ConversionDiagnostic("LEGACY_FREE_SOFT_BACKGROUND_NOT_REPRESENTED",
                DiagnosticSeverity.Loss,
                "Source SoftBackground blur, shading and artwork composition are not migrated. Native " +
                "background composition is available, but this import leaves it disabled and retains " +
                "the unprocessed fixed background image or the flat BackgroundColor; source settings and " +
                "composed pixels are not reproduced.", "document"));

        if (source.UnknownRootFields.Count > 0)
            findings.Add(UnknownField("FreeProject", source.UnknownRootFields));
        var groups = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var normalizedGroups = 0;
        var normalizedIds = 0;
        var textDeviationReported = false;
        foreach (var layer in source.Layers)
        {
            var id = LegacyDocumentParser.StableId(layer.Id);
            if (!Guid.TryParse(layer.Id, out var legacyId) || legacyId == Guid.Empty) normalizedIds++;
            if (layer.UnknownFields.Count > 0)
                findings.Add(UnknownField("FreeLayer", layer.UnknownFields, id));
            Guid? groupId = null;
            if (!string.IsNullOrEmpty(layer.Group))
            {
                if (!groups.TryGetValue(layer.Group, out var mapped))
                {
                    mapped = LegacyDocumentParser.StableId(layer.Group);
                    groups[layer.Group] = mapped;
                    if (!Guid.TryParse(layer.Group, out var legacyGroup) || legacyGroup == Guid.Empty) normalizedGroups++;
                }
                groupId = mapped;
            }
            var bounds = new RectD(layer.X, layer.Y, layer.Width, layer.Height);
            switch (layer.Kind)
            {
                case "Badge":
                {
                    var fit = Math.Min(layer.Width / ClassicGeometry.BadgeWidth,
                        layer.Height / ClassicGeometry.BadgeHeight);
                    var width = ClassicGeometry.BadgeWidth * fit;
                    var height = ClassicGeometry.BadgeHeight * fit;
                    if (width < 8)
                    {
                        var scaleUp = 8 / width;
                        width = 8;
                        height *= scaleUp;
                        findings.Add(new ConversionDiagnostic("LEGACY_BADGE_BELOW_NATIVE_MINIMUM",
                            DiagnosticSeverity.Loss,
                            $"Badge {layer.Name} is {layer.Width:0.###} px wide; the native model " +
                            "requires at least 8 px, so it was enlarged to the minimum.", "layer", id));
                    }
                    document.Layers.Add(new Layer
                    {
                        Id = id, Kind = LayerKind.Badge, Name = layer.Name, GroupId = groupId,
                        Difficulty = layer.Difficulty, Level = layer.Level, KeepAspect = true,
                        Visible = layer.Visible, Locked = layer.Locked,
                        Bounds = new RectD(layer.X, layer.Y, width, height)
                    });
                    findings.Add(new ConversionDiagnostic("LEGACY_FREE_BADGE_ORDER_NOT_REPRESENTED",
                        DiagnosticSeverity.Loss,
                        "Source badges paint level above difficulty; the native badge reverses that order.", "layer", id));
                    findings.Add(new ConversionDiagnostic("LEGACY_CLASSIC_BADGE_STYLE_DEVIATION",
                        DiagnosticSeverity.Deviation,
                        "The source renderer paints the badge as a slanted plate with a " +
                        "shadow and fitted text metrics; the native badge paints a rectangle with its own " +
                        "metrics and the opposite level/difficulty order.", "renderer", id));
                    break;
                }
                case "Text":
                {
                    document.Layers.Add(new Layer
                    {
                        Id = id, Kind = LayerKind.Text, Name = layer.Name, GroupId = groupId,
                        Bounds = bounds, Visible = layer.Visible, Locked = layer.Locked,
                        KeepAspect = layer.KeepAspect, Text = layer.Text, FontFamily = layer.FontName,
                        FontSize = layer.FontSize, Bold = layer.Bold, Italic = layer.Italic,
                        LineSpacing = layer.LineSpacing,
                        Alignment = Enum.Parse<HorizontalAlignment>(layer.Align,
                            ignoreCase: false),
                        VerticalAlignment = Enum.Parse<VerticalAlignment>(layer.VerticalAlign,
                            ignoreCase: false),
                        ColorArgb = layer.ColorArgb
                    });
                    textDeviationReported |= !string.IsNullOrEmpty(layer.Text);
                    break;
                }
                case "Shape":
                {
                    document.Layers.Add(new Layer
                    {
                        Id = id, Kind = LayerKind.Shape, Name = layer.Name, GroupId = groupId,
                        Bounds = bounds, Visible = layer.Visible, Locked = layer.Locked,
                        KeepAspect = layer.KeepAspect, Slanted = layer.Slanted,
                        ColorArgb = layer.ColorArgb
                    });
                    break;
                }
                case "Image":
                {
                    if (layer.Image.Length == 0)
                    {
                        // V1 accepts an image layer without bytes and paints a placeholder gradient
                        // plus a caption (REQ-ASSET-001). The native model has no placeholder image,
                        // so it becomes a flat plate plus the same caption text.
                        var legacyCaptionFontSize = Math.Min(layer.Height / 6, 40);
                        var captionFontSize = Math.Max(1, legacyCaptionFontSize);
                        if (captionFontSize != legacyCaptionFontSize)
                            findings.Add(new ConversionDiagnostic("LEGACY_IMAGE_PLACEHOLDER_FONT_SIZE_NORMALIZED",
                                DiagnosticSeverity.Deviation,
                                $"Image layer {layer.Name} has a {layer.Height:0.###} px placeholder; its " +
                                $"caption font size {legacyCaptionFontSize:0.#####} was normalized in memory " +
                                "to the native minimum of 1. The source file is unchanged.", "layer", id));
                        document.Layers.Add(new Layer
                        {
                            Id = id, Kind = LayerKind.Shape, Name = layer.Name, GroupId = groupId,
                            Bounds = bounds, Visible = layer.Visible, Locked = layer.Locked,
                            KeepAspect = layer.KeepAspect, Slanted = layer.Slanted,
                            ColorArgb = 0xFF456374
                        });
                        document.Layers.Add(new Layer
                        {
                            Id = ids.For($"layer/{id:D}/placeholder-caption"), Kind = LayerKind.Text,
                            Name = $"{layer.Name} placeholder", GroupId = groupId,
                            Bounds = bounds, Visible = layer.Visible, Locked = layer.Locked,
                            Text = "导入曲绘", FontFamily = layer.FontName,
                            FontSize = captionFontSize,
                            Bold = layer.Bold, Italic = layer.Italic, LineSpacing = layer.LineSpacing,
                            KeepAspect = layer.KeepAspect,
                            Alignment = HorizontalAlignment.Center,
                            VerticalAlignment = Enum.Parse<VerticalAlignment>(layer.VerticalAlign,
                                ignoreCase: false), ColorArgb = layer.ColorArgb
                        });
                        findings.Add(new ConversionDiagnostic("LEGACY_IMAGE_PLACEHOLDER_NORMALIZED",
                            DiagnosticSeverity.Deviation,
                            $"Image layer {layer.Name} has no embedded bytes; the source renderer paints a gradient " +
                            "placeholder with a caption, which the native document represents as a flat " +
                            "plate plus the same caption text.", "layer", id));
                        break;
                    }
                    var blob = new AssetBlob(layer.Image);
                    var assetId = ids.For($"layer/{id:D}/asset/{blob.Sha256}");
                    document.AddAsset(assetId, blob);
                    document.Layers.Add(new Layer
                    {
                        Id = id, Kind = LayerKind.Image, Name = layer.Name, GroupId = groupId,
                        Bounds = bounds, Visible = layer.Visible, Locked = layer.Locked,
                        KeepAspect = layer.KeepAspect, Slanted = layer.Slanted,
                        AssetId = assetId
                    });
                    var (width, height) = Probe(options, blob, findings, $"layer {layer.Name}");
                    if (!layer.Slanted && width > 0 && height > 0 &&
                             Math.Abs((double)width / height - layer.Width / layer.Height) > 1e-3)
                        findings.Add(new ConversionDiagnostic("LEGACY_FREE_IMAGE_FIT_DEVIATION",
                            DiagnosticSeverity.Deviation,
                            $"Image {layer.Name} is {width} x {height} inside {layer.Width:0.###} x " +
                            $"{layer.Height:0.###}; the source renderer fits it with the aspect ratio preserved while " +
                            "the native painter fills the rectangle.", "renderer", id));
                    break;
                }
                case "Tag":
                {
                    // V1 always paints a white slanted plate and insets the caption by the same padding.
                    var padding = Math.Min(layer.Width * 0.2, layer.Height * 0.268 + 4);
                    document.Layers.Add(new Layer
                    {
                        Id = id, Kind = LayerKind.Shape, Name = layer.Name, GroupId = groupId,
                        Bounds = bounds, Visible = layer.Visible, Locked = layer.Locked,
                        KeepAspect = layer.KeepAspect, Slanted = true,
                        ColorArgb = 0xFFFFFFFF
                    });
                    var caption = new Layer
                    {
                        Id = ids.For($"layer/{id:D}/tag-caption"), Kind = LayerKind.Text, Name = $"{layer.Name} caption",
                        GroupId = groupId, Visible = layer.Visible, Locked = layer.Locked,
                        Bounds = new RectD(layer.X + padding, layer.Y,
                            Math.Max(1, layer.Width - 2 * padding), layer.Height),
                        Text = layer.Text, FontFamily = layer.FontName, FontSize = layer.FontSize,
                        Bold = layer.Bold, Italic = layer.Italic, LineSpacing = layer.LineSpacing,
                        Alignment = Enum.Parse<HorizontalAlignment>(layer.Align, ignoreCase: false),
                        VerticalAlignment = Enum.Parse<VerticalAlignment>(layer.VerticalAlign,
                            ignoreCase: false),
                        ColorArgb = layer.ColorArgb
                    };
                    document.Layers.Add(caption);
                    textDeviationReported |= !string.IsNullOrEmpty(layer.Text);
                    break;
                }
            }
        }
        if (normalizedIds > 0)
            findings.Add(new ConversionDiagnostic("LEGACY_LAYER_ID_NORMALIZED",
                DiagnosticSeverity.Normalized,
                $"{normalizedIds} legacy layer id(s) were not GUIDs and received stable derived ids.",
                "document"));
        if (normalizedGroups > 0)
            findings.Add(new ConversionDiagnostic("LEGACY_GROUP_ID_NORMALIZED",
                DiagnosticSeverity.Normalized,
                $"{normalizedGroups} legacy group name(s) were not GUIDs and received stable derived " +
                "ids; grouping itself stays a flat reference.", "document"));
        if (textDeviationReported) findings.Add(TextMetricsDeviation());

        return Build(LegacyFormatKind.FreeLayout, 1, document, findings,
            visualLayout: findings.Any(item => item.Severity is DiagnosticSeverity.Deviation
                or DiagnosticSeverity.Loss));
    }

    private static (int Width, int Height) Probe(CompatibilityOptions options, AssetBlob asset,
        List<ConversionDiagnostic> findings, string scope)
    {
        if (options.ProbeImageSize is null)
        {
            findings.Add(new ConversionDiagnostic("LEGACY_ASSET_SIZE_UNPROBED",
                DiagnosticSeverity.Deviation,
                $"The {scope} image size was not probed; its fit and dependent geometry cannot be verified.", scope));
            return (0, 0);
        }
        try
        {
            return options.ProbeImageSize(asset);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            findings.Add(new ConversionDiagnostic("LEGACY_ASSET_SIZE_UNPROBED",
                DiagnosticSeverity.Deviation,
                $"The {scope} image size could not be read ({error.Message}); geometry that depends " +
                "on it was not adjusted.", scope));
            return (0, 0);
        }
    }

    private static bool? ProbeOpacity(CompatibilityOptions options, AssetBlob asset,
        List<ConversionDiagnostic> findings)
    {
        try
        {
            if (options.ProbeImageOpacity is not null) return options.ProbeImageOpacity(asset);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            findings.Add(new ConversionDiagnostic("LEGACY_BACKGROUND_OPACITY_UNPROBED",
                DiagnosticSeverity.Deviation,
                $"Background opacity could not be verified ({error.Message}); transparent pixels " +
                "may expose a different underlying colour in the native renderer.", "renderer"));
            return null;
        }
        findings.Add(new ConversionDiagnostic("LEGACY_BACKGROUND_OPACITY_UNPROBED",
            DiagnosticSeverity.Deviation,
            "Background opacity was not probed; transparent pixels may expose a different underlying " +
            "colour in the native renderer. Image dimensions alone do not establish visual compatibility.", "renderer"));
        return null;
    }

    private static ConversionDiagnostic UnknownField(string scope, IReadOnlyList<string> fields,
        Guid? layerId = null) =>
        new("LEGACY_UNKNOWN_FIELD", DiagnosticSeverity.Loss,
            $"Unknown legacy field(s) {string.Join(", ", fields)} were read but are not carried into " +
            "the native document.", scope, layerId);

    private static ConversionDiagnostic TextMetricsDeviation() =>
        new("LEGACY_TEXT_METRICS_DEVIATION", DiagnosticSeverity.Deviation,
            "The source renderer lays text out with GDI+ (measured wrapping, ascent/descent line height, ellipsis); " +
            "the native renderer uses its own text layout. Text content, font, size, style, colour, " +
            "line spacing and alignment are preserved.", "renderer");

    private static CompatibilityImport Build(LegacyFormatKind format, int version,
        EditorDocument document, List<ConversionDiagnostic> findings, bool visualLayout)
    {
        if (document.Layers.Count > 200)
            throw new CompatibilityException(CompatibilityErrorKind.TooLarge,
                "Conversion expands project beyond native layer limit (200).");
        try { document.Validate(); }
        catch (InvalidDataException error)
        {
            throw new CompatibilityException(CompatibilityErrorKind.InvalidGeometry,
                $"Converted project violates native document limits: {error.Message}", error);
        }
        document.ExtensionData["compat.source.format"] = format == LegacyFormatKind.ClassicPreview
            ? "phipreview" : "phifree";
        document.ExtensionData["compat.source.version"] = version.ToString(CultureInfo.InvariantCulture);
        var level = findings.Any(item => item.Severity is DiagnosticSeverity.Loss or DiagnosticSeverity.Deviation)
            ? CompatibilityLevel.Lossy
            : findings.Any(item => item.Severity == DiagnosticSeverity.Normalized)
                ? CompatibilityLevel.Normalized
                : CompatibilityLevel.Exact;
        var dimensions = new List<CompatibilityDimension>
        {
            new(CompatibilityDimensionKind.DataReadable, DimensionStatus.Exact,
                "Every element the reader understands was parsed and validated."),
            new(CompatibilityDimensionKind.EditableSemantics,
                findings.Any(item => item.Severity == DiagnosticSeverity.Loss) ? DimensionStatus.Lossy : DimensionStatus.Exact,
                findings.Any(item => item.Severity == DiagnosticSeverity.Loss)
                    ? $"{findings.Count(item => item.Severity == DiagnosticSeverity.Loss)} legacy " +
                      "semantic(s) cannot be represented."
                    : "No known editable semantic loss was found; see normalization and visual findings."),
            new(CompatibilityDimensionKind.VisualLayout,
                visualLayout ? DimensionStatus.Lossy : DimensionStatus.Exact,
                visualLayout
                    ? "The registered conversion losses or renderer deviations can change the painted result; " +
                      "review the converted appearance before using the result."
                    : "No renderer deviation applies to this document."),
            new(CompatibilityDimensionKind.LegacyReopen, DimensionStatus.NotVerified,
                "Writing back to the source format is not supported; reopening the conversion " +
                "in the source program is not verified.")
        };
        return new CompatibilityImport(document,
            new CompatibilityReport(format, version, level, findings, dimensions));
    }
}

/// <summary>
/// Single entry point for legacy compatibility: detect the format by content, import it into the
/// canonical V2 document and return the structured report.
/// </summary>
public static class CompatibilityService
{
    public static CompatibilityImport Import(string path, CompatibilityOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] content;
        try
        {
            using var stream = File.OpenRead(path);
            var format = LegacyFormatDetector.DetectStream(stream);
            CompatibilityInputLimits.Check(stream.Length, format);
            if (format is not (LegacyFormatKind.ClassicPreview or LegacyFormatKind.FreeLayout))
                throw Unsupported(format);
            stream.Position = 0;
            content = new byte[checked((int)stream.Length)];
            stream.ReadExactly(content);
            if (stream.ReadByte() != -1)
                throw new CompatibilityException(CompatibilityErrorKind.TooLarge,
                    "Legacy project changed size while being read.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CompatibilityException(CompatibilityErrorKind.Io,
                $"The legacy project could not be read: {error.Message}", error);
        }
        return ImportBytes(content, options);
    }

    public static CompatibilityImport ImportBytes(byte[] content, CompatibilityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        CompatibilityInputLimits.Check(content.LongLength);
        using var stream = new MemoryStream(content, writable: false);
        var format = LegacyFormatDetector.DetectStream(stream);
        CompatibilityInputLimits.Check(content.LongLength, format);
        if (format is not (LegacyFormatKind.ClassicPreview or LegacyFormatKind.FreeLayout))
            throw Unsupported(format);
        var parsed = LegacyDocumentParser.Parse(content, format);
        var import = LegacyCompatibilityMapper.Map(parsed, options ?? new CompatibilityOptions(), new MigrationIds(format, content));
        if (options?.RejectLossy == true && import.Report.Level == CompatibilityLevel.Lossy)
            throw new CompatibilityException(CompatibilityErrorKind.UnsupportedFeature,
                $"The legacy project needs a lossy conversion ({import.Report.LossCount} semantic loss, {import.Report.DeviationCount} visual deviation).");
        return import;
    }

    private static CompatibilityException Unsupported(LegacyFormatKind format) =>
        new(CompatibilityErrorKind.UnsupportedFeature, format switch
        {
            LegacyFormatKind.Native => "The file is a native .ppgproj package; open it as a project.",
            LegacyFormatKind.TemporaryJson => "The file is a temporary JSON document.",
            _ => "The file is not a recognised legacy project."
        });
}

// Domain-separated generated identities depend only on input bytes, format and semantic role.
// Existing free layer/group GUIDs retain their legacy identity. Paths, time and process are excluded.
internal sealed class MigrationIds(LegacyFormatKind format, byte[] content)
{
    private readonly string _seed = $"ppg-migration-v1/{format}/{Convert.ToHexString(SHA256.HashData(content))}/";
    public Guid For(string role) => new(SHA256.HashData(Encoding.UTF8.GetBytes(_seed + role)).AsSpan(0, 16));
}
