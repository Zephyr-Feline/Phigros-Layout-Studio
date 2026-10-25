namespace Editor.Application;

/// <summary>
/// Protects a session's imported file paths and their link/junction targets from save/export
/// overwrite (<c>REQ-DOC-004</c>). Retains both the chosen path and its resolved
/// link/junction target at import time. Comparison is case-insensitive for the Windows product.
/// </summary>
public sealed class ProtectedSourcePaths
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _targets = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _paths.Count;

    public void Add(string path) => PrepareAdd(path)();

    /// <summary>Resolve before an import mutates the editor; invoke the returned registration only
    /// after success. Registration is idempotent and performs no filesystem IO.</summary>
    public Action PrepareAdd(string path)
    {
        var full = FilePathIdentity.Normalize(path);
        var target = FilePathIdentity.Resolve(full);
        return () => { _paths.Add(full); _targets.Add(target); };
    }

    public void Clear() { _paths.Clear(); _targets.Clear(); }

    public bool Contains(string path)
    {
        var full = FilePathIdentity.Normalize(path);
        return _paths.Contains(full) || _targets.Contains(FilePathIdentity.Resolve(full));
    }

    /// <summary>Throws when the chosen output path is one of the imported sources.</summary>
    public void EnsureNotProtected(string path)
    {
        if (Contains(path))
            throw new InvalidOperationException(
                "Choose another output path; this file is an imported source.");
    }
}
