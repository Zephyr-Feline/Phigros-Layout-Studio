namespace Editor.Core;

/// <summary>Asset-free placement slots expressed as fractions of the persisted canvas.</summary>
public sealed record LayoutTemplate(string Id, string Name, IReadOnlyList<RectD> Slots)
{
    public override string ToString() => Name;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80 || Id.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || Name.Any(char.IsControl))
            throw new InvalidOperationException("Template ID/name is missing or too long (80/120 characters maximum).");
        if (Slots is null || Slots.Count is < 1 or > 200)
            throw new InvalidOperationException("A layout template needs between 1 and 200 slots.");
        foreach (var slot in Slots)
            if (!double.IsFinite(slot.X) || !double.IsFinite(slot.Y) ||
                !double.IsFinite(slot.Width) || !double.IsFinite(slot.Height) ||
                slot.X < 0 || slot.Y < 0 || slot.Width <= 0 || slot.Height <= 0 ||
                slot.X + slot.Width > 1 + 1e-12 || slot.Y + slot.Height > 1 + 1e-12)
                throw new InvalidOperationException("Template slots must have positive size and fit inside the normalized canvas.");
    }
}
