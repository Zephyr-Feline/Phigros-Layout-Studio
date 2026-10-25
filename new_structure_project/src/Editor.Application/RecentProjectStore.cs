using System.Text.Json;
using Editor.Formats;

namespace Editor.Application;

public sealed record RecentProjectEntry(
    string Path, string DisplayName, DateTimeOffset LastOpenedUtc, bool Exists);

public sealed record RecentProjectDiagnostic(string Message);

public sealed record RecentProjectSnapshot(
    IReadOnlyList<RecentProjectEntry> Entries,
    IReadOnlyList<RecentProjectDiagnostic> Diagnostics);

/// <summary>
/// Best-effort user-data index of successfully opened/saved native projects. Reads and writes are
/// independent of project documents; a broken index starts empty and never blocks an open/save.
/// </summary>
public sealed class RecentProjectStore
{
    public const int CurrentVersion = 1;
    public const int MaxEntries = 12;
    public const int MaxBytes = 65536;
    public string FilePath { get; }
    public string? LastError { get; private set; }

    public static string DefaultPath
    {
        get
        {
            var verification = Environment.GetEnvironmentVariable("PPG_INTERNAL_VERIFY_RECENT_PROJECTS");
            return !string.IsNullOrWhiteSpace(verification)
                ? System.IO.Path.GetFullPath(verification)
                : System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PhigrosPreviewEditor", "RecentProjects.json");
        }
    }

    public RecentProjectStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = System.IO.Path.GetFullPath(filePath);
    }

    public RecentProjectSnapshot Load()
    {
        if (!File.Exists(FilePath)) return new([], []);
        var diagnostics = new List<RecentProjectDiagnostic>();
        try
        {
            using var input = File.OpenRead(FilePath);
            if (input.Length > MaxBytes)
            {
                diagnostics.Add(new("最近工程索引超过 64 KiB，已忽略。"));
                return new([], diagnostics);
            }
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("formatVersion", out var version) ||
                version.GetInt32() != CurrentVersion ||
                !root.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("最近工程索引版本或结构无效。");
            var result = new List<RecentProjectEntry>();
            foreach (var item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("path", out var path) ||
                    !item.TryGetProperty("displayName", out var display) ||
                    !item.TryGetProperty("lastOpenedUtc", out var stamp) ||
                    path.ValueKind != JsonValueKind.String ||
                    display.ValueKind != JsonValueKind.String ||
                    stamp.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(stamp.GetString(), out var opened))
                    throw new InvalidDataException("最近工程索引包含无效条目。");
                var full = System.IO.Path.GetFullPath(path.GetString()!);
                var exists = false;
                try { exists = File.Exists(full); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                result.Add(new(full, display.GetString()!, opened, exists));
            }
            return new(result.GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).Take(MaxEntries).ToArray(), diagnostics);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or FormatException or
            OverflowException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Add(new("最近工程索引无法读取：" + error.Message));
            return new([], diagnostics);
        }
    }

    /// <summary>Returns false when the index could not be written; callers must not fail the save/open.</summary>
    public bool Record(string path, DateTimeOffset openedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        try
        {
            return WithLock(() =>
            {
                var snapshot = Load();
                var entries = snapshot.Entries.Where(entry =>
                    !string.Equals(entry.Path, full, StringComparison.OrdinalIgnoreCase)).ToList();
                entries.Insert(0, new(full, System.IO.Path.GetFileName(full), openedAt, true));
                if (entries.Count > MaxEntries) entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
                Write(entries);
                return true;
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LastError = error.Message;
            return false;
        }
    }

    public bool Remove(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        return WithLock(() =>
        {
            var snapshot = Load();
            var entries = snapshot.Entries.Where(entry =>
                !string.Equals(entry.Path, full, StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count == snapshot.Entries.Count) return false;
            Write(entries);
            return true;
        });
    }

    public int RemoveMissing()
    {
        return WithLock(() =>
        {
            var snapshot = Load();
            var entries = snapshot.Entries.Where(entry => entry.Exists).ToList();
            if (entries.Count == snapshot.Entries.Count) return 0;
            Write(entries);
            return snapshot.Entries.Count - entries.Count;
        });
    }

    private void Write(IReadOnlyList<RecentProjectEntry> entries)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = CurrentVersion,
            entries = entries.Select(entry => new
            {
                path = entry.Path,
                displayName = entry.DisplayName,
                lastOpenedUtc = entry.LastOpenedUtc.ToUniversalTime().ToString("o")
            })
        }, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaxBytes) throw new InvalidDataException("最近工程索引超过 64 KiB。");
        AtomicFile.Write(FilePath, stream => stream.Write(bytes));
    }

    private T WithLock<T>(Func<T> action)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        var lockPath = FilePath + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
                return action();
            }
            catch (IOException error) when (attempt < 20 && IsSharingError(error))
            {
                Thread.Sleep(25);
            }
        }
    }

    private static bool IsSharingError(IOException error) =>
        (error.HResult & 0xFFFF) is 5 or 32 or 33;
}
