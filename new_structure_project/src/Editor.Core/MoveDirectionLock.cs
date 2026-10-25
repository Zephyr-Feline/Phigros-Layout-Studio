namespace Editor.Core;

public enum MoveAxis { None, Horizontal, Vertical }

/// <summary>Latch the dominant logical drag axis while Shift is held.</summary>
public static class MoveDirectionLock
{
    public static (PointD Delta, MoveAxis Axis) Apply(double deltaX, double deltaY,
        bool shiftHeld, MoveAxis current, double activationDistance)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY) ||
            !double.IsFinite(activationDistance) || activationDistance < 0 ||
            !Enum.IsDefined(current))
            throw new ArgumentOutOfRangeException(nameof(deltaX));
        if (!shiftHeld) return (new PointD(deltaX, deltaY), MoveAxis.None);
        if (current == MoveAxis.None && Math.Max(Math.Abs(deltaX), Math.Abs(deltaY)) >= activationDistance)
            current = Math.Abs(deltaX) >= Math.Abs(deltaY) ? MoveAxis.Horizontal : MoveAxis.Vertical;
        return current switch
        {
            MoveAxis.Horizontal => (new PointD(deltaX, 0), current),
            MoveAxis.Vertical => (new PointD(0, deltaY), current),
            _ => (new PointD(0, 0), MoveAxis.None)
        };
    }
}
