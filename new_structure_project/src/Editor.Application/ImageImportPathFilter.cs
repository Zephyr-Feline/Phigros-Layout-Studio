namespace Editor.Application;

public sealed record ImageImportPathSelection(IReadOnlyList<string> Paths, int Ignored);

/// <summary>
/// Normalizes local file-drop candidates without opening or reading them. The batch import
/// workflow remains responsible for image validation, limits, source protection and commit.
/// </summary>
public static class ImageImportPathFilter
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"
    };

    public static ImageImportPathSelection Filter(IEnumerable<string?>? candidates) => Filter(candidates, File.Exists);

    internal static ImageImportPathSelection Filter(IEnumerable<string?>? candidates, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignored = 0;
        foreach (var candidate in candidates ?? [])
        {
            if (string.IsNullOrWhiteSpace(candidate) || IsExplicitNetworkOrUri(candidate))
            {
                ignored++;
                continue;
            }
            string fullPath;
            try { fullPath = Path.GetFullPath(candidate); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                ignored++;
                continue;
            }
            if (IsExplicitNetworkOrUri(fullPath) || !Extensions.Contains(Path.GetExtension(fullPath)) || !fileExists(fullPath))
            {
                ignored++;
                continue;
            }
            if (seen.Add(fullPath)) paths.Add(fullPath);
            else ignored++;
        }
        return new(paths.AsReadOnly(), ignored);
    }

    private static bool IsExplicitNetworkOrUri(string path)
    {
        // File drops accept paths, not URIs. Check before any existence query so an explicit
        // share is never contacted merely to decide whether its extension can be imported.
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            // Preserve Windows extended local drive paths, including long paths. Other
            // UNC/device namespaces are outside the local-file drop contract.
            return !(normalized.Length >= 7 && (normalized[2] is '?' or '.') && normalized[3] == '/' &&
                char.IsAsciiLetter(normalized[4]) && normalized[5] == ':' && normalized[6] == '/');
        }
        if (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':') return false;
        return Uri.TryCreate(path, UriKind.Absolute, out _);
    }
}
