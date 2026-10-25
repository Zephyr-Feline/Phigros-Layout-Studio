using Editor.Core;
using Editor.Formats.Compatibility;

namespace Editor.Formats;

public sealed record LegacyReadResult(EditorDocument? Document, IReadOnlyList<string> Diagnostics)
{
    public bool Supported => Document is not null && Diagnostics.All(message => !message.StartsWith("ERROR:", StringComparison.Ordinal));
}

/// <summary>
/// Read probe kept for the P1 JSON/legacy workflow. It is now a thin adapter over the P3
/// compatibility layer (<see cref="CompatibilityService"/>), so there is exactly one legacy parser.
/// </summary>
public static class LegacyReader
{
    public static LegacyReadResult Read(string path)
    {
        try
        {
            var import = CompatibilityService.Import(path);
            return new(import.Document, import.Report.Diagnostics.Select(Format).ToList());
        }
        catch (CompatibilityException error)
        {
            return new(null, [$"ERROR: {error.Message}"]);
        }
        catch (Exception error) when (error is IOException or InvalidDataException
            or ArgumentException or NotSupportedException)
        {
            return new(null, [$"ERROR: {error.Message}"]);
        }
    }

    private static string Format(ConversionDiagnostic diagnostic) => diagnostic.Severity switch
    {
        DiagnosticSeverity.Loss => $"WARNING: {diagnostic.Message}",
        DiagnosticSeverity.Deviation => $"WARNING: {diagnostic.Message}",
        DiagnosticSeverity.Normalized => $"WARNING: {diagnostic.Message}",
        _ => $"INFO: {diagnostic.Message}"
    };
}
