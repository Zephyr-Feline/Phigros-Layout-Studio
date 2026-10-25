using System.IO;

namespace Editor.Formats;

/// <summary>
/// Public file-naming rules for the native package (DEC-008). The extension is a user-facing file
/// name convention only: the container is identified by its ZIP layout, <c>formatId</c> and
/// <c>schemaVersion</c>, never by the name a file happens to carry.
/// </summary>
public static class NativeProjectFile
{
    /// <summary>Public project extension frozen by DEC-008.</summary>
    public const string Extension = ".ppgproj";

    /// <summary>Extension without the dot, for pickers that expect a bare default extension.</summary>
    public const string ExtensionName = "ppgproj";

    /// <summary>Filter pattern for the platform file pickers.</summary>
    public const string FilterPattern = "*.ppgproj";

    public static bool HasExtension(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFileName(path).EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Appends the public extension when the chosen name carries none of its own. A name that
    /// already ends with <c>.ppgproj</c> in any casing is returned untouched, and a deliberate
    /// different extension is left for the user instead of being silently rewritten.
    /// </summary>
    public static string EnsureExtension(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var name = Path.GetFileName(path);
        if (name.Length == 0)
            throw new ArgumentException("A file name is required.", nameof(path));
        var trimmed = name.TrimEnd('.');
        if (trimmed.Length == 0)
            throw new ArgumentException("A file name is required.", nameof(path));
        var extension = Path.GetExtension(trimmed);
        if (extension.Length == 0) return Combine(path, trimmed + Extension);
        return path;
    }

    private static string Combine(string path, string name)
    {
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }
}
