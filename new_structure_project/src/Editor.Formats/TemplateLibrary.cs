using Editor.Core;

namespace Editor.Formats;

public sealed record TemplateLibraryEntry(
    string Id, string Name, IReadOnlyList<RectD> Slots)
{
    public int SlotCount => Slots.Count;
    public override string ToString() => $"{Name}（{SlotCount} 个槽位）";
}

public sealed record TemplateLibraryDiagnostic(string FileName, string Message);

public sealed record TemplateLibrarySnapshot(
    IReadOnlyList<TemplateLibraryEntry> Entries,
    IReadOnlyList<TemplateLibraryDiagnostic> Diagnostics);

public sealed record TemplateLibraryAddResult(TemplateLibraryEntry Entry, bool Added);

/// <summary>
/// A user-owned directory of layout-only templates. Entries use controlled 32-hex file names;
/// the display name is never interpreted as a path. Loading one damaged entry never blocks the
/// remaining entries, and read operations never rewrite user files.
/// </summary>
public sealed class TemplateLibrary
{
    public const int MaxEntries = 200;
    public const string FileExtension = LayoutTemplateStore.Extension;
    public string DirectoryPath { get; }

    public static string DefaultDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhigrosPreviewEditor", "LayoutTemplates");

    public TemplateLibrary(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    public TemplateLibrarySnapshot Load()
    {
        if (!Directory.Exists(DirectoryPath)) return new([], []);
        var entries = new List<TemplateLibraryEntry>();
        var diagnostics = new List<TemplateLibraryDiagnostic>();
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(DirectoryPath, "*" + FileExtension, SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new("", "模板库无法读取：" + error.Message));
            return new(entries, diagnostics);
        }
        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);
            var id = Path.GetFileNameWithoutExtension(path);
            if (!IsControlledId(id))
            {
                diagnostics.Add(new(fileName, "文件名不是受控模板 ID，已跳过。"));
                continue;
            }
            if (entries.Count >= MaxEntries)
            {
                diagnostics.Add(new(fileName, $"模板库超过 {MaxEntries} 项上限，后续文件未加载。"));
                break;
            }
            try
            {
                var template = LayoutTemplateStore.Read(path);
                entries.Add(new(id, template.Name, template.Slots));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                diagnostics.Add(new(fileName, error.Message));
            }
        }
        return new(entries.OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray(), diagnostics);
    }

    public TemplateLibraryAddResult Add(LayoutTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        template.Validate();
        var name = template.Name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Template name is required.");
        var snapshot = Load();
        var duplicate = snapshot.Entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, name, StringComparison.Ordinal) &&
            entry.Slots.SequenceEqual(template.Slots));
        if (duplicate is not null) return new(duplicate, false);
        if (snapshot.Entries.Count >= MaxEntries)
            throw new InvalidDataException($"The template library limit of {MaxEntries} entries has been reached.");
        Directory.CreateDirectory(DirectoryPath);
        string id;
        string path;
        do
        {
            id = Guid.NewGuid().ToString("N");
            path = Path.Combine(DirectoryPath, id + FileExtension);
        } while (File.Exists(path));
        var copy = template with { Id = id, Name = name };
        LayoutTemplateStore.Save(path, copy);
        return new(new TemplateLibraryEntry(id, name, copy.Slots), true);
    }

    public TemplateLibraryAddResult Import(string sourcePath)
    {
        var source = LayoutTemplateStore.Read(sourcePath);
        return Add(source);
    }

    public LayoutTemplate Read(string id)
    {
        var path = ControlledPath(id);
        if (!File.Exists(path)) throw new KeyNotFoundException($"Template {id} was not found.");
        var template = LayoutTemplateStore.Read(path);
        return template with { Id = id };
    }

    public TemplateLibraryEntry Rename(string id, string name)
    {
        var template = Read(id);
        var trimmed = (name ?? "").Trim();
        var renamed = template with { Name = trimmed };
        renamed.Validate();
        if (string.Equals(template.Name, trimmed, StringComparison.Ordinal))
            return new(id, template.Name, template.Slots);
        LayoutTemplateStore.Save(ControlledPath(id), renamed);
        return new(id, renamed.Name, renamed.Slots);
    }

    public void Remove(string id)
    {
        var path = ControlledPath(id);
        if (!File.Exists(path)) throw new KeyNotFoundException($"Template {id} was not found.");
        File.Delete(path);
    }

    public void Export(string id, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var template = Read(id);
        LayoutTemplateStore.Save(destination, template);
    }

    private string ControlledPath(string id)
    {
        if (!IsControlledId(id))
            throw new ArgumentException("A controlled template ID is required.", nameof(id));
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, id + FileExtension));
        var prefix = DirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(path), DirectoryPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Template path escaped the library directory.");
        return path;
    }

    private static bool IsControlledId(string id) =>
        id.Length == 32 && id.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
