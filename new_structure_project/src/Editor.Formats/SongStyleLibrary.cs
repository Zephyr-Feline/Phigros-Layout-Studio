using Editor.Core;

namespace Editor.Formats;

public sealed record SongStyleLibraryEntry(string Id, SongStyle Style)
{
    public string Name => Style.Name;
    public int LayerCount => Style.Layers.Count;
    public override string ToString() =>
        $"{Name}（{LayerCount} 个角色图层 · {(Id.Length >= 8 ? Id[..8] : Id)}）";
}

public sealed record SongStyleLibraryDiagnostic(string FileName, string Message);

public sealed record SongStyleLibrarySnapshot(
    IReadOnlyList<SongStyleLibraryEntry> Entries,
    IReadOnlyList<SongStyleLibraryDiagnostic> Diagnostics);

public sealed record SongStyleLibraryAddResult(SongStyleLibraryEntry Entry, bool Added);

/// <summary>
/// A user-owned directory of content-free song styles. Display names cannot select paths, and a
/// damaged or future entry is isolated without rewriting any user file.
/// </summary>
public sealed class SongStyleLibrary
{
    public const int MaxEntries = 200;
    public const string FileExtension = SongStyleStore.Extension;
    public string DirectoryPath { get; }

    public static string DefaultDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhigrosPreviewEditor", "SongStyles");

    public SongStyleLibrary(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    public SongStyleLibrarySnapshot Load()
    {
        if (!Directory.Exists(DirectoryPath)) return new([], []);
        var entries = new List<SongStyleLibraryEntry>();
        var diagnostics = new List<SongStyleLibraryDiagnostic>();
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(DirectoryPath, "*" + FileExtension,
                SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new("", "歌曲样式库无法读取：" + error.Message));
            return new(entries, diagnostics);
        }
        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);
            var id = Path.GetFileNameWithoutExtension(path);
            if (!IsControlledId(id))
            {
                diagnostics.Add(new(fileName, "文件名不是受控歌曲样式 ID，已跳过。"));
                continue;
            }
            if (entries.Count >= MaxEntries)
            {
                diagnostics.Add(new(fileName, $"歌曲样式库超过 {MaxEntries} 项上限，后续文件未加载。"));
                break;
            }
            try
            {
                var style = SongStyleStore.Read(path);
                if (!string.Equals(style.Id, id, StringComparison.Ordinal))
                {
                    diagnostics.Add(new(fileName, "文件内样式 ID 与受控文件名不一致，已跳过。"));
                    continue;
                }
                entries.Add(new(id, style));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                diagnostics.Add(new(fileName, error.Message));
            }
        }
        return new(entries.OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray(), diagnostics);
    }

    public SongStyleLibraryAddResult Add(SongStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();
        var name = style.Name.Trim();
        if (name.Length == 0) throw new InvalidDataException("歌曲样式名称不能为空。");
        var snapshot = Load();
        var duplicate = snapshot.Entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, name, StringComparison.Ordinal) &&
            entry.Style.ContentEquals(style));
        if (duplicate is not null) return new(duplicate, false);
        if (snapshot.Entries.Count >= MaxEntries)
            throw new InvalidDataException($"歌曲样式库已达到 {MaxEntries} 项上限。");
        Directory.CreateDirectory(DirectoryPath);
        string id;
        string path;
        do
        {
            id = Guid.NewGuid().ToString("N");
            path = Path.Combine(DirectoryPath, id + FileExtension);
        } while (File.Exists(path));
        var copy = style with { Id = id, Name = name };
        SongStyleStore.Save(path, copy);
        return new(new SongStyleLibraryEntry(id, copy), true);
    }

    public SongStyle Read(string id)
    {
        var path = ControlledPath(id);
        if (!File.Exists(path)) throw new KeyNotFoundException("找不到所选歌曲样式。");
        var style = SongStyleStore.Read(path);
        if (!string.Equals(style.Id, id, StringComparison.Ordinal))
            throw new InvalidDataException("歌曲样式 ID 与文件名不一致。");
        return style;
    }

    public SongStyleLibraryEntry Rename(string id, string name)
    {
        var style = Read(id);
        var trimmed = (name ?? "").Trim();
        var renamed = style with { Name = trimmed };
        renamed.Validate();
        if (string.Equals(style.Name, trimmed, StringComparison.Ordinal))
            return new(id, style);
        SongStyleStore.Save(ControlledPath(id), renamed);
        return new(id, renamed);
    }

    public void Remove(string id)
    {
        var path = ControlledPath(id);
        if (!File.Exists(path)) throw new KeyNotFoundException("找不到所选歌曲样式。");
        File.Delete(path);
    }

    private string ControlledPath(string id)
    {
        if (!IsControlledId(id))
            throw new ArgumentException("歌曲样式 ID 无效。", nameof(id));
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, id + FileExtension));
        var prefix = DirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(path), DirectoryPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("歌曲样式路径超出库目录，已拒绝。");
        return path;
    }

    private static bool IsControlledId(string id) =>
        id.Length == 32 && id.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
