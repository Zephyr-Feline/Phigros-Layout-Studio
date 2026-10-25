using System.Text;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Formats.Compatibility;
using Editor.Rendering;

namespace Editor.Desktop;

/// <summary>
/// Headless P3 compatibility probe: legacy XML -> parse -> migrate -> validate -> save .ppgproj ->
/// delete the legacy sources -> reopen -> render, plus an optional sweep over a real legacy corpus.
/// </summary>
internal static class CompatibilityProbe
{
    public static int Run(string directory, string? corpusDirectory)
    {
        Directory.CreateDirectory(directory);
        var classic = RunClassic(directory);
        var free = RunFree(directory);
        var corpus = corpusDirectory is null ? "" : SweepCorpus(corpusDirectory);
        Console.WriteLine($"COMPATIBILITY PROBE PASS: classic {classic}, free {free}{corpus}");
        return 0;
    }

    private static string RunClassic(string directory)
    {
        var background = ProbeImages.EncodePng(1440, 1080, ProbeImages.SyntheticBgra(1440, 1080));
        var path = Path.Combine(directory, "legacy 4比3 classic.phipreview");
        File.WriteAllText(path, ClassicFixture(background), new UTF8Encoding(false));
        var before = File.ReadAllBytes(path);

        var import = Import(path);
        var document = import.Document;
        var badge = document.Layers.First(layer => layer.Kind == LayerKind.Badge);
        // 4:3 background: 1450 * 1440 / 1920 = 1087.5, uniform scale 1080/1080 = 1, badge 128 x 108
        if (document.CanvasWidth != 1440 || document.CanvasHeight != 1080)
            throw new InvalidDataException("Classic canvas was not mapped to the background size.");
        if (Math.Abs(badge.Bounds.X - 1087.5) > 1e-6 || Math.Abs(badge.Bounds.Width - 128) > 1e-6 ||
            Math.Abs(badge.Bounds.Height - 108) > 1e-6)
            throw new InvalidDataException($"Classic badge geometry is wrong: {badge.Bounds}.");
        var author = document.Layers.First(layer => layer.Kind == LayerKind.Text);
        if (author.Text != "谱师甲" || Math.Abs(author.FontSize - 25) > 1e-6)
            throw new InvalidDataException("Classic author text was not mapped.");
        if (import.Report.Dimensions.Count != 4 ||
            import.Report.Dimensions.Last().Status != DimensionStatus.NotVerified)
            throw new InvalidDataException("Compatibility report is missing a dimension.");

        if (!File.ReadAllBytes(path).SequenceEqual(before))
            throw new InvalidDataException("The legacy source file was modified.");
        var exported = Migrate(import, path, Path.Combine(directory, "classic project"));
        Console.WriteLine($"CLASSIC COMPAT PASS: {import.Report.Summary}");
        foreach (var line in import.Report.Lines) Console.WriteLine($"  {line}");
        return exported;
    }

    private static string RunFree(string directory)
    {
        var image = ProbeImages.EncodePng(400, 200, ProbeImages.SyntheticBgra(400, 200));
        var path = Path.Combine(directory, "legacy free 工程.phifree");
        File.WriteAllText(path, FreeFixture(image), new UTF8Encoding(false));
        var import = Import(path);
        var document = import.Document;
        if (document.CanvasWidth != 800 || document.CanvasHeight != 400)
            throw new InvalidDataException("Free canvas was not mapped.");
        var imageBlobs = document.Layers.Where(layer => layer.Kind == LayerKind.Image)
            .Select(layer => document.Assets[layer.AssetId!.Value]).ToList();
        if (imageBlobs.Count != 2 || !ReferenceEquals(imageBlobs[0], imageBlobs[1]))
            throw new InvalidDataException("Identical legacy images do not share one immutable payload.");
        var badge = document.Layers.First(layer => layer.Kind == LayerKind.Badge);
        if (badge.Bounds.Width < 8 || Math.Abs(badge.Bounds.Width / badge.Bounds.Height - 128.0 / 108.0) > 1e-9)
            throw new InvalidDataException("Tiny legacy badge was not canonicalised.");
        var exported = Migrate(import, path, Path.Combine(directory, "free project"));
        Console.WriteLine($"FREE COMPAT PASS: {import.Report.Summary}");
        return exported;
    }

    /// <summary>Import, validate, write the native project, delete the legacy sources and re-render.</summary>
    private static string Migrate(CompatibilityImport import, string legacyPath, string projectPath)
    {
        ImageAssetValidator.Validate(import.Document, NativeImaging.Decoder);
        var session = new EditSession(new EditorDocument());
        var workflow = new NativeProjectWorkflow(session, NativeImaging.ValidateImages, "p3-probe");
        workflow.ImportConverted(import.Document);
        if (!workflow.IsDirty || workflow.CurrentPath is not null)
            throw new InvalidDataException("A legacy import must start as an unsaved native document.");
        var expected = PngExporter.Render(session.Document, decoder: NativeImaging.Decoder);
        var target = NativeProjectFile.EnsureExtension(projectPath);
        workflow.SaveAs(target);
        File.Delete(legacyPath);
        var reopened = new NativeProjectWorkflow(new EditSession(new EditorDocument()),
            NativeImaging.ValidateImages, "p3-probe");
        reopened.Open(target);
        var actual = PngExporter.Render(reopened.Session.Document, decoder: NativeImaging.Decoder);
        if (!expected.SequenceEqual(actual))
            throw new InvalidDataException("The migrated project changed after the legacy source was removed.");
        return Path.GetFileName(target);
    }

    private static CompatibilityImport Import(string path)
    {
        var import = CompatibilityService.Import(path, NativeImaging.Compatibility);
        ImageAssetValidator.Validate(import.Document, NativeImaging.Decoder);
        return import;
    }

    private static string SweepCorpus(string corpusDirectory)
    {
        if (!Directory.Exists(corpusDirectory)) return " (corpus directory not found)";
        var files = Directory.EnumerateFiles(corpusDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".phipreview", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".phifree", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal).ToList();
        var accepted = 0;
        var rejected = 0;
        foreach (var file in files)
        {
            var before = File.ReadAllBytes(file);
            try
            {
                var import = CompatibilityService.Import(file, NativeImaging.Compatibility);
                ImageAssetValidator.Validate(import.Document, NativeImaging.Decoder);
                accepted++;
                Console.WriteLine($"  CORPUS {Path.GetFileName(file)}: {import.Report.Summary}");
            }
            catch (Exception error) when (error is CompatibilityException or InvalidDataException)
            {
                rejected++;
                Console.WriteLine($"  CORPUS {Path.GetFileName(file)}: REJECTED " +
                    $"({(error as CompatibilityException)?.Kind.ToString() ?? "InvalidData"}): {error.Message}");
            }
            if (!File.ReadAllBytes(file).SequenceEqual(before))
                throw new InvalidDataException($"Corpus file {file} was modified by the reader.");
        }
        if (accepted == 0)
            throw new InvalidDataException("The legacy corpus sweep accepted no file.");
        return $", corpus {accepted} accepted / {rejected} rejected";
    }

    private static string ClassicFixture(byte[] background) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Project xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <Version>1</Version>
          <SourceName>四比三</SourceName>
          <NumberOnTop>true</NumberOnTop>
          <Background>{Convert.ToBase64String(background)}</Background>
          <Labels>
            <ChartLabel>
              <Name>曲目一</Name><Difficulty>IN</Difficulty><Level>15</Level><Author>谱师甲</Author>
              <ShowBadge>true</ShowBadge><ShowAuthor>true</ShowAuthor>
              <BadgeX>1450</BadgeX><BadgeY>800</BadgeY><AuthorX>1000</AuthorX><AuthorY>900</AuthorY>
              <Scale>1</Scale><AuthorWidth>360</AuthorWidth>
            </ChartLabel>
            <ChartLabel>
              <Name>曲目二</Name><Difficulty>AT</Difficulty><Level>16</Level><Author />
              <ShowBadge>true</ShowBadge><ShowAuthor>false</ShowAuthor>
              <BadgeX>200</BadgeX><BadgeY>120</BadgeY><AuthorX>0</AuthorX><AuthorY>0</AuthorY>
              <Scale>0.8</Scale><AuthorWidth>360</AuthorWidth>
            </ChartLabel>
          </Labels>
        </Project>
        """;

    private static string FreeFixture(byte[] image) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <FreeProject xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <Version>1</Version><Width>800</Width><Height>400</Height>
          <BackgroundColor>-14468281</BackgroundColor><SoftBackground>false</SoftBackground>
          <Layers>
            <FreeLayer>
              <Id>9ba7bdf895814d41ab5660171e22ea71</Id><Group /><Name>曲绘</Name><Kind>Image</Kind>
              <X>20</X><Y>20</Y><Width>400</Width><Height>200</Height>
              <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName>
              <Bold>false</Bold><Italic>false</Italic><LineSpacing>1</LineSpacing>
              <VerticalAlign>Center</VerticalAlign><Text>导入曲绘</Text><Align>Left</Align>
              <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb>
              <Visible>true</Visible><Locked>false</Locked><Slanted>false</Slanted><KeepAspect>true</KeepAspect>
              <Image>{Convert.ToBase64String(image)}</Image>
            </FreeLayer>
            <FreeLayer>
              <Id>5f8c1fd5d24b46feb299a713180d440e</Id><Group /><Name>重复曲绘</Name><Kind>Image</Kind>
              <X>440</X><Y>20</Y><Width>200</Width><Height>100</Height>
              <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName>
              <Bold>false</Bold><Italic>false</Italic><LineSpacing>1</LineSpacing>
              <VerticalAlign>Center</VerticalAlign><Text>导入曲绘</Text><Align>Left</Align>
              <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb>
              <Visible>true</Visible><Locked>false</Locked><Slanted>false</Slanted><KeepAspect>true</KeepAspect>
              <Image>{Convert.ToBase64String(image)}</Image>
            </FreeLayer>
            <FreeLayer>
              <Id>98d5c374861749a68669ac6b3c1e5511</Id><Group /><Name>极小难度牌</Name><Kind>Badge</Kind>
              <X>10</X><Y>300</Y><Width>1</Width><Height>1</Height>
              <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName>
              <Bold>false</Bold><Italic>false</Italic><LineSpacing>1</LineSpacing>
              <VerticalAlign>Center</VerticalAlign><Text>文字</Text><Align>Left</Align>
              <Difficulty>HD</Difficulty><Level>12</Level><ColorArgb>-1</ColorArgb>
              <Visible>true</Visible><Locked>false</Locked><Slanted>false</Slanted><KeepAspect>true</KeepAspect>
            </FreeLayer>
            <FreeLayer>
              <Id>c30eaabbd0b8434ba07ca2d669dfd743</Id><Group /><Name>标题</Name><Kind>Text</Kind>
              <X>40</X><Y>250</Y><Width>600</Width><Height>120</Height>
              <FontSize>48</FontSize><FontName>Microsoft YaHei UI</FontName>
              <Bold>false</Bold><Italic>false</Italic><LineSpacing>1.2</LineSpacing>
              <VerticalAlign>Top</VerticalAlign><Text>中文 English 标题</Text><Align>Center</Align>
              <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb>
              <Visible>true</Visible><Locked>false</Locked><Slanted>false</Slanted><KeepAspect>true</KeepAspect>
            </FreeLayer>
          </Layers>
        </FreeProject>
        """;
}
