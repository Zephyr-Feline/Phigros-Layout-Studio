namespace Editor.Formats;

/// <summary>Writes beside the destination and commits only a complete file.</summary>
public static class AtomicFile
{
    private const int MaxCommitAttempts = 5;
    private const int RetryDelayMilliseconds = 25;

    public static void Write(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("A full file path is required.", nameof(path));
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.WriteThrough))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(fullPath)) File.Replace(temp, fullPath, null);
                    else File.Move(temp, fullPath);
                    return;
                }
                catch (IOException error) when (attempt < MaxCommitAttempts - 1 && IsTransientCommitError(error))
                {
                    Thread.Sleep(RetryDelayMilliseconds);
                }
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>
    /// Commits a new file only when the destination does not already exist. The final move is
    /// itself non-overwriting, so a file created after a preflight check is never replaced.
    /// </summary>
    public static void WriteNew(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("A full file path is required.", nameof(path));
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.WriteThrough))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temp, fullPath, overwrite: false);
            }
            catch (IOException error) when (File.Exists(fullPath))
            {
                throw new IOException("The destination already exists; new-file output never overwrites it.", error);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static bool IsTransientCommitError(IOException error)
    {
        if (!OperatingSystem.IsWindows()) return false;
        // Windows sharing/lock violations (32/33), a destination created between the existence
        // check and File.Move (80/183), and the two errors a still-held destination produces for
        // commit itself: access denied (5) and "cannot remove the file to be replaced" (1175).
        // Indexers, anti-virus and backup agents release the destination after a short moment, so
        // these are retried inside the bounded window and reported if they persist. Other IO
        // failures must reach the caller immediately.
        return (error.HResult & 0xFFFF) is 5 or 32 or 33 or 80 or 183 or 1175;
    }
}
