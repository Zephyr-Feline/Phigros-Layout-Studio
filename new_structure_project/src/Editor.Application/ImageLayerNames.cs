namespace Editor.Application;

/// <summary>Automatic layer names only; the full source path remains the asset identity.</summary>
public static class ImageLayerNames
{
    public const int MaxLength = 200;

    public static string FromPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length <= MaxLength) return name;
        var length = MaxLength;
        // Keep a complete UTF-16 surrogate pair at the model boundary.
        if (char.IsHighSurrogate(name[length - 1]) && char.IsLowSurrogate(name[length])) length--;
        return name[..length];
    }
}
