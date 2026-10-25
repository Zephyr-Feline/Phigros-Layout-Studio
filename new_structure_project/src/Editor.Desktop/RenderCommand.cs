using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using System.Globalization;

namespace Editor.Desktop;

/// <summary>Public CLI export, sharing desktop import, validation, rendering and atomic writes.</summary>
public static class RenderCommand
{
    public const string Usage =
        "Usage: Editor.Desktop --render <project> <output.png> [--transparent] [--width <pixels>] " +
        "[--crop <x,y,width,height>] [--block-low-resolution]";

    // The caller initializes the headless platform only after syntax validation.
    public static int Run(string[] args, TextWriter output, TextWriter error, Action initialize)
    {
        if (!TryParse(args, out var transparent, out var outputWidth, out var outputRegion,
            out var blockLowResolution))
        {
            error.WriteLine(Usage);
            return 2;
        }
        try
        {
            var source = Path.GetFullPath(args[1]);
            var destination = Path.GetFullPath(args[2]);
            if (!Path.GetExtension(destination).Equals(".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output path must end in .png.");
            if (FilePathIdentity.AreSame(source, destination))
                throw new InvalidOperationException("Choose another output path; the input project must not be overwritten.");
            var checkSource = FilePathIdentity.PrepareTargetCheck(source);
            var checkDestination = FilePathIdentity.PrepareTargetCheck(destination);
            initialize();
            checkSource();
            checkDestination();
            var fonts = LocalFontLibrary.Default;
            foreach (var warning in fonts.Warnings) error.WriteLine("FONT WARNING: " + warning);
            var session = new EditSession(new EditorDocument());
            var native = new NativeProjectWorkflow(session, NativeImaging.ValidateImages, App.ProductVersion);
            var sources = new ProtectedSourcePaths();
            var projects = new ProjectOpenWorkflow(native, NativeImaging.ValidateImages,
                NativeImaging.Compatibility, sources);
            projects.Open(source);
            checkSource();
            var size = PngExportSize.Create(session.Document, outputWidth, outputRegion);
            sources.Add(source);
            if (projects.SourceLabel is not null)
            {
                error.WriteLine(projects.Summary);
                foreach (var detail in projects.Details) error.WriteLine(detail);
            }
            var warnings = ExportPreflight.Inspect(session.Document,
                LocalFontRegistry.IsMissing, size.Width, ImageAssetValidator.ReadEncodedDimensions,
                transparentBackground: transparent, outputRegion: outputRegion);
            foreach (var warning in warnings)
                error.WriteLine($"WARNING [{warning.Kind}] {warning.LayerName}: {warning.Message}");
            if (blockLowResolution && warnings.Any(warning =>
                    warning.Kind == ExportWarningKind.LowResolutionArtwork))
            {
                error.WriteLine("RENDER BLOCKED: source artwork would be upscaled at the requested output width.");
                return 1;
            }
            var export = new PngExportWorkflow(session, NativeImaging.ValidateImages,
                (document, transparent, width) => PngExporter.Render(document, transparent, NativeImaging.Decoder, width),
                NativeDocumentStore.WriteBytesAtomically, sources, () => native.CurrentPath,
                ImageAssetValidator.ReadEncodedDimensions,
                (document, transparent, width, region) =>
                    PngExporter.Render(document, transparent, NativeImaging.Decoder, width, region));
            checkDestination();
            export.Export(destination, transparent, outputWidth, outputRegion);
            output.WriteLine($"PNG exported: {destination} ({size.Width} x {size.Height}; " +
                (transparent ? "transparent background" : "document background") +
                (outputRegion is { } region
                    ? $"; crop {region.X},{region.Y},{region.Width},{region.Height})"
                    : ")"));
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error.WriteLine("RENDER FAILED: " + exception.Message);
            return 1;
        }
    }

    private static bool TryParse(string[] args, out bool transparent, out int? width,
        out PngExportRegion? outputRegion, out bool blockLowResolution)
    {
        transparent = false;
        width = null;
        outputRegion = null;
        blockLowResolution = false;
        if (args.Length < 3 || args[0] != "--render" ||
            string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[2])) return false;
        for (var i = 3; i < args.Length; i++)
        {
            if (args[i] == "--transparent" && !transparent) transparent = true;
            else if (args[i] == "--block-low-resolution" && !blockLowResolution)
                blockLowResolution = true;
            else if (args[i] == "--width" && width is null)
            {
                if (++i >= args.Length ||
                    !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture,
                        out var value) ||
                    value is < 1 or > PngExportSize.MaxDimension) return false;
                width = value;
            }
            else if (args[i] == "--crop" && outputRegion is null)
            {
                if (++i >= args.Length ||
                    !TryParseRegion(args[i], out var parsedRegion)) return false;
                outputRegion = parsedRegion;
            }
            else return false;
        }
        return true;
    }

    private static bool TryParseRegion(string text, out PngExportRegion region)
    {
        region = default;
        var parts = text.Split(',');
        if (parts.Length != 4 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height) ||
            !double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            x < 0 || y < 0 || width <= 0 || height <= 0)
            return false;
        region = new(x, y, width, height);
        return true;
    }
}
