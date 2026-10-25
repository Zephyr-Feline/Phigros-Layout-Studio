using Editor.Core;

namespace Editor.Formats.Compatibility;

/// <summary>Format families the compatibility layer can identify from content, never from extension.</summary>
public enum LegacyFormatKind
{
    Native,
    TemporaryJson,
    ClassicPreview,
    FreeLayout,
    Unknown
}

/// <summary>How faithfully a legacy document is represented by the canonical V2 document.</summary>
public enum CompatibilityLevel
{
    /// <summary>Every legacy semantic is represented without change.</summary>
    Exact,
    /// <summary>Valid input whose identity/representation is normalised without a known semantic or visual loss.</summary>
    Normalized,
    /// <summary>Opens with a known semantic loss or registered visual deviation; acceptability is not implied.</summary>
    Lossy,
    /// <summary>Cannot be represented correctly; the import is rejected.</summary>
    Unsupported
}

/// <summary>Per-dimension status used by the compatibility report required by REQ-COMPAT-004.</summary>
public enum DimensionStatus
{
    Exact,
    Normalized,
    Lossy,
    Unsupported,
    NotVerified,
    NotApplicable
}

public enum CompatibilityDimensionKind
{
    /// <summary>数据可读</summary>
    DataReadable,
    /// <summary>可编辑语义保持</summary>
    EditableSemantics,
    /// <summary>视觉布局保持</summary>
    VisualLayout,
    /// <summary>旧版可重开</summary>
    LegacyReopen
}

/// <summary>
/// Severity of a conversion finding. <see cref="Loss"/> means a legacy semantic is not represented;
/// <see cref="Deviation"/> means the data is represented but the presentation engine differs and the
/// difference is registered in the deviation registry (P3_COMPATIBILITY_SPEC.md).
/// </summary>
public enum DiagnosticSeverity { Info, Normalized, Deviation, Loss }

public sealed record ConversionDiagnostic(string Code, DiagnosticSeverity Severity, string Message,
    string Scope, Guid? LayerId = null);

public sealed record CompatibilityDimension(CompatibilityDimensionKind Kind, DimensionStatus Status,
    string Detail);

/// <summary>Structured conversion report: levels, findings and the four REQ-COMPAT-004 dimensions.</summary>
public sealed record CompatibilityReport(LegacyFormatKind Format, int SourceVersion,
    CompatibilityLevel Level, IReadOnlyList<ConversionDiagnostic> Diagnostics,
    IReadOnlyList<CompatibilityDimension> Dimensions)
{
    public int LossCount => Diagnostics.Count(item => item.Severity == DiagnosticSeverity.Loss);

    public int DeviationCount => Diagnostics.Count(item => item.Severity == DiagnosticSeverity.Deviation);

    public int NormalizedCount => Diagnostics.Count(item => item.Severity == DiagnosticSeverity.Normalized);

    public string Summary => $"Source format {Format}, revision {SourceVersion}: {Level}" +
        $" ({LossCount} loss, {DeviationCount} deviation, {NormalizedCount} normalized)";

    /// <summary>One line per finding, for the headless probe and the desktop status text.</summary>
    public IEnumerable<string> Lines => Diagnostics.Select(item =>
        $"[{item.Severity}] {item.Code} ({item.Scope}): {item.Message}");
}

/// <summary>Error classification for a refused import (REQ-COMPAT compatible error kinds).</summary>
public enum CompatibilityErrorKind
{
    Malformed,
    UnsupportedVersion,
    UnsupportedFeature,
    InvalidGeometry,
    MissingAsset,
    TooLarge,
    Io,
    Unknown
}

public sealed class CompatibilityException : Exception
{
    public CompatibilityException(CompatibilityErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;

    public CompatibilityErrorKind Kind { get; }
}

/// <summary>Result of importing a legacy project into the canonical V2 document model.</summary>
public sealed record CompatibilityImport(EditorDocument Document, CompatibilityReport Report);

/// <summary>Injection points the format layer cannot own itself.</summary>
public sealed class CompatibilityOptions
{
    /// <summary>
    /// Encoded image dimensions, provided by the rendering layer
    /// (<see cref="Editor.Rendering.ImageAssetValidator.ReadEncodedDimensions"/> in the desktop and
    /// test hosts). Without it a classic project keeps the 1920 x 1080 reference canvas, because the
    /// V1 canvas depends on the background bitmap size.
    /// </summary>
    public Func<AssetBlob, (int Width, int Height)>? ProbeImageSize { get; init; }

    /// <summary>True only after every decoded pixel is known opaque. Missing/failed evidence is a visual deviation.</summary>
    public Func<AssetBlob, bool>? ProbeImageOpacity { get; init; }

    /// <summary>Reject lossy conversions instead of importing them with diagnostics.</summary>
    public bool RejectLossy { get; init; }
}
