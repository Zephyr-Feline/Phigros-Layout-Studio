using Avalonia.Media.Imaging;
using Editor.Core;

namespace Editor.Rendering;

/// <summary>One preview's decoded bitmaps, bounded by estimated BGRA bytes and entry count.
/// Use on its drawing thread. Neither bitmaps nor cache state enter a document or history.</summary>
public sealed class DecodedImageCache : IDisposable
{
    public const long DefaultPixelBudget = 128L * 1024 * 1024;
    public const int DefaultEntryLimit = 64;
    private sealed record Entry(Bitmap Bitmap, long Bytes, LinkedListNode<string> Node);
    private readonly Dictionary<string, Entry> _images = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();
    private readonly long _pixelBudget;
    private readonly int _entryLimit;
    private AssetImageDecoder? _decoder;
    private bool _disposed;
    public int Count => _images.Count;
    public long EstimatedPixelBytes { get; private set; }

    public DecodedImageCache(long pixelBudget = DefaultPixelBudget, int entryLimit = DefaultEntryLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pixelBudget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryLimit);
        _pixelBudget = pixelBudget;
        _entryLimit = entryLimit;
    }

    /// <summary>Retire pixels that this frame no longer draws, including replaced/hidden artwork.</summary>
    public void Retain(IEnumerable<AssetBlob> assets, AssetImageDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(assets);
        EnsureDecoder(decoder);
        var used = assets.Select(asset => asset.Sha256).ToHashSet(StringComparer.Ordinal);
        foreach (var hash in _images.Keys.Where(hash => !used.Contains(hash)).ToArray()) Remove(hash);
    }

    /// <summary>Synchronously draw with a borrowed bitmap; the callback must not dispose or retain it.</summary>
    public void Use(AssetBlob asset, AssetImageDecoder decoder, Action<Bitmap> draw)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(draw);
        EnsureDecoder(decoder);
        if (_images.TryGetValue(asset.Sha256, out var entry))
        {
            _recent.Remove(entry.Node);
            _recent.AddLast(entry.Node);
            draw(entry.Bitmap);
            return;
        }
        var bitmap = decoder.Decode(asset);
        var bytes = checked((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);
        if (bytes > _pixelBudget)
        {
            // Large images retain their original pixels and the former per-draw lifetime.
            using (bitmap) draw(bitmap);
            return;
        }
        while (_images.Count >= _entryLimit || EstimatedPixelBytes + bytes > _pixelBudget)
            Remove(_recent.First!.Value);
        var node = _recent.AddLast(asset.Sha256);
        _images.Add(asset.Sha256, new(bitmap, bytes, node));
        EstimatedPixelBytes += bytes;
        draw(bitmap);
    }

    public void Clear()
    {
        foreach (var entry in _images.Values) entry.Bitmap.Dispose();
        _images.Clear();
        _recent.Clear();
        EstimatedPixelBytes = 0;
        _decoder = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
    }

    private void EnsureDecoder(AssetImageDecoder decoder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(decoder);
        if (!ReferenceEquals(_decoder, decoder)) { Clear(); _decoder = decoder; }
    }

    private void Remove(string hash)
    {
        var entry = _images[hash];
        _images.Remove(hash);
        _recent.Remove(entry.Node);
        EstimatedPixelBytes -= entry.Bytes;
        entry.Bitmap.Dispose();
    }
}
