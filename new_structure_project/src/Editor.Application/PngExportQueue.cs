using System.Text.Json;
using Editor.Core;

namespace Editor.Application;

public sealed record PngExportTask(int Width, bool Transparent)
{
    public string FileName => $"preview-{Width}{(Transparent ? "-transparent" : "")}.png";
}

public enum PngExportTaskStatus { Completed, Failed, Cancelled, NotRun }

public sealed record PngExportTaskResult(
    PngExportTask Task, string FileName, PngExportSize Size, PngExportTaskStatus Status, string? Error)
{
    public IReadOnlyList<ExportWarning> Warnings { get; init; } = [];
}

public sealed record PngExportQueueProgress(int Completed, int Total, string Status);

public sealed record PngExportQueueResult(
    string Directory, IReadOnlyList<PngExportTaskResult> Items, string ManifestPath,
    bool Cancelled, string? ManifestError = null);

/// <summary>
/// Validates every output path before rendering, then writes serially from one document snapshot.
/// Completed files are retained if a later task fails or the user cancels; one file is never partial.
/// </summary>
public static class PngExportQueue
{
    public static IReadOnlyList<PngExportTask> Normalize(EditorDocument document,
        IEnumerable<PngExportTask> tasks, bool removeDuplicates,
        PngExportRegion? outputRegion = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = NormalizeTaskList(tasks, removeDuplicates);
        foreach (var task in result)
            _ = PngExportSize.Create(document, task.Width, outputRegion);
        return result;
    }

    /// <summary>Width/identity validation for plans whose documents are read later.</summary>
    public static IReadOnlyList<PngExportTask> NormalizeTaskList(
        IEnumerable<PngExportTask> tasks, bool removeDuplicates)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var result = new List<PngExportTask>();
        var seen = new HashSet<(int, bool)>();
        foreach (var task in tasks)
        {
            if (task.Width is < 1 or > PngExportSize.MaxDimension)
                throw new InvalidDataException(
                    $"PNG width must be between 1 and {PngExportSize.MaxDimension} pixels.");
            if (seen.Add((task.Width, task.Transparent)))
            {
                result.Add(task);
                continue;
            }
            if (!removeDuplicates)
                throw new InvalidOperationException($"Duplicate output task: {task.FileName}");
        }
        if (result.Count == 0)
            throw new InvalidOperationException("Add at least one PNG export task.");
        return result.AsReadOnly();
    }

    public static string CreateRunDirectory(string parentDirectory, DateTime localTime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        var parent = Path.GetFullPath(parentDirectory);
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("Choose an existing parent folder for the export queue.");
        var stem = "PhigrosExport-" + localTime.ToString("yyyyMMdd-HHmmss");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Path.Combine(parent, stem +
                (attempt == 0 ? "" : "-" + attempt.ToString("00")));
            if (!Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not allocate a new export directory.");
    }

    public static Task<PngExportQueueResult> ExecuteAsync(
        EditorDocument snapshot, IReadOnlyList<PngExportTask> tasks, string directory,
        Action<EditorDocument> validateImages,
        Func<EditorDocument, bool, int?, byte[]> render,
        Action<string, byte[]> writeNew,
        ProtectedSourcePaths sources,
        Func<string?> currentProjectPath,
        IProgress<PngExportQueueProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<string, bool>? isLocalFontMissing = null,
        Func<AssetBlob, (int Width, int Height)>? readImageDimensions = null,
        ExportQualityPolicy qualityPolicy = ExportQualityPolicy.Notify)
    {
        ArgumentNullException.ThrowIfNull(render);
        return ExecuteCoreAsync(snapshot, tasks, directory, validateImages,
            (document, transparent, width, _) => render(document, transparent, width),
            writeNew, sources, currentProjectPath, progress, cancellationToken,
            isLocalFontMissing, readImageDimensions, qualityPolicy, null);
    }

    public static Task<PngExportQueueResult> ExecuteRegionAsync(
        EditorDocument snapshot, IReadOnlyList<PngExportTask> tasks, string directory,
        Action<EditorDocument> validateImages,
        Func<EditorDocument, bool, int?, PngExportRegion?, byte[]> render,
        Action<string, byte[]> writeNew,
        ProtectedSourcePaths sources,
        Func<string?> currentProjectPath,
        IProgress<PngExportQueueProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<string, bool>? isLocalFontMissing = null,
        Func<AssetBlob, (int Width, int Height)>? readImageDimensions = null,
        ExportQualityPolicy qualityPolicy = ExportQualityPolicy.Notify,
        PngExportRegion? outputRegion = null)
        => ExecuteCoreAsync(snapshot, tasks, directory, validateImages, render,
            writeNew, sources, currentProjectPath, progress, cancellationToken,
            isLocalFontMissing, readImageDimensions, qualityPolicy, outputRegion);

    private static async Task<PngExportQueueResult> ExecuteCoreAsync(
        EditorDocument snapshot, IReadOnlyList<PngExportTask> tasks, string directory,
        Action<EditorDocument> validateImages,
        Func<EditorDocument, bool, int?, PngExportRegion?, byte[]> render,
        Action<string, byte[]> writeNew,
        ProtectedSourcePaths sources,
        Func<string?> currentProjectPath,
        IProgress<PngExportQueueProgress>? progress,
        CancellationToken cancellationToken,
        Func<string, bool>? isLocalFontMissing,
        Func<AssetBlob, (int Width, int Height)>? readImageDimensions,
        ExportQualityPolicy qualityPolicy,
        PngExportRegion? outputRegion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(validateImages);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(writeNew);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(currentProjectPath);
        snapshot.Validate();
        var normalized = Normalize(snapshot, tasks, removeDuplicates: false, outputRegion);
        var fullDirectory = Path.GetFullPath(directory);
        if (Directory.Exists(fullDirectory))
            throw new IOException("The export queue directory must be new.");

        var paths = normalized.Select(task => Path.Combine(fullDirectory, task.FileName)).ToArray();
        var checkTargets = paths.Select(FilePathIdentity.PrepareTargetCheck).ToArray();
        var projectPath = currentProjectPath();
        for (var index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            if (File.Exists(path))
                throw new IOException($"Output already exists: {path}");
            sources.EnsureNotProtected(path);
            if (projectPath is not null && FilePathIdentity.AreSame(path, projectPath))
                throw new InvalidOperationException("Choose another output folder; it must not replace the current project.");
        }
        validateImages(snapshot);
        var quality = normalized.Select(task =>
            isLocalFontMissing is not null && readImageDimensions is not null
                ? ExportPreflight.Inspect(snapshot, isLocalFontMissing, task.Width,
                    readImageDimensions, task.Transparent, outputRegion)
                : (IReadOnlyList<ExportWarning>)Array.Empty<ExportWarning>()).ToArray();
        if (qualityPolicy == ExportQualityPolicy.BlockLowResolution &&
            quality.Any(warnings => warnings.Any(warning =>
                warning.Kind == ExportWarningKind.LowResolutionArtwork)))
            throw new InvalidOperationException(
                "Export blocked by the quality policy because one or more tasks upscale source artwork.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(fullDirectory);

        var results = new List<PngExportTaskResult>(normalized.Count);
        var cancelled = false;
        for (var index = 0; index < normalized.Count; index++)
        {
            var task = normalized[index];
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(new(task, task.FileName, default, PngExportTaskStatus.Cancelled, null)
                    { Warnings = quality[index] });
                for (var rest = index + 1; rest < normalized.Count; rest++)
                    results.Add(new(normalized[rest], normalized[rest].FileName, default,
                        PngExportTaskStatus.NotRun, null) { Warnings = quality[rest] });
                break;
            }
            try
            {
                var size = PngExportSize.Create(snapshot, task.Width, outputRegion);
                var bytes = render(snapshot, task.Transparent, task.Width, outputRegion);
                if (bytes is null || bytes.Length == 0)
                    throw new InvalidDataException("PNG renderer produced no output.");
                cancellationToken.ThrowIfCancellationRequested();
                checkTargets[index]();
                sources.EnsureNotProtected(paths[index]);
                projectPath = currentProjectPath();
                if (projectPath is not null && FilePathIdentity.AreSame(paths[index], projectPath))
                    throw new InvalidOperationException("The output path became the current project.");
                writeNew(paths[index], bytes);
                results.Add(new(task, task.FileName, size, PngExportTaskStatus.Completed, null)
                    { Warnings = quality[index] });
                progress?.Report(new(index + 1, normalized.Count,
                    $"{task.FileName} 已完成（{size.Width} × {size.Height}）"));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                results.Add(new(task, task.FileName, default, PngExportTaskStatus.Cancelled, null)
                    { Warnings = quality[index] });
                for (var rest = index + 1; rest < normalized.Count; rest++)
                    results.Add(new(normalized[rest], normalized[rest].FileName, default,
                        PngExportTaskStatus.NotRun, null) { Warnings = quality[rest] });
                break;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                results.Add(new(task, task.FileName, default, PngExportTaskStatus.Failed, error.Message)
                    { Warnings = quality[index] });
                progress?.Report(new(index + 1, normalized.Count, $"{task.FileName} 失败：{error.Message}"));
            }
            // Do not capture a GUI dispatcher: the dialog pumps between tasks while renders
            // are marshalled by the caller when the renderer requires the UI thread.
            await Task.Delay(1).ConfigureAwait(false);
        }

        var manifestPath = Path.Combine(fullDirectory, "export-manifest.json");
        string? manifestError = null;
        try
        {
            var manifest = new
            {
                formatVersion = 2,
                createdUtc = DateTime.UtcNow.ToString("o"),
                qualityPolicy = qualityPolicy.ToString(),
                sourceCanvas = new { width = snapshot.CanvasWidth, height = snapshot.CanvasHeight },
                tasks = results.Select(item => new
                {
                    width = item.Task.Width,
                    height = item.Size.Height,
                    transparent = item.Task.Transparent,
                    fileName = item.FileName,
                    status = item.Status.ToString(),
                    error = item.Error,
                    warnings = item.Warnings.Select(warning => new
                    {
                        kind = warning.Kind.ToString(),
                        layer = warning.LayerName,
                        message = warning.Message
                    })
                })
            };
            writeNew(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            manifestError = error.Message;
        }
        return new(fullDirectory, results.AsReadOnly(), manifestPath, cancelled, manifestError);
    }
}
