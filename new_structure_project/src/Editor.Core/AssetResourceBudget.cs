namespace Editor.Core;

/// <summary>
/// Admission policy for new encoded resources, not a decoded or whole-process memory limit.
/// 256 MiB leaves room below the native container's 300 MiB limit for its manifest and ZIP.
/// Existing larger documents remain readable and can be edited or reduced without growth.
/// </summary>
public static class AssetResourceBudget
{
    public const long MaxDocumentBytes = 256L * 1024 * 1024;
    public const int MaxAssetBytes = 100 * 1024 * 1024;

    public static long UniqueBytes(IEnumerable<AssetBlob> assets)
    {
        var unique = new Dictionary<string, AssetBlob>(StringComparer.Ordinal);
        long total = 0;
        foreach (var asset in assets)
        {
            ArgumentNullException.ThrowIfNull(asset);
            if (unique.TryGetValue(asset.Sha256, out var existing))
            {
                if (!existing.ContentEquals(asset))
                    throw new InvalidDataException("Asset content-hash collision.");
                continue;
            }
            unique.Add(asset.Sha256, asset);
            total = checked(total + asset.Length);
        }
        return total;
    }

    public static long AddUniqueBytes(long current, int incoming, long maximum)
    {
        if (incoming is <= 0 or > MaxAssetBytes)
            throw new InvalidDataException("Encoded image exceeds the 100 MiB asset limit.");
        if (current < 0 || incoming > maximum - current)
            throw new InvalidDataException("Images exceed the document's encoded resource budget (256 MiB).");
        return checked(current + incoming);
    }

    public static void ValidateChange(IEnumerable<AssetBlob> before, IEnumerable<AssetBlob> after,
        long maximum = MaxDocumentBytes)
    {
        var original = before.ToArray();
        var candidate = after.ToArray();
        var allowed = Math.Max(maximum, UniqueBytes(original));
        if (UniqueBytes(candidate) > allowed)
            throw new InvalidDataException("Images exceed the document's encoded resource budget (256 MiB).");
        foreach (var asset in candidate.Where(asset => asset.Length > MaxAssetBytes))
            if (!original.Any(existing => existing.ContentEquals(asset)))
                throw new InvalidDataException("Encoded image exceeds the 100 MiB asset limit.");
    }
}
