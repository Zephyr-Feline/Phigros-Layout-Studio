using System.Xml.Linq;
using Editor.Formats.Compatibility;

namespace Editor.Desktop;

public sealed record CompatibilityEvidenceFixture(string Id, string Title, string Extension,
    XDocument Xml, CompatibilityErrorKind? ExpectedError = null);

/// <summary>Original generated assets only; stable IDs and explicit V1 fields for repeatability.</summary>
public static class CompatibilityEvidenceFixtures
{
    public static IReadOnlyList<CompatibilityEvidenceFixture> Create()
    {
        var fixtures = new List<CompatibilityEvidenceFixture>();
        var sequence = 0;
        var wide = Image(480, 160, 0);
        var tall = Image(160, 480, 1);
        var exact = Image(480, 270, 2);
        foreach (var (width, height, id) in new[] { (960, 540, "16-9"), (720, 540, "4-3"), (864, 540, "16-10") })
            Add("classic-" + id, "经典模式 · " + id + " · 四难度 / 谱师", ".phipreview",
                Classic(width, height, true, false));
        Add("classic-number-bottom", "经典模式 · 数字在下 / 空谱师隐藏", ".phipreview", Classic(960, 540, false, true));
        var edge = Classic(720, 540, true, false);
        edge.Root!.Element("Labels")!.Elements().First().SetElementValue("BadgeX", 1920);
        edge.Root.Element("Labels")!.Elements().First().SetElementValue("AuthorY", 1080);
        Add("classic-edge", "经典模式 · 4:3 边缘坐标", ".phipreview", edge);

        foreach (var (image, id, title) in new[] { (wide, "wide", "宽图"), (tall, "tall", "高图"), (exact, "exact", "同宽高比") })
        {
            var background = Free(); background.Root!.Add(new XElement("Background", image));
            Add("free-background-" + id, "固定背景中央裁剪 · " + title, ".phifree", background);
        }
        AddFree("free-image-fit", "平直图片 · 旧版等比适配 / V2 拉伸", Layer("Image", 30, 40, 400, 400,
            ("Image", wide), ("Slanted", false), ("KeepAspect", true)));
        AddFree("free-slanted-image", "斜边图片 · 中央裁剪", Layer("Image", 80, 60, 600, 360,
            ("Image", tall), ("Slanted", true)));
        AddFree("free-shapes", "矩形 / 宽斜边 / 窄斜边", Layer("Shape", 40, 40, 400, 100, ("ColorArgb", -3216365)),
            Layer("Shape", 40, 180, 400, 100, ("Slanted", true), ("ColorArgb", -16742339)),
            Layer("Shape", 500, 40, 50, 360, ("Slanted", true), ("ColorArgb", -13606913)));
        AddFree("free-text", "中英文换行 · 字重 / 斜体 / 行距", Layer("Text", 30, 30, 380, 440,
            ("Text", "中文混排 Music Preview\n换行与 line spacing\n长行文字用于观察自动换行的布局差异"),
            ("FontSize", 32), ("LineSpacing", 1.4), ("Bold", true), ("VerticalAlign", "Top")),
            Layer("Text", 440, 30, 480, 440, ("Text", "Italic é ♫\n右侧第二行"), ("FontSize", 38),
                ("Italic", true), ("Align", "Right"), ("VerticalAlign", "Bottom")));
        AddFree("free-text-alignment", "文字 · 三种水平 / 垂直对齐",
            Layer("Text", 30, 30, 270, 440, ("Text", "左 / 顶"), ("VerticalAlign", "Top")),
            Layer("Text", 340, 30, 270, 440, ("Text", "中 / 中"), ("Align", "Center")),
            Layer("Text", 650, 30, 270, 440, ("Text", "右 / 底"), ("Align", "Right"), ("VerticalAlign", "Bottom")));
        AddFree("free-tags", "Tag · 文本 / 空文本 / 半透明色",
            Layer("Tag", 40, 40, 400, 100, ("Text", "谱师 · Charter"), ("ColorArgb", -2133921537)),
            Layer("Tag", 40, 200, 400, 100, ("Text", "")));
        foreach (var height in new[] { 1, 2, 5, 90 })
            AddFree("free-placeholder-" + height, "空曲绘占位 · 高度 " + height,
                Layer("Image", 60, 200, 700, height, ("Text", "导入曲绘")));
        AddFree("free-placeholder-style", "空曲绘占位 · 粗体 / 斜体 / 行距",
            Layer("Image", 60, 120, 700, 180, ("Bold", true), ("Italic", true), ("LineSpacing", 2.5),
                ("VerticalAlign", "Bottom"), ("ColorArgb", -256)));
        foreach (var alpha in new[] { 0, 128 })
        {
            var transparent = Free(); transparent.Root!.Add(new XElement("Background", Image(480, 270, 0, alpha)));
            Add("free-background-alpha-" + alpha, "固定背景透明度 · alpha " + alpha, ".phifree", transparent);
            var classic = Classic(640, 360, false, true);
            classic.Root!.SetElementValue("Background", Image(640, 360, 0, alpha));
            Add("classic-background-alpha-" + alpha, "经典背景透明度 · alpha " + alpha, ".phipreview", classic);
        }
        AddFree("free-badges", "自由布局 · 四难度 / 1×1 难度牌",
            new[] { "EZ", "HD", "IN", "AT" }.Select((difficulty, index) =>
                Layer("Badge", 40 + index * 220, 70, 160, 135, ("Difficulty", difficulty), ("Level", 1 + index * 5)))
                .Append(Layer("Badge", 40, 350, 1, 1)).ToArray());
        AddFree("free-visibility", "图层顺序 · 隐藏 / 锁定 / 分组",
            Layer("Shape", 50, 50, 700, 350, ("ColorArgb", -65536), ("Group", "song-a")),
            Layer("Shape", 100, 100, 600, 300, ("ColorArgb", -16776961), ("Group", "song-a"), ("Locked", true)),
            Layer("Shape", 150, 150, 400, 200, ("ColorArgb", -256), ("Visible", false), ("Group", "song-b")));
        foreach (var count in new[] { 3, 5 })
        {
            var songLayers = Enumerable.Range(0, count).Select(index => Layer("Image", 20 + index * (900 / count),
                100, 880 / count, 280, ("Image", Image(160, 100, index)), ("Group", "song-" + index),
                ("Slanted", true), ("Visible", count != 5 || index != 2))).ToArray();
            var songs = Free(songLayers); songs.Root!.SetElementValue("SoftBackground", true);
            Add("free-soft-" + count, "柔化背景 · " + count + " 曲" + (count == 5 ? " / 中间隐藏" : ""), ".phifree", songs);
        }
        var softFixed = Free(); softFixed.Root!.Add(new XElement("Background", wide));
        softFixed.Root.SetElementValue("SoftBackground", true);
        Add("free-soft-fixed", "固定背景 · 旧版柔化参数", ".phifree", softFixed);
        var unknown = Free(Layer("Shape", 40, 40, 300, 200));
        unknown.Root!.Add(new XElement("FutureSetting", "review required"));
        Add("free-unknown-field", "未知字段 · 显式损失诊断", ".phifree", unknown);
        var future = Free(); future.Root!.SetElementValue("Version", 2);
        Add("reject-future", "拒绝 · 未来版本", ".phifree", future, CompatibilityErrorKind.UnsupportedVersion);
        Add("reject-kind", "拒绝 · 不支持的组件", ".phifree", Free(Layer("Video", 0, 0, 100, 100)), CompatibilityErrorKind.UnsupportedFeature);
        Add("reject-geometry", "拒绝 · 零宽度", ".phifree", Free(Layer("Shape", 0, 0, 0, 100)), CompatibilityErrorKind.InvalidGeometry);
        return fixtures;

        void Add(string id, string title, string extension, XDocument xml, CompatibilityErrorKind? error = null) =>
            fixtures.Add(new(id, title, extension, xml, error));
        void AddFree(string id, string title, params XElement[] layers) => Add(id, title, ".phifree", Free(layers));
        XElement Layer(string kind, double x, double y, double width, double height, params (string Name, object Value)[] fields)
        {
            var layer = new XElement("FreeLayer", new XElement("Id", (++sequence).ToString("x32")),
                new XElement("Name", "证据组件 " + sequence), new XElement("Kind", kind),
                new XElement("X", x), new XElement("Y", y), new XElement("Width", width), new XElement("Height", height),
                new XElement("FontName", "Microsoft YaHei UI"), new XElement("FontSize", 32),
                new XElement("Text", "预览 Preview"), new XElement("Align", "Left"), new XElement("VerticalAlign", "Center"),
                new XElement("LineSpacing", 1), new XElement("Bold", false), new XElement("Italic", false),
                new XElement("Difficulty", "IN"), new XElement("Level", 15), new XElement("ColorArgb", -1),
                new XElement("Visible", true), new XElement("Locked", false), new XElement("Slanted", false),
                new XElement("KeepAspect", true), new XElement("Group", ""));
            foreach (var (name, value) in fields) layer.SetElementValue(name, value);
            return layer;
        }
    }

    private static XDocument Free(params XElement[] layers) => new(new XElement("FreeProject",
        new XElement("Version", 1), new XElement("Width", 960), new XElement("Height", 540),
        new XElement("BackgroundColor", -14468281), new XElement("SoftBackground", false), new XElement("Layers", layers)));

    private static XDocument Classic(int width, int height, bool numberOnTop, bool hideAuthor) => new(new XElement("Project",
        new XElement("Version", 1), new XElement("SourceName", "独立生成兼容样例"), new XElement("NumberOnTop", numberOnTop),
        new XElement("Background", Image(width, height, 0)), new XElement("Labels",
            new[] { "EZ", "HD", "IN", "AT" }.Select((difficulty, index) => new XElement("ChartLabel",
                new XElement("Name", "样例 " + index), new XElement("Difficulty", difficulty), new XElement("Level", 1 + index * 5),
                new XElement("Author", hideAuthor && index < 2 ? "" : "谱师 Charter " + index),
                new XElement("ShowBadge", true), new XElement("ShowAuthor", !hideAuthor || index != 3),
                new XElement("BadgeX", 100 + index * 400), new XElement("BadgeY", 300),
                new XElement("AuthorX", 100 + index * 400), new XElement("AuthorY", 600),
                new XElement("Scale", 1), new XElement("AuthorWidth", 360))))));

    private static string Image(int width, int height, int phase, int alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var at = (y * width + x) * 4;
            pixels[at] = (byte)((x * 255 / width + phase * 53) % 256);
            pixels[at + 1] = (byte)((y * 255 / height + phase * 79) % 256);
            pixels[at + 2] = (byte)(((x / 32 + y / 32) % 2 == 0 ? 230 : 35) + phase % 3 * 7);
            pixels[at + 3] = (byte)alpha;
        }
        return Convert.ToBase64String(ProbeImages.EncodePng(width, height, pixels));
    }
}
