namespace Editor.Application;

/// <summary>Local path comparison including symbolic-link/junction ancestors. No file content is read.</summary>
public static class FilePathIdentity
{
    /// <summary>Bind before reading/validating/rendering; recheck before committing editor state
    /// or an output file. This detects changed link targets, not concurrent content edits.</summary>
    public static Action PrepareTargetCheck(string path)
    {
        var full = Normalize(path);
        var target = Resolve(full);
        return () => EnsureTarget(full, target);
    }

    internal static void EnsureTarget(string path, string target)
    {
        if (!string.Equals(Resolve(path), target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected file/output path now points to another location. Restore the original path or choose the file again.");
    }

    public static bool AreSame(string left, string right)
    {
        var a = Normalize(left); var b = Normalize(right);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Resolve(a), Resolve(b), StringComparison.OrdinalIgnoreCase);
    }

    internal static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) full = @"\\" + full[8..];
            else if (full.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) full = full[4..];
        }
        return full;
    }

    internal static string Resolve(string path)
    {
        var full = Normalize(path);
        var followed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var root = Path.GetPathRoot(full)!;
            var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var restart = false;
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { return full; }
                catch (DirectoryNotFoundException) { return full; }
                if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(current) : new FileInfo(current);
                var target = info.ResolveLinkTarget(returnFinalTarget: false);
                if (target is null) continue; // Other reparse types (such as cloud placeholders) are not links.
                if (!followed.Add(current) || followed.Count > 63)
                    throw new IOException("The selected path has a symbolic-link or directory-junction cycle.");
                full = Normalize(Path.Combine([target.FullName, .. parts[(i + 1)..]]));
                restart = true;
                break;
            }
            if (!restart) return full;
        }
    }
}
