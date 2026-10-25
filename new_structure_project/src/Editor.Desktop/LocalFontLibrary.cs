using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Editor.Formats;
using Editor.Rendering;

[assembly: InternalsVisibleTo("Editor.Tests")]

namespace Editor.Desktop;

/// <summary>User-owned local fonts, copied by content hash; no installation or project embedding.</summary>
public sealed class LocalFontLibrary
{
    internal const string VerificationDirectoryVariable = "PPG_INTERNAL_VERIFY_FONT_DIRECTORY";
    private static string? _verificationDirectory;
    private static readonly Lazy<LocalFontLibrary> DefaultLibrary = new(() => new LocalFontLibrary(
        _verificationDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhigrosPreviewEditor", "Fonts")));
    public static LocalFontLibrary Default => DefaultLibrary.Value;
    public string DirectoryPath { get; }
    private readonly List<LocalFontInfo> _fonts = [];
    private readonly List<string> _warnings = [];
    public IReadOnlyList<LocalFontInfo> Fonts => _fonts;
    public IReadOnlyList<string> Warnings => _warnings;
    private const int MaxFonts = 128;
    private const long MaxLibraryBytes = 256L * 1024 * 1024;

    // Only CLI startup calls this, before the lazy library is read. GUI startup keeps its normal profile.
    internal static void ConfigureVerificationDirectory()
    {
        var directory = Environment.GetEnvironmentVariable(VerificationDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory)) return;
        if (DefaultLibrary.IsValueCreated) throw new InvalidOperationException("Font profile was already initialized.");
        _verificationDirectory = Path.GetFullPath(directory);
    }

    public LocalFontLibrary(string directory) : this(directory, LoadVerifiedFont) { }

    internal LocalFontLibrary(string directory, Func<byte[], LocalFontInfo> loadFont)
    {
        ArgumentNullException.ThrowIfNull(loadFont);
        DirectoryPath = Path.GetFullPath(directory);
        if (!Directory.Exists(DirectoryPath)) return;
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(DirectoryPath).Where(IsFontPath)
                .OrderBy(path => path, StringComparer.Ordinal).Take(MaxFonts + 1).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _warnings.Add("Local font library could not be read: " + error.Message);
            return;
        }
        long total = 0;
        foreach (var path in paths)
        {
            try
            {
                var length = new FileInfo(path).Length;
                if (_fonts.Count >= MaxFonts || total + length > MaxLibraryBytes)
                    throw new InvalidDataException("Local font library exceeds its 128-font / 256-MiB limit.");
                var bytes = ReadFont(path);
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), hash, StringComparison.Ordinal))
                    throw new InvalidDataException("Local font file failed its content-hash check.");
                var info = loadFont(bytes);
                if (_fonts.All(font => font.Sha256 != info.Sha256)) _fonts.Add(info);
                total += length;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _warnings.Add($"{Path.GetFileName(path)}: {error.Message}");
            }
        }
    }

    private static LocalFontInfo LoadVerifiedFont(byte[] bytes)
    {
        // Keep native validation even for a face already registered in this process, but
        // only after the owned cache entry passed its inexpensive content-hash check.
        _ = LocalFontRegistry.Inspect(bytes);
        return LocalFontRegistry.Register(bytes);
    }

    public LocalFontInfo Import(string sourcePath)
    {
        if (!IsFontPath(sourcePath)) throw new InvalidDataException("Select a .ttf or .otf file.");
        var bytes = ReadFont(sourcePath);
        var info = LocalFontRegistry.Inspect(bytes);
        var existing = _fonts.FirstOrDefault(font => font.Sha256 == info.Sha256);
        var extension = bytes.AsSpan(0, 4).SequenceEqual("OTTO"u8) ? ".otf" : ".ttf";
        var target = Path.Combine(DirectoryPath, info.Sha256 + extension);
        if (existing is not null && File.Exists(target))
        {
            try { if (ReadFont(target).AsSpan().SequenceEqual(bytes)) return existing; }
            catch (InvalidDataException) { /* Reimport repairs the owned invalid cache file. */ }
            catch (FileNotFoundException) { /* It disappeared after the existence check. */ }
        }
        if (existing is null && _fonts.Count >= MaxFonts) throw new InvalidDataException("Local font library is full (128 fonts).");
        var currentBytes = Directory.Exists(DirectoryPath)
            ? Directory.EnumerateFiles(DirectoryPath).Sum(path => new FileInfo(path).Length) : 0;
        var replacedBytes = File.Exists(target) ? new FileInfo(target).Length : 0;
        if (currentBytes - replacedBytes + bytes.Length > MaxLibraryBytes)
            throw new InvalidDataException("Local font library exceeds 256 MiB.");
        AtomicFile.Write(target, stream => stream.Write(bytes));
        info = LocalFontRegistry.Register(bytes);
        if (existing is null) _fonts.Add(info);
        _warnings.RemoveAll(warning => warning.StartsWith(Path.GetFileName(target) + ": ", StringComparison.OrdinalIgnoreCase));
        return info;
    }

    private static bool IsFontPath(string path) =>
        Path.GetExtension(path).Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".otf", StringComparison.OrdinalIgnoreCase);

    private static byte[] ReadFont(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is < 12 or > LocalFontRegistry.MaxFontBytes)
            throw new InvalidDataException("Select a TTF/OTF font no larger than 32 MiB.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("Font file changed while it was being read.");
        return bytes;
    }
}
