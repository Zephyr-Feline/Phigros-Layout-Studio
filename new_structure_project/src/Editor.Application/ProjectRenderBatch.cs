using System.Text.Json;
using Editor.Core;
using Editor.Formats;

namespace Editor.Application;

public sealed record ProjectRenderPlan(
    string OutputParent, IReadOnlyList<string> ProjectPaths, IReadOnlyList<PngExportTask> Tasks,
    ExportQualityPolicy QualityPolicy = ExportQualityPolicy.Notify);

public enum ProjectRenderStatus { Completed, Failed, Cancelled }

public sealed record ProjectRenderTaskResult(
    string ProjectPath,
    string ProjectName,
    ProjectRenderStatus Status,
    string? Error,
    IReadOnlyList<PngExportTaskResult> Tasks,
    IReadOnlyList<ExportWarning> Warnings);

public sealed record ProjectRenderProgress(int Completed, int Total, string Status);

public sealed record ProjectRenderBatchResult(
    string Directory,
    string ManifestPath,
    bool Cancelled,
    IReadOnlyList<ProjectRenderTaskResult> Projects,
    string? ManifestError = null);

/// <summary>
/// Reads and validates each native project independently, renders all requested sizes from that
/// project's snapshot, and writes one versioned manifest. A read/task failure is isolated to that
/// project/row; cancellation stops at a task boundary while retaining complete files.
/// </summary>
public static class ProjectRenderBatch
{
    public static async Task<ProjectRenderBatchResult> ExecuteAsync(
        ProjectRenderPlan plan,
        Action<EditorDocument> validateImages,
        Func<EditorDocument, bool, int?, byte[]> render,
        Action<string, byte[]> writeNew,
        ProtectedSourcePaths sources,
        Func<string?> currentProjectPath,
        Func<string, bool> isLocalFontMissing,
        IProgress<ProjectRenderProgress>? progress = null,
        CancellationToken cancellationToken = default,
        DateTime? localTime = null,
        Func<AssetBlob, (int Width, int Height)>? readImageDimensions = null,
        ExportQualityPolicy qualityPolicy = ExportQualityPolicy.Notify)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(validateImages);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(writeNew);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(currentProjectPath);
        ArgumentNullException.ThrowIfNull(isLocalFontMissing);
        var projects = NormalizeProjects(plan.ProjectPaths);
        var tasks = PngExportQueue.NormalizeTaskList(plan.Tasks, removeDuplicates: false);
        if (projects.Length == 0 || tasks.Count == 0)
            throw new InvalidOperationException("Choose at least one project and one export task.");
        // Protect every input before even checking output names; an earlier output must never
        // overwrite a project that has not been read yet.
        var checkSources = projects.Select(FilePathIdentity.PrepareTargetCheck).ToArray();
        foreach (var project in projects) sources.Add(project);
        var current = currentProjectPath();
        if (!string.IsNullOrWhiteSpace(current)) sources.Add(current);
        var directory = PngExportQueue.CreateRunDirectory(plan.OutputParent, localTime ?? DateTime.Now);
        var outputPaths = CreateOutputPaths(projects, tasks, directory);
        var checkTargets = new List<Action>();
        foreach (var path in outputPaths)
        {
            if (File.Exists(path)) throw new IOException($"Output already exists: {path}");
            sources.EnsureNotProtected(path);
            if (!string.IsNullOrWhiteSpace(current) && FilePathIdentity.AreSame(path, current))
                throw new InvalidOperationException("Choose another output folder; it must not replace the current project.");
            checkTargets.Add(FilePathIdentity.PrepareTargetCheck(path));
        }
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var total = projects.Length * tasks.Count;
        var resultProjects = new List<ProjectRenderTaskResult>(projects.Length);
        var completed = 0;
        var cancelled = false;
        for (var projectIndex = 0; projectIndex < projects.Length; projectIndex++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                resultProjects.AddRange(RemainingCancelled(projects, tasks, projectIndex));
                break;
            }
            var projectPath = projects[projectIndex];
            var projectName = Path.GetFileName(projectPath);
            try
            {
                checkSources[projectIndex]();
                var document = NativePackageStore.Read(projectPath);
                validateImages(document);
                var warnings = ExportPreflight.Inspect(document, isLocalFontMissing,
                    readImageDimensions: readImageDimensions);
                var taskWarnings = tasks.Select(task => ExportPreflight.Inspect(document,
                    isLocalFontMissing, task.Width, readImageDimensions, task.Transparent)).ToArray();
                if (qualityPolicy == ExportQualityPolicy.BlockLowResolution &&
                    taskWarnings.Any(items => items.Any(warning =>
                        warning.Kind == ExportWarningKind.LowResolutionArtwork)))
                {
                    var blocked = tasks.Select((task, taskIndex) =>
                        new PngExportTaskResult(task,
                            Path.GetFileName(outputPaths[projectIndex, taskIndex]),
                            default, PngExportTaskStatus.Failed,
                            "Export blocked by the quality policy because this project upscales source artwork.")
                        { Warnings = taskWarnings[taskIndex] }).ToArray();
                    resultProjects.Add(new(projectPath, projectName, ProjectRenderStatus.Failed,
                        "Export blocked by the quality policy.", blocked, warnings));
                    progress?.Report(new(completed, total, $"{projectName} 被质量策略阻止"));
                    continue;
                }
                var taskResults = new List<PngExportTaskResult>(tasks.Count);
                var projectStatus = ProjectRenderStatus.Completed;
                for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
                {
                    var task = tasks[taskIndex];
                    var currentTaskWarnings = taskWarnings[taskIndex];
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        projectStatus = ProjectRenderStatus.Cancelled;
                        taskResults.Add(new(task, Path.GetFileName(outputPaths[projectIndex, taskIndex]),
                            default, PngExportTaskStatus.Cancelled, null)
                            { Warnings = currentTaskWarnings });
                        for (var rest = taskIndex + 1; rest < tasks.Count; rest++)
                            taskResults.Add(new(tasks[rest], Path.GetFileName(outputPaths[projectIndex, rest]),
                                default, PngExportTaskStatus.NotRun, null));
                        break;
                    }
                    try
                    {
                        var size = PngExportSize.Create(document, task.Width);
                        var bytes = render(document, task.Transparent, task.Width);
                        if (bytes is null || bytes.Length == 0)
                            throw new InvalidDataException("PNG renderer produced no output.");
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = outputPaths[projectIndex, taskIndex];
                        checkTargets[projectIndex * tasks.Count + taskIndex]();
                        sources.EnsureNotProtected(path);
                        writeNew(path, bytes);
                        completed++;
                        taskResults.Add(new(task, Path.GetFileName(path), size,
                            PngExportTaskStatus.Completed, null) { Warnings = currentTaskWarnings });
                        progress?.Report(new(completed, total,
                            $"{projectName} · {task.FileName} 已完成"));
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        projectStatus = ProjectRenderStatus.Cancelled;
                        taskResults.Add(new(task, Path.GetFileName(outputPaths[projectIndex, taskIndex]),
                            default, PngExportTaskStatus.Cancelled, null)
                            { Warnings = currentTaskWarnings });
                        for (var rest = taskIndex + 1; rest < tasks.Count; rest++)
                            taskResults.Add(new(tasks[rest], Path.GetFileName(outputPaths[projectIndex, rest]),
                                default, PngExportTaskStatus.NotRun, null));
                        break;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        projectStatus = ProjectRenderStatus.Failed;
                        taskResults.Add(new(task, Path.GetFileName(outputPaths[projectIndex, taskIndex]),
                            default, PngExportTaskStatus.Failed, error.Message)
                            { Warnings = currentTaskWarnings });
                        progress?.Report(new(completed, total, $"{projectName} · {task.FileName} 失败"));
                    }
                }
                resultProjects.Add(new(projectPath, projectName, projectStatus, null, taskResults, warnings));
                if (cancelled)
                {
                    resultProjects.AddRange(RemainingCancelled(projects, tasks, projectIndex + 1));
                    break;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                var taskResults = tasks.Select((task, taskIndex) => new PngExportTaskResult(
                    task, Path.GetFileName(outputPaths[projectIndex, taskIndex]),
                    default, PngExportTaskStatus.Failed, error.Message)).ToArray();
                resultProjects.Add(new(projectPath, projectName, ProjectRenderStatus.Failed,
                    error.Message, taskResults, []));
                progress?.Report(new(completed, total, $"{projectName} 读取失败"));
            }
            await Task.Delay(1).ConfigureAwait(false);
        }
        var manifestPath = Path.Combine(directory, "project-render-manifest.json");
        string? manifestError = null;
        try
        {
            var manifest = new
            {
                formatVersion = 2,
                createdUtc = DateTime.UtcNow.ToString("o"),
                qualityPolicy = qualityPolicy.ToString(),
                outputDirectory = directory,
                tasks = tasks.Select(task => new { width = task.Width, transparent = task.Transparent }),
                projects = resultProjects.Select(project => new
                {
                    path = project.ProjectPath,
                    name = project.ProjectName,
                    status = project.Status.ToString(),
                    error = project.Error,
                    warnings = project.Warnings.Select(warning => new
                    {
                        kind = warning.Kind.ToString(),
                        layer = warning.LayerName,
                        message = warning.Message
                    }),
                    tasks = project.Tasks.Select(item => new
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
                })
            };
            writeNew(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            manifestError = error.Message;
        }
        return new(directory, manifestPath, cancelled, resultProjects.AsReadOnly(), manifestError);
    }

    public static string[] NormalizeProjects(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException("Project file not found.", full);
            if (seen.Add(full)) result.Add(full);
        }
        return result.ToArray();
    }

    private static string[,] CreateOutputPaths(string[] projects, IReadOnlyList<PngExportTask> tasks,
        string directory)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new string[projects.Length, tasks.Count];
        for (var projectIndex = 0; projectIndex < projects.Length; projectIndex++)
        {
            var baseName = Sanitize(Path.GetFileNameWithoutExtension(projects[projectIndex]));
            for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
            {
                var task = tasks[taskIndex];
                var stem = $"{baseName}-{task.Width}{(task.Transparent ? "-transparent" : "")}";
                var name = stem + ".png";
                var attempt = 2;
                while (!used.Add(name)) name = $"{stem}-{attempt++}.png";
                result[projectIndex, taskIndex] = Path.Combine(directory, name);
            }
        }
        return result;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return cleaned.Length == 0 ? "project" : cleaned;
    }

    private static IEnumerable<ProjectRenderTaskResult> RemainingCancelled(
        string[] projects, IReadOnlyList<PngExportTask> tasks, int start)
    {
        for (var index = start; index < projects.Length; index++)
            yield return new(projects[index], Path.GetFileName(projects[index]),
                ProjectRenderStatus.Cancelled, null,
                tasks.Select(task => new PngExportTaskResult(task, task.FileName, default,
                    PngExportTaskStatus.NotRun, null)).ToArray(), []);
    }
}
