using Editor.Core;
using Editor.Formats;
using Editor.Formats.Compatibility;

namespace Editor.Application;

/// <summary>The desktop's content router and transient conversion state. Commit state only after validation.</summary>
public sealed class ProjectOpenWorkflow(NativeProjectWorkflow native, Action<EditorDocument> validateImages,
    CompatibilityOptions options, ProtectedSourcePaths protectedPaths)
{
    public string? SourceLabel { get; private set; }
    public string Summary { get; private set; } = "Ready.";
    public IReadOnlyList<string> Details { get; private set; } = [];

    public void New()
    {
        native.New();
        ResetConversion();
        Summary = "Ready.";
    }

    public void Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var checkSource = FilePathIdentity.PrepareTargetCheck(fullPath);
        var format = LegacyFormatDetector.DetectFile(fullPath);
        checkSource();
        if (format == LegacyFormatKind.Native)
        {
            native.Open(fullPath);
            ResetConversion();
            Summary = "Project loaded.";
            return;
        }

        EditorDocument candidate;
        var protectSource = protectedPaths.PrepareAdd(fullPath);
        string summary;
        string[] details;
        if (format == LegacyFormatKind.TemporaryJson)
        {
            candidate = NativeDocumentStore.Read(fullPath);
            summary = "Temporary JSON loaded as an unsaved native document.";
            details = [];
        }
        else if (format is LegacyFormatKind.ClassicPreview or LegacyFormatKind.FreeLayout)
        {
            var import = CompatibilityService.Import(fullPath, options);
            candidate = import.Document;
            summary = $"Imported source document {import.Report.Summary}. Save Project writes a new .ppgproj; the source file stays protected.";
            details = import.Report.Lines.ToArray();
        }
        else throw new CompatibilityException(CompatibilityErrorKind.Unknown,
            "The file is not a recognised project document.");

        validateImages(candidate);
        checkSource();
        native.ImportConverted(candidate);
        ResetConversion();
        protectSource();
        SourceLabel = Path.GetFileName(fullPath);
        Summary = summary;
        Details = details;
    }

    /// <summary>Load a recovery snapshot as a fresh unsaved native document.</summary>
    public void RestoreRecovered(EditorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        validateImages(document);
        native.ImportConverted(document);
        ResetConversion();
        SourceLabel = null;
        Summary = "Recovered unsaved project.";
        Details = ["Auto recovery contains applied document content only; side-panel drafts, undo history and view state are not included."];
    }

    private void ResetConversion()
    {
        protectedPaths.Clear();
        SourceLabel = null;
        Details = [];
    }
}
