using System.Diagnostics;
using System.Text.Json;
using Editor.Core;
using Editor.Formats;

namespace Editor.Application;

public sealed record RecoveryCandidate(
    string Id,
    string SnapshotPath,
    string MetadataPath,
    string? ProjectPath,
    string DisplayName,
    DateTimeOffset SavedAtUtc,
    int CanvasWidth,
    int CanvasHeight,
    int LayerCount,
    int ProcessId,
    DateTimeOffset? ProcessStartedUtc)
{
    public string SourceLabel => ProjectPath is null ? "未保存工程" : ProjectPath;
    public override string ToString() =>
        $"{DisplayName} · {SavedAtUtc.ToLocalTime():g} · {CanvasWidth}×{CanvasHeight} · {LayerCount} 层";
}

public sealed record RecoveryDiagnostic(string FileName, string Message);

public sealed record RecoveryLoadResult(
    IReadOnlyList<RecoveryCandidate> Candidates,
    IReadOnlyList<RecoveryDiagnostic> Diagnostics);

/// <summary>
/// Writes one native snapshot per editor session plus a versioned sidecar. It never changes the
/// session, its history, save target or saved marker; only applied document state is captured.
/// </summary>
public sealed class AutoRecoveryService
{
    public const int MetadataVersion = 1;
    public const int MaxCandidates = 20;
    public const long MaxSnapshotBytes = 512L * 1024 * 1024;
    public const string VerificationDisableVariable = "PPG_INTERNAL_VERIFY_DISABLE_AUTO_RECOVERY";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan OrphanAge = TimeSpan.FromHours(1);
    private readonly EditSession _session;
    private readonly string _applicationVersion;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<int, DateTimeOffset?, bool> _isProcessActive;
    private readonly object _gate = new();
    private EditorDocument? _lastCaptured;
    private RecoveryCandidate? _currentCandidate;
    public string DirectoryPath { get; }
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public bool HasSnapshot
    {
        get
        {
            lock (_gate)
                return _currentCandidate is not null && File.Exists(_currentCandidate.SnapshotPath);
        }
    }

    public static string DefaultDirectoryPath
    {
        get
        {
            var verification = Environment.GetEnvironmentVariable("PPG_INTERNAL_VERIFY_RECOVERY_DIRECTORY");
            return !string.IsNullOrWhiteSpace(verification)
                ? Path.GetFullPath(verification)
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PhigrosPreviewEditor", "Recovery");
        }
    }

    public static bool IsDisabledForVerification =>
        Environment.GetEnvironmentVariable(VerificationDisableVariable) == "1";

    public AutoRecoveryService(EditSession session, string directoryPath, string applicationVersion,
        Func<DateTimeOffset>? clock = null,
        Func<int, DateTimeOffset?, bool>? isProcessActive = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
        _applicationVersion = applicationVersion ?? throw new ArgumentNullException(nameof(applicationVersion));
        _clock = clock ?? (() => DateTimeOffset.Now);
        _isProcessActive = isProcessActive ?? DefaultIsProcessActive;
    }

    /// <summary>Capture only a settled, applied dirty document; active gestures are skipped.</summary>
    public bool CaptureIfDirty(string? projectPath, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (!_session.IsDirty) return false;
            try { _session.EnsureReadyForPersistence(); }
            catch (InvalidOperationException) { return false; }
            return CaptureIfChanged(_session.Document.Copy(), projectPath, now ?? _clock());
        }
    }

    public bool CaptureIfChanged(EditorDocument snapshot, string? projectPath, DateTimeOffset savedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_lastCaptured is not null && _currentCandidate is not null &&
                File.Exists(_currentCandidate.SnapshotPath) && _lastCaptured.StateEquals(snapshot))
                return false;
            return CaptureSnapshot(snapshot, projectPath, savedAt);
        }
    }

    public bool CaptureSnapshot(EditorDocument snapshot, string? projectPath, DateTimeOffset savedAt)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            snapshot.Validate();
            Directory.CreateDirectory(DirectoryPath);
            var snapshotPath = SnapshotPathFor(SessionId);
            var metadataPath = MetadataPathFor(SessionId);
            NativePackageStore.Save(snapshotPath, snapshot, _applicationVersion);
            var fullProject = string.IsNullOrWhiteSpace(projectPath) ? null : Path.GetFullPath(projectPath);
            var started = ProcessStartTimeUtc();
            var metadata = new
            {
                formatVersion = MetadataVersion,
                id = SessionId,
                snapshotFile = Path.GetFileName(snapshotPath),
                projectPath = fullProject,
                displayName = fullProject is null ? "未命名工程" : Path.GetFileName(fullProject),
                savedAtUtc = savedAt.ToUniversalTime().ToString("o"),
                processId = Environment.ProcessId,
                processStartedUtc = started.ToString("o"),
                canvasWidth = snapshot.CanvasWidth,
                canvasHeight = snapshot.CanvasHeight,
                layerCount = snapshot.Layers.Count
            };
            AtomicFile.Write(metadataPath, stream =>
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(metadata,
                    new JsonSerializerOptions { WriteIndented = true })));
            _lastCaptured = snapshot.Copy();
            _currentCandidate = new(SessionId, snapshotPath, metadataPath, fullProject,
                metadata.displayName, savedAt, snapshot.CanvasWidth, snapshot.CanvasHeight,
                snapshot.Layers.Count, Environment.ProcessId, started);
            Cleanup();
            return true;
        }
    }

    public RecoveryLoadResult LoadCandidates()
    {
        if (!Directory.Exists(DirectoryPath)) return new([], []);
        var candidates = new List<RecoveryCandidate>();
        var diagnostics = new List<RecoveryDiagnostic>();
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(DirectoryPath, "recovery-*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new([], [new("", "恢复目录无法读取：" + error.Message)]);
        }
        foreach (var metadataPath in paths)
        {
            try
            {
                var candidate = ReadCandidate(metadataPath);
                if (candidate.Id == SessionId) continue;
                if (_isProcessActive(candidate.ProcessId, candidate.ProcessStartedUtc)) continue;
                if (!File.Exists(candidate.SnapshotPath))
                {
                    diagnostics.Add(new(Path.GetFileName(metadataPath), "恢复快照文件不存在，已保留元数据。"));
                    continue;
                }
                candidates.Add(candidate);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                diagnostics.Add(new(Path.GetFileName(metadataPath), error.Message));
            }
        }
        return new(candidates.OrderByDescending(candidate => candidate.SavedAtUtc)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal).ToArray(), diagnostics);
    }

    public bool Delete(RecoveryCandidate candidate)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (!IsControlledId(candidate.Id))
                throw new ArgumentException("A controlled recovery ID is required.", nameof(candidate));
            var snapshot = SnapshotPathFor(candidate.Id);
            var metadata = MetadataPathFor(candidate.Id);
            var deleted = false;
            if (File.Exists(snapshot)) { File.Delete(snapshot); deleted = true; }
            if (File.Exists(metadata)) { File.Delete(metadata); deleted = true; }
            if (_currentCandidate?.Id == candidate.Id)
            {
                _currentCandidate = null;
                _lastCaptured = null;
            }
            return deleted;
        }
    }

    public void DeleteSessionSnapshot()
    {
        lock (_gate)
        {
            if (_currentCandidate is not null) Delete(_currentCandidate);
            else
            {
                var snapshot = SnapshotPathFor(SessionId);
                var metadata = MetadataPathFor(SessionId);
                if (File.Exists(snapshot)) File.Delete(snapshot);
                if (File.Exists(metadata)) File.Delete(metadata);
            }
            _currentCandidate = null;
            _lastCaptured = null;
        }
    }

    /// <summary>Called after a successful save or when the user explicitly discards a document.</summary>
    public void OnDocumentClean() => DeleteSessionSnapshot();

    public void OnDocumentReplaced() => DeleteSessionSnapshot();

    public RecoveryLoadResult Cleanup()
    {
        lock (_gate)
        {
            if (!Directory.Exists(DirectoryPath)) return new([], []);
            var load = LoadCandidates();
            var candidates = load.Candidates.ToList();
            var cutoff = _clock() - MaxAge;
            foreach (var candidate in candidates.Where(candidate => candidate.SavedAtUtc < cutoff).ToArray())
            {
                Delete(candidate);
                candidates.Remove(candidate);
            }
            var current = _currentCandidate is not null && File.Exists(_currentCandidate.SnapshotPath)
                ? _currentCandidate : null;
            var total = candidates.Sum(candidate => FileSize(candidate.SnapshotPath)) +
                (current is null ? 0 : FileSize(current.SnapshotPath));
            while (candidates.Count + (current is null ? 0 : 1) > MaxCandidates ||
                (total > MaxSnapshotBytes && candidates.Count > 0))
            {
                if (candidates.Count == 0) break;
                var oldest = candidates.MinBy(candidate => candidate.SavedAtUtc)!;
                var size = FileSize(oldest.SnapshotPath);
                Delete(oldest);
                candidates.Remove(oldest);
                total -= size;
            }
            CleanupOrphans();
            return new(candidates, load.Diagnostics);
        }
    }

    private RecoveryCandidate ReadCandidate(string metadataPath)
    {
        if (new FileInfo(metadataPath).Length > 65536)
            throw new InvalidDataException("恢复元数据超过 64 KiB。");
        using var input = File.OpenRead(metadataPath);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("formatVersion", out var version) ||
            version.GetInt32() != MetadataVersion)
            throw new InvalidDataException("恢复元数据版本不受支持。");
        var id = RequiredString(root, "id");
        if (!IsControlledId(id)) throw new InvalidDataException("恢复条目 ID 无效。");
        var snapshotName = RequiredString(root, "snapshotFile");
        if (!string.Equals(snapshotName, Path.GetFileName(SnapshotPathFor(id)), StringComparison.Ordinal))
            throw new InvalidDataException("恢复条目快照文件名无效。");
        var saved = DateTimeOffset.Parse(RequiredString(root, "savedAtUtc"));
        var projectPath = root.TryGetProperty("projectPath", out var project) &&
            project.ValueKind == JsonValueKind.String ? project.GetString() : null;
        var display = RequiredString(root, "displayName");
        var processId = RequiredInt(root, "processId");
        var processStarted = DateTimeOffset.TryParse(RequiredString(root, "processStartedUtc"), out var started)
            ? started : (DateTimeOffset?)null;
        return new(id, SnapshotPathFor(id), metadataPath, projectPath, display, saved,
            RequiredInt(root, "canvasWidth"), RequiredInt(root, "canvasHeight"),
            RequiredInt(root, "layerCount"), processId, processStarted);
    }

    private void CleanupOrphans()
    {
        var cutoff = _clock() - OrphanAge;
        foreach (var snapshot in Directory.EnumerateFiles(DirectoryPath, "recovery-*.ppgproj"))
        {
            var id = Path.GetFileNameWithoutExtension(snapshot)["recovery-".Length..];
            if (!IsControlledId(id)) continue;
            var metadata = MetadataPathFor(id);
            if (File.Exists(metadata) || id == SessionId) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(snapshot) < cutoff.UtcDateTime) File.Delete(snapshot);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private string SnapshotPathFor(string id) => Path.Combine(DirectoryPath, "recovery-" + id + ".ppgproj");
    private string MetadataPathFor(string id) => Path.Combine(DirectoryPath, "recovery-" + id + ".json");

    private static bool IsControlledId(string id) =>
        id.Length == 32 && id.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException("恢复元数据缺少 " + name + "。");

    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result : throw new InvalidDataException("恢复元数据缺少 " + name + "。");

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static DateTimeOffset ProcessStartTimeUtc()
    {
        try { return new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero); }
        catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException or
            NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static bool DefaultIsProcessActive(int processId, DateTimeOffset? started)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (started is null) return true;
            var actual = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return Math.Abs((actual - started.Value).TotalSeconds) < 5;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
