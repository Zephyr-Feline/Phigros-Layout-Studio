using System.Security.Cryptography;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Editor.Rendering;

public sealed record LocalFontInfo(string Family, string Reference, string Sha256, string Face)
{
    public override string ToString() => Family + " — " + Face;
}

/// <summary>Process-local font registration shared by preview and export. No path comes from a project.</summary>
public static class LocalFontRegistry
{
    public const string Prefix = "fonts:ppg-local/";
    public const string Fallback = "Microsoft YaHei UI";
    public const int MaxFontBytes = 32 * 1024 * 1024;
    private static readonly Dictionary<string, LocalFontInfo> Registered = new(StringComparer.Ordinal);

    private sealed class Collection : FontCollectionBase
    {
        public override Uri Key { get; }
        public GlyphTypeface Glyph { get; }
        public Collection(string hash, byte[] bytes)
        {
            Key = new Uri(Prefix + hash);
            using var stream = new MemoryStream(bytes, writable: false);
            if (!TryAddGlyphTypeface(stream, out var glyph) || glyph is null)
                throw new InvalidDataException("The selected file is not a usable TTF/OTF font.");
            Glyph = glyph;
        }
    }

    public static LocalFontInfo Inspect(byte[] bytes)
    {
        var hash = CheckBytes(bytes);
        var collection = new Collection(hash, bytes);
        try { return Describe(collection, hash); }
        finally { collection.Glyph.Dispose(); }
    }

    public static LocalFontInfo Register(byte[] bytes)
    {
        var hash = CheckBytes(bytes);
        if (Registered.TryGetValue(hash, out var existing)) return existing;
        var collection = new Collection(hash, bytes);
        try
        {
            var info = Describe(collection, hash);
            FontManager.Current.AddFontCollection(collection);
            Registered.Add(hash, info);
            return info;
        }
        catch { collection.Glyph.Dispose(); throw; }
    }

    public static bool IsMissing(string family) => family.StartsWith(Prefix, StringComparison.Ordinal) &&
        !Registered.Values.Any(info => info.Reference == family);

    public static string Resolve(string family) => IsMissing(family) ? Fallback : family;

    public static string DisplayName(string family)
    {
        if (!family.StartsWith(Prefix, StringComparison.Ordinal)) return family;
        var known = Registered.Values.FirstOrDefault(info => info.Reference == family);
        if (known is not null) return known + " (local)";
        var name = family.Contains('#') ? family[(family.IndexOf('#') + 1)..] : "Font";
        return name + " (local, unavailable)";
    }

    private static string CheckBytes(byte[] bytes)
    {
        if (bytes.Length is < 12 or > MaxFontBytes ||
            !(bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 1, 0, 0 }) ||
              bytes.AsSpan(0, 4).SequenceEqual("OTTO"u8)))
            throw new InvalidDataException("Select a TTF/OTF font no larger than 32 MiB.");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static LocalFontInfo Describe(Collection collection, string hash)
    {
        var family = collection.Glyph.FamilyName;
        var reference = Prefix + hash + "#" + family;
        if (string.IsNullOrWhiteSpace(family) || family.Contains('#') || reference.Length > 200)
            throw new InvalidDataException("The font family name cannot be stored in this project.");
        return new LocalFontInfo(family, reference, hash,
            collection.Glyph.FaceNames.Values.FirstOrDefault() ?? collection.Glyph.Style.ToString());
    }
}
