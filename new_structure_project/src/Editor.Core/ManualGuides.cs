namespace Editor.Core;

/// <summary>A session-only horizontal or vertical canvas guide in document coordinates.</summary>
public readonly record struct ManualGuide(bool Vertical, double Position);

public enum ManualGuideAddResult
{
    Added,
    Duplicate,
    OutOfRange,
    LimitReached
}

/// <summary>
/// Runtime guide state shared by rulers, snapping and the canvas. It is deliberately not part
/// of <see cref="EditorDocument"/>: guides never enter project files, history or PNG output.
/// </summary>
public sealed class ManualGuideSet
{
    public const int MaxGuides = 200;
    private const double DuplicateTolerance = 1e-6;
    private readonly List<ManualGuide> _guides = [];

    public event Action? Changed;

    public IReadOnlyList<ManualGuide> Items => _guides;
    public int Count => _guides.Count;

    public ManualGuideAddResult Add(bool vertical, double position,
        double canvasWidth, double canvasHeight)
    {
        var limit = vertical ? canvasWidth : canvasHeight;
        if (!ValidPosition(position, limit)) return ManualGuideAddResult.OutOfRange;
        if (_guides.Any(guide => guide.Vertical == vertical &&
            Math.Abs(guide.Position - position) <= DuplicateTolerance))
            return ManualGuideAddResult.Duplicate;
        if (_guides.Count >= MaxGuides) return ManualGuideAddResult.LimitReached;
        _guides.Add(new ManualGuide(vertical, position));
        Changed?.Invoke();
        return ManualGuideAddResult.Added;
    }

    /// <summary>Moves an existing guide. Invalid or duplicate positions leave it unchanged.</summary>
    public bool Move(int index, double position, double canvasWidth, double canvasHeight)
    {
        if (index < 0 || index >= _guides.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var guide = _guides[index];
        var limit = guide.Vertical ? canvasWidth : canvasHeight;
        if (!ValidPosition(position, limit)) return false;
        if (_guides.Where((_, other) => other != index).Any(other =>
            other.Vertical == guide.Vertical &&
            Math.Abs(other.Position - position) <= DuplicateTolerance))
            return false;
        if (Math.Abs(guide.Position - position) <= DuplicateTolerance) return true;
        _guides[index] = guide with { Position = position };
        Changed?.Invoke();
        return true;
    }

    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= _guides.Count) return false;
        _guides.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    public int Clear()
    {
        if (_guides.Count == 0) return 0;
        var removed = _guides.Count;
        _guides.Clear();
        Changed?.Invoke();
        return removed;
    }

    /// <summary>Drops guides that no longer fit the current canvas after a canvas resize.</summary>
    public int Prune(double canvasWidth, double canvasHeight)
    {
        var before = _guides.Count;
        _guides.RemoveAll(guide =>
            !ValidPosition(guide.Position, guide.Vertical ? canvasWidth : canvasHeight));
        var removed = before - _guides.Count;
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    public IReadOnlyList<double> Positions(bool vertical) =>
        _guides.Where(guide => guide.Vertical == vertical).Select(guide => guide.Position).ToArray();

    private static bool ValidPosition(double position, double limit) =>
        double.IsFinite(position) && double.IsFinite(limit) && limit > 0 &&
        position >= 0 && position <= limit;
}
