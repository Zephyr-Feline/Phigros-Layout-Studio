using System.Security.Cryptography;

namespace Editor.Core;

/// <summary>Owned, immutable asset payload. Snapshots share this object, never its mutable input.</summary>
public sealed class AssetBlob
{
    private readonly byte[] _bytes;

    public int Length => _bytes.Length;
    public string Sha256 { get; }

    public AssetBlob(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) throw new ArgumentException("Asset bytes must be nonempty.", nameof(bytes));
        _bytes = (byte[])bytes.Clone();
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(_bytes));
    }

    public Stream OpenRead() => new MemoryStream(_bytes, writable: false);
    public void WriteTo(Stream destination) => destination.Write(_bytes);
    public byte[] ToArray() => (byte[])_bytes.Clone();

    public bool ContentEquals(AssetBlob? other) => other is not null &&
        Sha256 == other.Sha256 && _bytes.AsSpan().SequenceEqual(other._bytes);
}
