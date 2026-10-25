using System.Globalization;
using System.Xml.Linq;
using Editor.Formats;
using Editor.Core;
using Editor.Formats.Compatibility;
using Editor.Rendering;

namespace Editor.Desktop;

internal static class LegacyProbe
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        // This probe owns its fixture; the native independence probe is free to delete its own source.
        var image = ProbeImages.EncodePng(1440, 1080, ProbeImages.SyntheticBgra(1440, 1080));
        var base64 = Convert.ToBase64String(image);

        var classicPath = Path.Combine(directory, "representative-classic.phipreview");
        var classicXml = new XDocument(new XElement("Project",
            new XElement("Version", 1), new XElement("SourceName", "P1 sample"),
            new XElement("Background", base64),
            new XElement("Labels", new XElement("ChartLabel",
                new XElement("Name", "自制曲目"), new XElement("Difficulty", "IN"),
                new XElement("Level", 15), new XElement("Author", "谱师甲"),
                new XElement("BadgeX", 1450), new XElement("BadgeY", 800),
                new XElement("AuthorX", 1000), new XElement("AuthorY", 900),
                new XElement("Scale", 1), new XElement("AuthorWidth", 360)))));
        classicXml.Save(classicPath);
        var classic = CompatibilityService.Import(classicPath, NativeImaging.Compatibility);
        NativeImaging.ValidateImages(classic.Document);
        var badge = classic.Document.Layers.Single(layer => layer.Kind == LayerKind.Badge);
        var plate = classic.Document.Layers.Single(layer => layer.Kind == LayerKind.Shape);
        var prose = classic.Document.Layers.Single(layer => layer.Kind == LayerKind.Text);
        if (badge.Bounds != new RectD(1087.5, 800, 128, 108) || prose.Text != "谱师甲" ||
            classic.Document.Layers.IndexOf(plate) >= classic.Document.Layers.IndexOf(prose) ||
            classic.Document.FixedBackgroundAssetId is not Guid background ||
            !classic.Document.Assets[background].ToArray().SequenceEqual(image) ||
            !classic.Report.Diagnostics.Any(item => item.Code == "LEGACY_CLASSIC_BADGE_STYLE_DEVIATION"))
            throw new InvalidDataException("Classic semantic roles, order, geometry or diagnostics changed.");
        var expected = PngExporter.Render(classic.Document, decoder: NativeImaging.Decoder);
        var migrated = Path.Combine(directory, "legacy-probe.ppgproj");
        NativePackageStore.Save(migrated, classic.Document, "p3-probe");
        File.Delete(classicPath);
        var reopened = NativePackageStore.Read(migrated);
        if (!PngExporter.Render(reopened, decoder: NativeImaging.Decoder).SequenceEqual(expected))
            throw new InvalidDataException("Legacy probe migration depends on its deleted source.");
        File.WriteAllBytes(Path.Combine(directory, "classic-render.png"), expected);

        var freePath = Path.Combine(directory, "representative-free.phifree");
        var freeXml = new XDocument(new XElement("FreeProject",
            new XElement("Version", 1), new XElement("Width", 2740), new XElement("Height", 1080),
            new XElement("Layers",
                new XElement("FreeLayer", new XElement("Id", Guid.NewGuid().ToString("N")),
                    new XElement("Kind", "Image"), new XElement("X", 50), new XElement("Y", 50),
                    new XElement("Width", 500), new XElement("Height", 312.5.ToString(CultureInfo.InvariantCulture)),
                    new XElement("Image", base64)),
                new XElement("FreeLayer", new XElement("Id", Guid.NewGuid().ToString("N")),
                    new XElement("Kind", "Text"), new XElement("X", 90), new XElement("Y", 420),
                    new XElement("Width", 600), new XElement("Height", 120),
                    new XElement("Text", "中文 English"), new XElement("FontSize", 48),
                    new XElement("Align", "Center"), new XElement("LineSpacing", 1.25),
                    new XElement("FutureField", "must warn")),
                new XElement("FreeLayer", new XElement("Id", Guid.NewGuid().ToString("N")),
                    new XElement("Kind", "Badge"), new XElement("X", 800), new XElement("Y", 190),
                    new XElement("Width", 256), new XElement("Height", 216),
                    new XElement("Difficulty", "HD"), new XElement("Level", 12)))));
        freeXml.Save(freePath);
        var free = LegacyReader.Read(freePath);
        if (!free.Supported || free.Document!.Layers.Count != 3 ||
            free.Document.Layers[1].Text != "中文 English" ||
            free.Document.Layers[2].Difficulty != "HD" ||
            free.Diagnostics.All(message => !message.Contains("FutureField", StringComparison.Ordinal)))
            throw new InvalidDataException("Free representative read failed: " + string.Join(" | ", free.Diagnostics));
        File.WriteAllBytes(Path.Combine(directory, "free-render.png"),
            PngExporter.Render(free.Document));

        var unsupportedPath = Path.Combine(directory, "unsupported-free.phifree");
        freeXml.Descendants("Kind").Last().Value = "Star";
        freeXml.Save(unsupportedPath);
        var unsupported = LegacyReader.Read(unsupportedPath);
        if (unsupported.Supported || unsupported.Diagnostics.All(x => !x.Contains("Star", StringComparison.Ordinal)))
            throw new InvalidDataException("Unsupported legacy Kind was not diagnosed.");

        Console.WriteLine("LEGACY PROBE PASS: classic and free synthetic fixtures, valid embedded image, key fields, unknown-field warning, unsupported-kind rejection.");
        return 0;
    }
}
