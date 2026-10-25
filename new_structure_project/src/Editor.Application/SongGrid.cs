using Editor.Core;

namespace Editor.Application;

/// <summary>Configurable grid over the existing six-layer song card; margin/gap are canvas units.</summary>
public sealed record SongGridSettings(
    int Rows, int Columns, double MarginX = 40, double MarginY = 40,
    double GapX = 24, double GapY = 24)
{
    public int Count => checked(Rows * Columns);

    public void Validate(int canvasWidth, int canvasHeight)
    {
        if (Rows is < 1 or > 20 || Columns is < 1 or > 20 || Count is < 1 or > 20)
            throw new InvalidDataException("歌曲网格需要 1–20 个单元格，行列各为 1–20。");
        if (!double.IsFinite(MarginX) || !double.IsFinite(MarginY) ||
            !double.IsFinite(GapX) || !double.IsFinite(GapY) ||
            MarginX < 0 || MarginY < 0 || GapX < 0 || GapY < 0)
            throw new InvalidDataException("歌曲网格的边距和间距必须是非负有限数字。");
        if (cellWidth(canvasWidth) <= 0 || cellHeight(canvasHeight) <= 0)
            throw new InvalidDataException("当前画布不足以容纳所选行列、边距和间距。");

        double cellWidth(int width) =>
            (width - 2 * MarginX - (Columns - 1) * GapX) / Columns;
        double cellHeight(int height) =>
            (height - 2 * MarginY - (Rows - 1) * GapY) / Rows;
    }
}

public static class SongGrid
{
    public static EditorDocument Create(int canvasWidth, int canvasHeight,
        IReadOnlyList<SongArtwork> artwork, SongGridSettings settings)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate(canvasWidth, canvasHeight);
        if (artwork.Count != settings.Count)
            throw new InvalidOperationException(
                $"网格需要 {settings.Count} 张曲绘，当前选择了 {artwork.Count} 张。");
        var (document, images) = SongBatch.CreateCards(canvasWidth, canvasHeight, artwork);
        LayoutTemplates.Apply(document, images, CreateTemplate(canvasWidth, canvasHeight, settings));
        document.Validate();
        return document;
    }

    /// <summary>Normalized slots for the settings preview; the original templates remain unchanged.</summary>
    public static LayoutTemplate CreateTemplate(int canvasWidth, int canvasHeight, SongGridSettings settings)
    {
        settings.Validate(canvasWidth, canvasHeight);
        var cellWidth = (canvasWidth - 2 * settings.MarginX - (settings.Columns - 1) * settings.GapX) /
            settings.Columns;
        var cellHeight = (canvasHeight - 2 * settings.MarginY - (settings.Rows - 1) * settings.GapY) /
            settings.Rows;
        var slots = new List<RectD>(settings.Count);
        for (var row = 0; row < settings.Rows; row++)
        for (var column = 0; column < settings.Columns; column++)
        {
            var x = settings.MarginX + column * (cellWidth + settings.GapX);
            var y = settings.MarginY + row * (cellHeight + settings.GapY);
            slots.Add(new RectD(x / canvasWidth, y / canvasHeight,
                cellWidth / canvasWidth, cellHeight / canvasHeight));
        }
        var template = new LayoutTemplate(
            $"grid-{settings.Rows}x{settings.Columns}", $"{settings.Rows} × {settings.Columns} 网格",
            slots.AsReadOnly());
        template.Validate();
        return template;
    }
}
