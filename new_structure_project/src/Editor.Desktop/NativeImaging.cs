using Editor.Core;
using Editor.Application;
using Editor.Formats.Compatibility;
using Editor.Imaging.Windows;
using Editor.Rendering;

namespace Editor.Desktop;

/// <summary>
/// Process-wide image decoder. Skia (Avalonia) handles PNG/JPEG/BMP; the Windows imaging stack adds
/// the TIFF support required by REQ-ASSET-001 (DEC-014).
/// </summary>
internal static class NativeImaging
{
    public static AssetImageDecoder Decoder { get; } = new(new WicTiffImageDecoder());

    /// <summary>Compatibility mapping options: the classic canvas needs real background dimensions.</summary>
    public static CompatibilityOptions Compatibility { get; } = new()
    {
        ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions,
        ProbeImageOpacity = asset => ImageAssetValidator.IsOpaque(asset, Decoder)
    };

    public static void ValidateImages(EditorDocument document) =>
        ImageAssetValidator.Validate(document, Decoder);

    public static async Task<LoadedImage> LoadImageAsync(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is <= 0 or > ImageAssetValidator.MaxEncodedBytes)
            throw new InvalidDataException($"Image {Path.GetFileName(path)} must be nonempty and no larger than 100 MiB.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes);
        if (input.ReadByte() != -1) throw new IOException("Image file changed while it was being read.");
        var blob = new AssetBlob(bytes);
        try { ImageAssetValidator.Validate(blob, Decoder); }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException($"Image {Path.GetFileName(path)}: {error.Message}", error);
        }
        using var bitmap = Decoder.Decode(blob);
        return new(blob, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
    }
}
