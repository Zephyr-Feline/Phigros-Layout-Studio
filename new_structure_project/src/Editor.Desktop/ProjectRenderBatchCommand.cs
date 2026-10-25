using System.Globalization;
using System.Text.Json;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;

namespace Editor.Desktop;

/// <summary>CLI front end for the same versioned multi-project plan used by the desktop.</summary>
public static class ProjectRenderBatchCommand
{
    public const string Usage = "Usage: Editor.Desktop --render-batch <plan.json>";

    public static int Run(string planPath, TextWriter output, TextWriter error, Action initialize)
    {
        ProjectRenderPlan plan;
        try
        {
            var fullPlan = Path.GetFullPath(planPath);
            plan = ReadPlan(fullPlan);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            error.WriteLine("BATCH PLAN INVALID: " + failure.Message);
            return 2;
        }
        try
        {
            initialize();
            var sources = new ProtectedSourcePaths();
            var result = ProjectRenderBatch.ExecuteAsync(plan,
                NativeImaging.ValidateImages,
                (document, transparent, width) =>
                    PngExporter.Render(document, transparent, NativeImaging.Decoder, width),
                (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
                sources, () => null, LocalFontRegistry.IsMissing,
                readImageDimensions: ImageAssetValidator.ReadEncodedDimensions,
                qualityPolicy: plan.QualityPolicy)
                .GetAwaiter().GetResult();
            foreach (var project in result.Projects)
            {
                output.WriteLine($"{project.Status}: {project.ProjectPath}" +
                    (project.Error is null ? "" : " — " + project.Error));
                foreach (var task in project.Tasks)
                    output.WriteLine($"  {task.Status}: {task.FileName}" +
                        (task.Error is null ? "" : " — " + task.Error));
            }
            output.WriteLine($"Manifest: {result.ManifestPath}");
            var success = result.Projects.All(project => project.Status == ProjectRenderStatus.Completed) &&
                result.ManifestError is null && !result.Cancelled;
            output.WriteLine(success ? "BATCH RENDER PASS" : "BATCH RENDER FAILED");
            return success ? 0 : 1;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            error.WriteLine("BATCH RENDER FAILED: " + failure.Message);
            return 1;
        }
    }

    private static ProjectRenderPlan ReadPlan(string planPath)
    {
        using var input = File.OpenRead(planPath);
        using var json = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("formatVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var formatVersion) ||
            formatVersion is < 1 or > 2 ||
            !root.TryGetProperty("outputParent", out var parent) ||
            !root.TryGetProperty("projects", out var projects) ||
            !root.TryGetProperty("tasks", out var tasks) ||
            parent.ValueKind != JsonValueKind.String ||
            projects.ValueKind != JsonValueKind.Array ||
            tasks.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                "Batch plan must have formatVersion 1 or 2, outputParent, projects and tasks.");
        var baseDirectory = Path.GetDirectoryName(planPath)!;
        string Resolve(string path) => Path.GetFullPath(Path.IsPathRooted(path)
            ? path : Path.Combine(baseDirectory, path));
        var projectPaths = projects.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidDataException("Every project entry must be a path string.");
            return Resolve(item.GetString()!);
        }).ToArray();
        var exportTasks = tasks.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("width", out var width) ||
                !item.TryGetProperty("transparent", out var transparent) ||
                !width.TryGetInt32(out var value) ||
                transparent.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("Every task needs integer width and boolean transparent.");
            return new PngExportTask(value, transparent.GetBoolean());
        }).ToArray();
        var qualityPolicy = ExportQualityPolicy.Notify;
        if (formatVersion == 1 && root.TryGetProperty("qualityPolicy", out _))
            throw new InvalidDataException("Batch plan format version 1 cannot contain qualityPolicy.");
        if (formatVersion >= 2)
        {
            if (!root.TryGetProperty("qualityPolicy", out var policy) ||
                policy.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<ExportQualityPolicy>(policy.GetString(), ignoreCase: false,
                    out qualityPolicy))
                throw new InvalidDataException(
                    "Batch plan format version 2 requires qualityPolicy Notify or BlockLowResolution.");
        }
        return new(Resolve(parent.GetString()!), projectPaths, exportTasks, qualityPolicy);
    }
}
