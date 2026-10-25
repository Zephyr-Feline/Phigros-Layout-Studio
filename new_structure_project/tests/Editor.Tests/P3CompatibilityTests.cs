using System.Globalization;
using System.Text;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Formats.Compatibility;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>
/// P3 compatibility: classic geometry, both legacy readers, mapping diagnostics, migration
/// independence, transactional failure recovery and the real V1 corpus when it is available.
/// </summary>
public sealed class P3CompatibilityTests
{
    private static readonly CompatibilityOptions Options = new()
    {
        ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions,
        ProbeImageOpacity = asset => ImageAssetValidator.IsOpaque(asset, TestImages.Decoder)
    };

    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        AvaloniaTestHost.EnsureStarted();
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p3-compat",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    // --- classic geometry ----------------------------------------------------------------------

    [TestCase(1920, 1080, 1.0)]
    [TestCase(1440, 1080, 4.0 / 3.0)]
    [TestCase(2560, 1080, 1920.0 / 2560.0 * 1)]
    [TestCase(1080, 1920, 1920.0 * 1920 / (1080.0 * 1080))]
    [TestCase(2740, 1080, 1920.0 * 1080 / (1080.0 * 2740))]
    public void AspectCorrectionMatchesTheV1Formula(int width, int height, double expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(ClassicGeometry.AspectCorrection(width, height), Is.EqualTo(expected).Within(1e-12));
            // The correction cancels the export anisotropy, so the final drawing scale is isotropic.
            Assert.That(ClassicGeometry.FinalScale(height), Is.EqualTo(height / 1080.0).Within(1e-12));
            Assert.That(ClassicGeometry.AspectCorrection(width, height) * width / 1920.0,
                Is.EqualTo(ClassicGeometry.FinalScale(height)).Within(1e-12));
        });
    }

    [TestCase(0, 1080)]
    [TestCase(1920, 0)]
    [TestCase(double.NaN, 1080)]
    [TestCase(double.PositiveInfinity, 1080)]
    [TestCase(1920, -1)]
    public void InvalidClassicSizesAreRejectedInsteadOfProducingNaN(double width, double height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicGeometry.AspectCorrection(width, height));

    [Test]
    public void ClassicAxisConversionsArePaired()
    {
        foreach (var (width, height) in new[] { (1920, 1080), (1440, 1080), (2740, 1080), (2560, 1080) })
        {
            foreach (var reference in new[] { 0.0, 145.5, 1000.0, 1920.0 })
            {
                Assert.That(ClassicGeometry.ToReferenceX(ClassicGeometry.ToCanvasX(reference, width), width),
                    Is.EqualTo(reference).Within(1e-9));
                Assert.That(ClassicGeometry.ToReferenceY(ClassicGeometry.ToCanvasY(reference, height), height),
                    Is.EqualTo(reference).Within(1e-9));
            }
        }
    }

    [TestCase(1440, 1080, 1440, 1080)]
    [TestCase(1920, 1080, 1280, 720)]
    [TestCase(2560, 1440, 1920, 1080)]
    [TestCase(1000, 1000, 1000, 1000)]
    public void ExportHeightUsesToEvenRounding(int backgroundWidth, int backgroundHeight, int exportWidth,
        int expected) => Assert.That(
            ClassicGeometry.ExportHeight(backgroundWidth, backgroundHeight, exportWidth), Is.EqualTo(expected));

    [TestCase(1920, 1080, 1450, 800, 128, 108)]
    [TestCase(1440, 1080, 1087.5, 800, 128, 108)]
    [TestCase(2560, 1080, 1450 * 2560 / 1920.0, 800 * 1, 128, 108)]
    [TestCase(2740, 1080, 1450 * 2740 / 1920.0, 800, 128, 108)]
    public void ClassicBadgeKeepsItsAspectAndUniformScale(int width, int height, double x, double y,
        double expectedWidth, double expectedHeight)
    {
        var placement = ClassicGeometry.Place(new ClassicLabelGeometry(1450, 800, 1000, 900, 1, 360),
            width, height);
        Assert.Multiple(() =>
        {
            Assert.That(placement.Badge.X, Is.EqualTo(x).Within(1e-9));
            Assert.That(placement.Badge.Y, Is.EqualTo(y).Within(1e-9));
            Assert.That(placement.Badge.Width, Is.EqualTo(expectedWidth).Within(1e-9));
            Assert.That(placement.Badge.Height, Is.EqualTo(expectedHeight).Within(1e-9));
            Assert.That(placement.Badge.Width / placement.Badge.Height,
                Is.EqualTo(128.0 / 108.0).Within(1e-12));
            Assert.That(placement.Author.Height / placement.Author.Width,
                Is.EqualTo(40.0 / 360).Within(1e-12));
            Assert.That(placement.AuthorFontSize, Is.EqualTo(25 * placement.Scale).Within(1e-12));
            Assert.That(placement.Clamped, Is.False);
        });
    }

    [Test]
    public void ClassicClampMatchesTheV1Renderer()
    {
        // V1 clamps a copy in reference space, so a badge placed past the right/bottom edge moves in.
        var placement = ClassicGeometry.Place(new ClassicLabelGeometry(1900, 1075, 1900, 1070, 1, 1600),
            1440, 1080);
        Assert.Multiple(() =>
        {
            Assert.That(placement.Clamped, Is.True);
            Assert.That(placement.Badge.X + placement.Badge.Width,
                Is.LessThanOrEqualTo(1440 + 1e-6));
            Assert.That(placement.Badge.Y + placement.Badge.Height,
                Is.LessThanOrEqualTo(1080 + 1e-6));
            Assert.That(placement.Author.X + placement.Author.Width, Is.LessThanOrEqualTo(1440 + 1e-6));
            Assert.That(placement.Author.Y + placement.Author.Height, Is.LessThanOrEqualTo(1080 + 1e-6));
        });
    }

    // --- format detection ---------------------------------------------------------------------

    [Test]
    public void FormatsAreDetectedFromContentNotExtension()
    {
        var classic = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Project><Version>1</Version></Project>");
        var free = Encoding.UTF8.GetBytes("<FreeProject><Version>1</Version></FreeProject>");
        var json = Encoding.UTF8.GetBytes("{\"FormatVersion\":1,\"Document\":{}}");
        Assert.Multiple(() =>
        {
            Assert.That(LegacyFormatDetector.Detect(classic), Is.EqualTo(LegacyFormatKind.ClassicPreview));
            Assert.That(LegacyFormatDetector.Detect(free), Is.EqualTo(LegacyFormatKind.FreeLayout));
            Assert.That(LegacyFormatDetector.Detect(json), Is.EqualTo(LegacyFormatKind.TemporaryJson));
            Assert.That(LegacyFormatDetector.Detect([0x50, 0x4b, 0x03, 0x04, 0x11]),
                Is.EqualTo(LegacyFormatKind.Native));
            Assert.That(LegacyFormatDetector.Detect("<html></html>"u8),
                Is.EqualTo(LegacyFormatKind.Unknown));
            Assert.That(LegacyFormatDetector.Detect([]), Is.EqualTo(LegacyFormatKind.Unknown));
        });
    }

    [Test]
    public void NativePackageIsNeverRetriedAsLegacy()
    {
        var native = Path.Combine(_directory, "renamed 原生包.phipreview");
        NativePackageStore.Save(native, new EditorDocument(), "p3-test");
        Assert.That(LegacyFormatDetector.DetectFile(native), Is.EqualTo(LegacyFormatKind.Native));
        var error = Assert.Throws<CompatibilityException>(() => CompatibilityService.Import(native));
        Assert.Multiple(() =>
        {
            Assert.That(error!.Kind, Is.EqualTo(CompatibilityErrorKind.UnsupportedFeature));
            Assert.That(error.Message, Does.Contain("native .ppgproj"));
        });
    }

    [Test]
    public void FutureNativeSchemaIsRejectedByTheNativeReaderNotByLegacyFallback()
    {
        var path = Path.Combine(_directory, "future.ppgproj");
        NativePackageStore.Save(path, new EditorDocument(), "p3-test");
        using (var zip = System.IO.Compression.ZipFile.Open(path,
            System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("project.json")!;
            string json;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) json = reader.ReadToEnd();
            entry.Delete();
            using var output = new StreamWriter(zip.CreateEntry("project.json").Open(), new UTF8Encoding(false));
            output.Write(json.Replace($"\"schemaVersion\": {NativePackageStore.SchemaVersion}", "\"schemaVersion\": 99"));
        }
        Assert.Multiple(() =>
        {
            // Detection is content based, so the file must go to the native reader, never to a legacy
            // fallback parse attempt.
            Assert.That(LegacyFormatDetector.DetectFile(path), Is.EqualTo(LegacyFormatKind.Native));
            Assert.That(() => NativePackageStore.Read(path),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("schema"));
            var error = Assert.Throws<CompatibilityException>(() => CompatibilityService.Import(path));
            Assert.That(error!.Kind, Is.EqualTo(CompatibilityErrorKind.UnsupportedFeature));
        });
    }

    // --- classic reader ------------------------------------------------------------------------

    [Test]
    public void ClassicProjectMapsToTheBackgroundCanvasWithExactGeometry()
    {
        var path = WriteFile("4比3.phipreview", Classic(1440, 1080, numberOnTop: true, author: "谱师甲"));
        var import = CompatibilityService.Import(path, Options);
        var document = import.Document;
        var badge = document.Layers.First(layer => layer.Kind == LayerKind.Badge);
        var plate = document.Layers.First(layer => layer.Kind == LayerKind.Shape);
        var author = document.Layers.First(layer => layer.Kind == LayerKind.Text);
        Assert.Multiple(() =>
        {
            Assert.That(document.CanvasWidth, Is.EqualTo(1440));
            Assert.That(document.CanvasHeight, Is.EqualTo(1080));
            Assert.That(badge.Bounds.X, Is.EqualTo(1087.5).Within(1e-9));
            Assert.That(badge.Bounds.Width, Is.EqualTo(128).Within(1e-9));
            Assert.That(badge.Bounds.Height, Is.EqualTo(108).Within(1e-9));
            Assert.That(badge.Difficulty, Is.EqualTo("IN"));
            Assert.That(badge.Level, Is.EqualTo(15));
            Assert.That(plate.ColorArgb, Is.EqualTo(0xD20E1821));
            Assert.That(author.Text, Is.EqualTo("谱师甲"));
            Assert.That(author.FontSize, Is.EqualTo(25).Within(1e-9));
            Assert.That(import.Report.Format, Is.EqualTo(LegacyFormatKind.ClassicPreview));
            Assert.That(import.Report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
            Assert.That(import.Report.Diagnostics.Select(item => item.Code),
                Has.Some.EqualTo("LEGACY_CLASSIC_NUMBER_ON_TOP_NOT_REPRESENTED"));
            Assert.That(document.ExtensionData["compat.source.format"], Is.EqualTo("phipreview"));
            Assert.That(document.ExtensionData["compat.source.version"], Is.EqualTo("1"));
        });
    }

    [Test]
    public void ClassicWithoutBackgroundProbeKeepsTheReferenceCanvas()
    {
        var path = WriteFile("noprobe.phipreview", Classic(1440, 1080, numberOnTop: false, author: ""));
        var import = CompatibilityService.Import(path);
        Assert.Multiple(() =>
        {
            Assert.That(import.Document.CanvasWidth, Is.EqualTo(1920));
            Assert.That(import.Document.Layers[0].Bounds.X, Is.EqualTo(1450));
            Assert.That(import.Report.Diagnostics.Select(item => item.Code),
                Has.Some.EqualTo("LEGACY_CLASSIC_BACKGROUND_SIZE_UNAVAILABLE"));
        });
    }

    [Test]
    public void TinyLegacyBackgroundIsNormalisedIntoTheNativeCanvasRange()
    {
        var path = WriteFile("tiny classic.phipreview", Classic(8, 8, numberOnTop: false, author: ""));
        var import = CompatibilityService.Import(path, Options);
        Assert.Multiple(() =>
        {
            Assert.That(import.Document.CanvasWidth, Is.EqualTo(320));
            Assert.That(import.Document.CanvasHeight, Is.EqualTo(320));
            Assert.That(import.Report.Diagnostics.Select(item => item.Code),
                Has.Some.EqualTo("LEGACY_CANVAS_RESOLUTION_NORMALIZED"));
        });
    }

    [TestCase("<Version>2</Version>", CompatibilityErrorKind.UnsupportedVersion, "newer version")]
    [TestCase("<Version>0</Version>", CompatibilityErrorKind.UnsupportedVersion, "Unsupported legacy project version 0")]
    [TestCase("<Level>0</Level>", CompatibilityErrorKind.InvalidGeometry, "Level")]
    [TestCase("<Difficulty>SP</Difficulty>", CompatibilityErrorKind.UnsupportedFeature, "difficulty")]
    [TestCase("<BadgeY>1500</BadgeY>", CompatibilityErrorKind.InvalidGeometry, "BadgeY")]
    [TestCase("<Scale>9</Scale>", CompatibilityErrorKind.InvalidGeometry, "Scale")]
    public void MalformedClassicFieldsAreRejectedWithTypedErrors(string replacement, CompatibilityErrorKind kind,
        string messagePart)
    {
        var xml = Classic(1440, 1080, numberOnTop: true, author: "")
            .Replace("<Version>1</Version>", "<Version>1</Version>")
            .Replace("<Level>15</Level>", replacement.Contains("Level") ? replacement : "<Level>15</Level>")
            .Replace("<Difficulty>IN</Difficulty>", replacement.Contains("Difficulty") ? replacement : "<Difficulty>IN</Difficulty>")
            .Replace("<BadgeY>800</BadgeY>", replacement.Contains("BadgeY") ? replacement : "<BadgeY>800</BadgeY>")
            .Replace("<Scale>1</Scale>", replacement.Contains("Scale") ? replacement : "<Scale>1</Scale>")
            .Replace("<Version>1</Version>", replacement.Contains("Version") ? replacement : "<Version>1</Version>");
        var path = WriteFile("invalid classic.phipreview", xml);
        var error = Assert.Throws<CompatibilityException>(() => CompatibilityService.Import(path, Options));
        Assert.Multiple(() =>
        {
            Assert.That(error!.Kind, Is.EqualTo(kind));
            Assert.That(error.Message, Does.Contain(messagePart));
        });
    }

    [Test]
    public void ClassicMissingBackgroundAndMissingVersionBehaveAsDocumented()
    {
        var noBackground = WriteFile("nobg.phipreview", "<Project><Version>1</Version></Project>");
        var error = Assert.Throws<CompatibilityException>(() =>
            CompatibilityService.Import(noBackground, Options));
        Assert.That(error!.Kind, Is.EqualTo(CompatibilityErrorKind.MissingAsset));

        var noVersion = WriteFile("noversion.phipreview",
            Classic(1440, 1080, numberOnTop: false, author: "").Replace("<Version>1</Version>", ""));
        var import = CompatibilityService.Import(noVersion, Options);
        Assert.That(import.Report.SourceVersion, Is.EqualTo(1));
    }

    [Test]
    public void ClassicUnknownFieldsAreReportedAndNotCarried()
    {
        var xml = Classic(1440, 1080, numberOnTop: false, author: "")
            .Replace("</ChartLabel>", "<FutureField>untouched</FutureField></ChartLabel>")
            .Replace("</Project>", "<ProjectFuture>1</ProjectFuture></Project>");
        var path = WriteFile("unknown.phipreview", xml);
        var import = CompatibilityService.Import(path, Options);
        var codes = import.Report.Diagnostics.Select(item => item.Message).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(codes, Has.Some.Contains("FutureField"));
            Assert.That(codes, Has.Some.Contains("ProjectFuture"));
            Assert.That(import.Report.Diagnostics.Select(item => item.Code),
                Has.Some.EqualTo("LEGACY_UNKNOWN_FIELD"));
        });
    }

    // --- free-layout reader --------------------------------------------------------------------

    [Test]
    public void FreeLayoutMapsExactlyAndReportsOnlyRendererDeviations()
    {
        var image = TestImages.EncodePng(400, 200, TestImages.SyntheticBgra(400, 200));
        var layers = new List<string>
        {
            $"""
            <FreeLayer><Id>9ba7bdf895814d41ab5660171e22ea71</Id><Group /><Name>曲绘</Name><Kind>Image</Kind>
            <X>20</X><Y>20</Y><Width>400</Width><Height>200</Height>
            <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
            <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text>导入曲绘</Text><Align>Left</Align>
            <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
            <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>true</KeepAspect>
            <Image>{Convert.ToBase64String(image)}</Image></FreeLayer>
            """
        };
        layers.AddRange(ShapeLayer());
        var path = WriteFile("free.phifree", Free(800, 400, soft: false, background: image,
            layers: layers.ToArray()));
        var import = CompatibilityService.Import(path, Options);
        Assert.Multiple(() =>
        {
            Assert.That(import.Document.CanvasWidth, Is.EqualTo(800));
            Assert.That(import.Document.CanvasHeight, Is.EqualTo(400));
            Assert.That(import.Document.FixedBackgroundAssetId, Is.Not.Null);
            Assert.That(import.Document.Layers.Single(layer => layer.Kind == LayerKind.Shape).Bounds,
                Is.EqualTo(new RectD(100, 250, 200, 100)));
            // Matching aspects and a non-slanted shape need no conversion finding at all.
            Assert.That(import.Report.Level, Is.EqualTo(CompatibilityLevel.Exact));
            Assert.That(import.Report.Diagnostics, Is.Empty);
        });
    }

    [Test]
    public void FreeSlantedArtworkKeepsItsPolygonAndCoverCrop()
    {
        var image = TestImages.EncodePng(400, 200, TestImages.SyntheticBgra(400, 200));
        var xml = ImageLayer("d30eaabbd0b8434ba07ca2d669dfd743",
            Convert.ToBase64String(image), 200, 100)
            .Replace("<Slanted>false</Slanted>", "<Slanted>true</Slanted>");
        var path = WriteFile("slanted-art.phifree", Free(800, 400, soft: false,
            background: null, layers: [xml]));
        var import = CompatibilityService.Import(path, Options);
        var layer = import.Document.Layers.Single();
        Assert.Multiple(() =>
        {
            Assert.That(layer.Slanted, Is.True);
            Assert.That(import.Report.Diagnostics.Select(item => item.Code),
                Has.None.EqualTo("LEGACY_SLANTED_IMAGE_NOT_REPRESENTED"));
        });
        var (width, _, pixels) = TestImages.DecodePng(TestImages.RenderPng(import.Document, true));
        Assert.That(pixels[(22 * width + 22) * 4 + 3], Is.Zero);
        Assert.That(pixels[(70 * width + 100) * 4 + 3], Is.EqualTo(255));
    }

    [Test]
    public void FreeImageWhoseAspectDiffersFromItsBoxIsReportedAsADeviation()
    {
        var image = TestImages.EncodePng(400, 200, TestImages.SyntheticBgra(400, 200));
        var path = WriteFile("aspect.phifree", Free(800, 400, soft: false, background: null,
            layers: [ImageLayer("cccccccccccccccccccccccccccccccc", Convert.ToBase64String(image), 200, 200)]));
        var import = CompatibilityService.Import(path, Options);
        Assert.Multiple(() =>
        {
            Assert.That(import.Report.Diagnostics.Select(item => item.Code),
                Has.Some.EqualTo("LEGACY_FREE_IMAGE_FIT_DEVIATION"));
            Assert.That(import.Report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
            Assert.That(import.Report.Dimensions[2].Status, Is.EqualTo(DimensionStatus.Lossy));
        });
    }

    [Test]
    public void FreeLayoutDuplicateImagesShareOnePayload()
    {
        var image = TestImages.EncodePng(200, 100, TestImages.SyntheticBgra(200, 100));
        var data = Convert.ToBase64String(image);
        var path = WriteFile("dedup.phifree", Free(800, 400, soft: false, background: null,
            layers: [ImageLayer("11111111111111111111111111111111", data),
                ImageLayer("22222222222222222222222222222222", data)]));
        var import = CompatibilityService.Import(path, Options);
        var blobs = import.Document.Layers.Select(item => import.Document.Assets[item.AssetId!.Value]).ToList();
        Assert.That(ReferenceEquals(blobs[0], blobs[1]), Is.True);
        var project = Path.Combine(_directory, "dedup.ppgproj");
        NativePackageStore.Save(project, import.Document, "p3-test");
        using var zip = System.IO.Compression.ZipFile.OpenRead(project);
        Assert.That(zip.Entries.Count(entry =>
            entry.FullName.StartsWith("assets/", StringComparison.Ordinal)), Is.EqualTo(1));
    }

    private static string ImageLayer(string id, string base64Image, double width = 200,
        double height = 100) => $"""
        <FreeLayer><Id>{id}</Id><Group /><Name>曲绘</Name><Kind>Image</Kind>
        <X>20</X><Y>20</Y><Width>{width}</Width><Height>{height}</Height>
        <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
        <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text>导入曲绘</Text><Align>Left</Align>
        <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
        <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>true</KeepAspect>
        <Image>{base64Image}</Image></FreeLayer>
        """;

    [Test]
    public void FreeTagBecomesAPlateAndCaptionWithDeviations()
    {
        var path = WriteFile("tag.phifree", Free(800, 400, soft: false, background: null,
            layers: [$"""
                <FreeLayer><Id>c30eaabbd0b8434ba07ca2d669dfd743</Id><Group /><Name>Original 标签</Name><Kind>Tag</Kind>
                <X>100</X><Y>100</Y><Width>280</Width><Height>42</Height>
                <FontSize>25</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
                <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text>Fanmade Chart</Text>
                <Align>Center</Align><Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-16777216</ColorArgb>
                <Visible>true</Visible><Locked>false</Locked><Slanted>true</Slanted><KeepAspect>true</KeepAspect></FreeLayer>
                """]));
        var import = CompatibilityService.Import(path, Options);
        var plate = import.Document.Layers.Single(layer => layer.Kind == LayerKind.Shape);
        var caption = import.Document.Layers.Single(layer => layer.Kind == LayerKind.Text);
        Assert.Multiple(() =>
        {
            Assert.That(plate.ColorArgb, Is.EqualTo(0xFFFFFFFF));
            Assert.That(plate.Slanted, Is.True);
            Assert.That(plate.Bounds, Is.EqualTo(new RectD(100, 100, 280, 42)));
            Assert.That(caption.Text, Is.EqualTo("Fanmade Chart"));
            Assert.That(caption.VerticalAlignment, Is.EqualTo(VerticalAlignment.Center));
            Assert.That(caption.Bounds.X, Is.GreaterThan(plate.Bounds.X));
            Assert.That(import.Report.Diagnostics.Select(item => item.Code),
                Has.None.EqualTo("LEGACY_TAG_PLATE_NOT_REPRESENTED"));
            Assert.That(import.Report.Diagnostics.Select(item => item.Code),
                Has.None.EqualTo("LEGACY_TEXT_VERTICAL_ALIGN_NOT_REPRESENTED"));
        });
    }

    [Test]
    public void TinyFreeBadgeIsCanonicalisedAndRecordedAsALoss()
    {
        var path = WriteFile("tiny.phifree", Free(640, 360, soft: false, background: null,
            layers: [$"""
                <FreeLayer><Id>98d5c374861749a68669ac6b3c1e5511</Id><Group /><Name>组件</Name><Kind>Badge</Kind>
                <X>100</X><Y>100</Y><Width>1</Width><Height>1</Height>
                <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
                <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text>文字</Text><Align>Left</Align>
                <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
                <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>true</KeepAspect></FreeLayer>
                """]));
        var import = CompatibilityService.Import(path, Options);
        var badge = import.Document.Layers.Single(layer => layer.Kind == LayerKind.Badge);
        Assert.Multiple(() =>
        {
            Assert.That(badge.Bounds.Width, Is.EqualTo(8));
            Assert.That(badge.Bounds.Width / badge.Bounds.Height, Is.EqualTo(128.0 / 108.0).Within(1e-9));
            Assert.That(badge.KeepAspect, Is.True);
            Assert.That(import.Report.Diagnostics.Select(item => item.Code),
                Has.Some.EqualTo("LEGACY_BADGE_BELOW_NATIVE_MINIMUM"));
            Assert.That(import.Report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
        });
    }

    [Test]
    public void FreeLossAndDeviationFindingsCoverTheKnownGaps()
    {
        var path = WriteFile("gaps.phifree", Free(800, 400, soft: true, background: null,
            layers: [$"""
                <FreeLayer><Id>11111111111111111111111111111111</Id><Group /><Name>斜切</Name><Kind>Shape</Kind>
                <X>10</X><Y>10</Y><Width>200</Width><Height>100</Height>
                <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
                <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text /><Align>Left</Align>
                <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
                <Locked>false</Locked><Slanted>true</Slanted><KeepAspect>false</KeepAspect></FreeLayer>
                """, $"""
                <FreeLayer><Id>22222222222222222222222222222222</Id><Group /><Name>文本</Name><Kind>Text</Kind>
                <X>10</X><Y>200</Y><Width>400</Width><Height>100</Height>
                <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
                <LineSpacing>1</LineSpacing><VerticalAlign>Bottom</VerticalAlign><Text>中文</Text><Align>Center</Align>
                <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
                <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>true</KeepAspect></FreeLayer>
                """]));
        var import = CompatibilityService.Import(path, Options);
        var codes = import.Report.Diagnostics.Select(item => item.Code).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(codes, Has.Some.EqualTo("LEGACY_FREE_SOFT_BACKGROUND_NOT_REPRESENTED"));
            Assert.That(codes, Has.Some.EqualTo("LEGACY_FREE_BACKGROUND_GRADIENT_DEVIATION"));
            Assert.That(codes, Has.None.EqualTo("LEGACY_SLANTED_SHAPE_NOT_REPRESENTED"));
            Assert.That(import.Document.Layers.Single(layer => layer.Kind == LayerKind.Shape)
                .Slanted, Is.True);
            Assert.That(codes, Has.None.EqualTo("LEGACY_TEXT_VERTICAL_ALIGN_NOT_REPRESENTED"));
            Assert.That(codes, Has.Some.EqualTo("LEGACY_TEXT_METRICS_DEVIATION"));
            Assert.That(import.Document.Layers.Single(layer => layer.Kind == LayerKind.Text)
                .VerticalAlignment, Is.EqualTo(VerticalAlignment.Bottom));
            Assert.That(import.Report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
        });
    }

    [Test]
    public void OldFreeFileWithoutNewerFieldsUsesDocumentedDefaults()
    {
        // The real 旧工程字体兼容 fixture omits FontName/Bold/Italic/LineSpacing/VerticalAlign.
        var path = WriteFile("old fields.phifree", Free(800, 600, soft: false, background: null,
            layers: [$"""
                <FreeLayer><Id>835cc1d4faed4b44883182171f25572f</Id><Group /><Name>组件</Name><Kind>Text</Kind>
                <X>100</X><Y>100</Y><Width>600</Width><Height>350</Height>
                <FontSize>53</FontSize>
                <Text>Phigros 2026</Text><Align>Right</Align><Difficulty>IN</Difficulty><Level>15</Level>
                <ColorArgb>-1</ColorArgb><Visible>true</Visible><Locked>false</Locked>
                <Slanted>false</Slanted><KeepAspect>true</KeepAspect></FreeLayer>
                """]));
        var import = CompatibilityService.Import(path, Options);
        var text = import.Document.Layers.Single();
        Assert.Multiple(() =>
        {
            Assert.That(text.FontFamily, Is.EqualTo("Microsoft YaHei UI"));
            Assert.That(text.LineSpacing, Is.EqualTo(1));
            Assert.That(text.Alignment, Is.EqualTo(HorizontalAlignment.Right));
            Assert.That(text.VerticalAlignment, Is.EqualTo(VerticalAlignment.Center));
            Assert.That(text.Text, Is.EqualTo("Phigros 2026"));
        });
    }

    [Test]
    public void FreeImageLayerWithoutBytesUsesTheV1Placeholder()
    {
        var path = WriteFile("blank.phifree", Free(1000, 600, soft: false, background: null,
            layers: [$"""
                <FreeLayer><Id>33333333333333333333333333333333</Id><Group /><Name>曲绘</Name><Kind>Image</Kind>
                <X>40</X><Y>40</Y><Width>520</Width><Height>250</Height>
                <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
                <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text>导入曲绘</Text><Align>Left</Align>
                <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
                <Locked>false</Locked><Slanted>true</Slanted><KeepAspect>true</KeepAspect></FreeLayer>
                """]));
        var import = CompatibilityService.Import(path, Options);
        Assert.Multiple(() =>
        {
            Assert.That(import.Document.Assets, Is.Empty);
            Assert.That(import.Document.Layers.Select(layer => layer.Kind),
                Is.EqualTo(new[] { LayerKind.Shape, LayerKind.Text }));
            Assert.That(import.Document.Layers[1].Text, Is.EqualTo("导入曲绘"));
            Assert.That(import.Report.Diagnostics.Select(item => item.Code),
                Has.Some.EqualTo("LEGACY_IMAGE_PLACEHOLDER_NORMALIZED"));
        });
    }

    [TestCase("Kind>Star", CompatibilityErrorKind.UnsupportedFeature, "Unsupported FreeLayer Kind Star")]
    [TestCase("Width>invalid", CompatibilityErrorKind.Malformed, "Invalid integer field Width")]
    [TestCase("Width>100", CompatibilityErrorKind.InvalidGeometry, "Width")]
    [TestCase("Width>9000", CompatibilityErrorKind.InvalidGeometry, "Width")]
    [TestCase("X>99999", CompatibilityErrorKind.InvalidGeometry, "X")]
    [TestCase("X>NaN", CompatibilityErrorKind.InvalidGeometry, "finite")]
    [TestCase("LineSpacing>9", CompatibilityErrorKind.InvalidGeometry, "LineSpacing")]
    [TestCase("Level>0", CompatibilityErrorKind.InvalidGeometry, "Level")]
    [TestCase("Visible>maybe", CompatibilityErrorKind.Malformed, "Boolean")]
    [TestCase("Image>!!!not-base64!!!", CompatibilityErrorKind.Malformed, "Base64")]
    [TestCase("Align>Middle", CompatibilityErrorKind.UnsupportedFeature, "Align")]
    public void MalformedFreeFieldsAreRejectedWithTypedErrors(string mutation, CompatibilityErrorKind kind,
        string messagePart)
    {
        var parts = mutation.Split('>');
        var baseline = Free(800, 400, soft: false, background: null, layers: [$"""
            <FreeLayer><Id>44444444444444444444444444444444</Id><Group /><Name>组件</Name><Kind>Shape</Kind>
            <X>10</X><Y>10</Y><Width>200</Width><Height>100</Height>
            <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
            <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text /><Align>Left</Align>
            <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
            <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>false</KeepAspect></FreeLayer>
            """]);
        var mutated = ReplaceField(baseline, parts[0], parts[1]);
        var path = WriteFile("mutation.phifree", mutated);
        var error = Assert.Throws<CompatibilityException>(() => CompatibilityService.Import(path, Options));
        Assert.Multiple(() =>
        {
            Assert.That(error!.Kind, Is.EqualTo(kind));
            Assert.That(error.Message, Does.Contain(messagePart));
        });
    }

    [TestCase("<FreeProject><Version>1</Version>")]
    [TestCase("")]
    [TestCase("not xml at all")]
    [TestCase("<FreeProject><Version>1</Version><Layers><FreeLayer>")]
    public void TruncatedOrNonXmlLegacyInputIsRejectedAsMalformed(string content)
    {
        var path = WriteFile("malformed.phifree", content);
        var error = Assert.Throws<CompatibilityException>(() => CompatibilityService.Import(path, Options));
        Assert.That(error!.Kind, Is.AnyOf(CompatibilityErrorKind.Malformed,
            CompatibilityErrorKind.UnsupportedFeature));
    }

    [Test]
    public void LegacyVersionPolicyMatchesV1()
    {
        foreach (var version in new[] { "2", "9", "-1" })
        {
            var path = WriteFile($"v{version}.phifree",
                $"<FreeProject><Version>{version}</Version></FreeProject>");
            var error = Assert.Throws<CompatibilityException>(() => CompatibilityService.Import(path, Options));
            Assert.That(error!.Kind, Is.EqualTo(CompatibilityErrorKind.UnsupportedVersion), version);
        }
    }

    [Test]
    public void LayerCountAndTextLengthLimitsAreEnforced()
    {
        var many = Enumerable.Range(0, 201).Select(index => $"""
            <FreeLayer><Id>{index:x32}</Id><Group /><Name>组件</Name><Kind>Shape</Kind>
            <X>0</X><Y>0</Y><Width>10</Width><Height>10</Height>
            <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
            <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text /><Align>Left</Align>
            <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
            <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>false</KeepAspect></FreeLayer>
            """);
        var tooMany = WriteFile("many.phifree", Free(800, 400, false, null, many.ToArray()));
        var error = Assert.Throws<CompatibilityException>(() => CompatibilityService.Import(tooMany, Options));
        Assert.That(error!.Kind, Is.EqualTo(CompatibilityErrorKind.InvalidGeometry));

        var longText = WriteFile("long.phifree", Free(800, 400, false, null, [$"""
            <FreeLayer><Id>55555555555555555555555555555555</Id><Group /><Name>组件</Name><Kind>Text</Kind>
            <X>0</X><Y>0</Y><Width>10</Width><Height>10</Height>
            <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
            <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign>
            <Text>{new string('文', 4001)}</Text><Align>Left</Align>
            <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
            <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>false</KeepAspect></FreeLayer>
            """]));
        var lengthError = Assert.Throws<CompatibilityException>(() =>
            CompatibilityService.Import(longText, Options));
        Assert.That(lengthError!.Kind, Is.EqualTo(CompatibilityErrorKind.Malformed));
    }

    [Test]
    public void CultureDoesNotChangeParsingOrSerialization()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // comma decimal separator
            var path = WriteFile("culture.phifree", Free(800, 400, soft: false, background: null,
                layers: [$"""
                    <FreeLayer><Id>66666666666666666666666666666666</Id><Group /><Name>组件</Name><Kind>Text</Kind>
                    <X>10.5</X><Y>20.25</Y><Width>200.125</Width><Height>100.5</Height>
                    <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
                    <LineSpacing>1.5</LineSpacing><VerticalAlign>Center</VerticalAlign><Text>文字</Text><Align>Left</Align>
                    <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
                    <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>false</KeepAspect></FreeLayer>
                    """]));
            var import = CompatibilityService.Import(path, Options);
            Assert.Multiple(() =>
            {
                Assert.That(import.Document.Layers[0].Bounds.X, Is.EqualTo(10.5));
                Assert.That(import.Document.Layers[0].Bounds.Y, Is.EqualTo(20.25));
                Assert.That(import.Document.Layers[0].Bounds.Width, Is.EqualTo(200.125));
                Assert.That(import.Document.Layers[0].LineSpacing, Is.EqualTo(1.5));
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // --- report --------------------------------------------------------------------------------

    [Test]
    public void ReportCarriesTheFourRequiredDimensions()
    {
        var image = TestImages.EncodePng(800, 400, TestImages.SyntheticBgra(800, 400));
        var path = WriteFile("exact.phifree", Free(800, 400, soft: false, background: image,
            layers: [$"""
                <FreeLayer><Id>77777777777777777777777777777777</Id><Group /><Name>组件</Name><Kind>Shape</Kind>
                <X>10</X><Y>10</Y><Width>200</Width><Height>100</Height>
                <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
                <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text /><Align>Left</Align>
                <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
                <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>false</KeepAspect></FreeLayer>
                """]));
        var import = CompatibilityService.Import(path, Options);
        Assert.Multiple(() =>
        {
            Assert.That(import.Report.Level, Is.EqualTo(CompatibilityLevel.Exact));
            Assert.That(import.Report.Diagnostics, Is.Empty);
            Assert.That(import.Report.Dimensions.Select(item => item.Kind), Is.EqualTo(new[]
            {
                CompatibilityDimensionKind.DataReadable, CompatibilityDimensionKind.EditableSemantics,
                CompatibilityDimensionKind.VisualLayout, CompatibilityDimensionKind.LegacyReopen
            }));
            Assert.That(import.Report.Dimensions[0].Status, Is.EqualTo(DimensionStatus.Exact));
            Assert.That(import.Report.Dimensions[3].Status, Is.EqualTo(DimensionStatus.NotVerified));
            Assert.That(import.Report.Summary, Does.Contain("Exact"));
            Assert.That(import.Report.Lines, Is.Empty);
        });
    }

    // --- migration -----------------------------------------------------------------------------

    [Test]
    public void MigrationSurvivesDeletingTheLegacySourceAndItsImages()
    {
        var image = TestImages.EncodePng(400, 200, TestImages.SyntheticBgra(400, 200));
        var imagePath = Path.Combine(_directory, "曲绘 source.png");
        // The legacy format embeds image bytes, so the reader never depends on this file; deleting it
        // proves the migration does not keep an external reference.
        File.WriteAllBytes(imagePath, image);
        var legacy = WriteFile("migrate.phifree", Free(800, 400, soft: false, background: null,
            layers: [$"""
                <FreeLayer><Id>88888888888888888888888888888888</Id><Group /><Name>曲绘</Name><Kind>Image</Kind>
                <X>20</X><Y>20</Y><Width>400</Width><Height>200</Height>
                <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
                <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text>导入曲绘</Text><Align>Left</Align>
                <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
                <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>true</KeepAspect>
                <Image>{Convert.ToBase64String(image)}</Image></FreeLayer>
                """]));
        var before = File.ReadAllBytes(legacy);
        var import = CompatibilityService.Import(legacy, Options);
        TestImages.ValidateImages(import.Document);
        var session = new EditSession(new EditorDocument());
        var workflow = new NativeProjectWorkflow(session, TestImages.ValidateImages, "p3-test");
        workflow.ImportConverted(import.Document);
        var expected = TestImages.RenderPng(session.Document);
        var project = Path.Combine(_directory, "迁移 结果.ppgproj");
        workflow.SaveAs(project);
        File.Delete(legacy);
        File.Delete(imagePath);
        var reopened = new NativeProjectWorkflow(new EditSession(new EditorDocument()),
            TestImages.ValidateImages, "p3-test");
        reopened.Open(project);
        Assert.Multiple(() =>
        {
            Assert.That(import.Report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
            Assert.That(before, Is.Not.Empty);
            Assert.That(File.Exists(legacy), Is.False, "the legacy source was removed by this test");
            Assert.That(File.Exists(imagePath), Is.False, "the original artwork was removed by this test");
            Assert.That(TestImages.RenderPng(reopened.Session.Document), Is.EqualTo(expected));
        });
    }

    [Test]
    public void ImportIsDeterministicAndStartsUnsavedWithoutANativePath()
    {
        var path = WriteFile("deterministic.phifree", Free(800, 400, soft: false, background: null,
            layers: [$"""
                <FreeLayer><Id>not-a-guid</Id><Group>legacy group</Group><Name>组件</Name><Kind>Shape</Kind>
                <X>10</X><Y>10</Y><Width>200</Width><Height>100</Height>
                <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
                <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text /><Align>Left</Align>
                <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
                <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>false</KeepAspect></FreeLayer>
                """]));
        var first = CompatibilityService.Import(path, Options);
        var second = CompatibilityService.Import(path, Options);
        Assert.Multiple(() =>
        {
            Assert.That(first.Document.StateEquals(second.Document), Is.True,
                "derived ids must be stable across imports");
            Assert.That(first.Document.Layers[0].GroupId, Is.Not.Null);

            var workflow = new NativeProjectWorkflow(new EditSession(new EditorDocument()),
                TestImages.ValidateImages, "p3-test");
            workflow.ImportConverted(first.Document);
            Assert.That(workflow.IsDirty, Is.True);
            Assert.That(workflow.CurrentPath, Is.Null);
        });
    }

    [Test]
    public void FailedImportLeavesTheCurrentSessionUntouched()
    {
        var session = new EditSession(new EditorDocument());
        var layer = new Layer { Kind = LayerKind.Text, Text = "未保存" };
        session.AddLayer(layer);
        var workflow = new NativeProjectWorkflow(session, TestImages.ValidateImages, "p3-test");
        var undoCount = session.UndoCount;

        var broken = WriteFile("broken.phipreview", "<Project><Version>9</Version></Project>");
        Assert.Throws<CompatibilityException>(() =>
        {
            var import = CompatibilityService.Import(broken, Options);
            workflow.ImportConverted(import.Document);
        });
        Assert.Multiple(() =>
        {
            Assert.That(session.Document.Find(layer.Id).Text, Is.EqualTo("未保存"));
            Assert.That(session.UndoCount, Is.EqualTo(undoCount));
            Assert.That(workflow.IsDirty, Is.True);
            Assert.That(workflow.CurrentPath, Is.Null);
        });
    }

    [Test]
    public void UndoRedoAndNativeSaveWorkAfterALegacyImport()
    {
        var image = TestImages.EncodePng(200, 100, TestImages.SyntheticBgra(200, 100));
        var path = WriteFile("history.phifree", Free(800, 400, soft: false, background: null,
            layers: [$"""
                <FreeLayer><Id>99999999999999999999999999999999</Id><Group /><Name>曲绘</Name><Kind>Image</Kind>
                <X>20</X><Y>20</Y><Width>200</Width><Height>100</Height>
                <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
                <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text>导入曲绘</Text><Align>Left</Align>
                <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
                <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>true</KeepAspect>
                <Image>{Convert.ToBase64String(image)}</Image></FreeLayer>
                """]));
        var import = CompatibilityService.Import(path, Options);
        var session = new EditSession(new EditorDocument());
        var workflow = new NativeProjectWorkflow(session, TestImages.ValidateImages, "p3-test");
        workflow.ImportConverted(import.Document);
        var id = session.Document.Layers[0].Id;
        session.BeginGesture(id);
        session.MoveBy(30, 10);
        session.CommitGesture();
        Assert.That(session.IsDirty, Is.True);
        Assert.That(session.Undo(), Is.True);
        Assert.That(session.Document.Find(id).Bounds.X, Is.EqualTo(20));
        Assert.That(session.Redo(), Is.True);
        Assert.That(session.Document.Find(id).Bounds.X, Is.EqualTo(50));
        var project = Path.Combine(_directory, "history.ppgproj");
        workflow.SaveAs(project);
        Assert.Multiple(() =>
        {
            Assert.That(workflow.IsDirty, Is.False);
            Assert.That(NativePackageStore.Read(project).Find(id).Bounds.X, Is.EqualTo(50));
        });
    }

    // --- real corpus ---------------------------------------------------------------------------

    [Test]
    [Category("LocalCorpus")]
    public void RealLegacyCorpusIsReadWithoutModifyingTheSources()
    {
        var corpus = FindCorpus();
        if (string.IsNullOrWhiteSpace(corpus))
            Assert.Ignore("Missing local input: set PPG_TEST_LOCAL_CORPUS_ROOT to the local V1 corpus directory; these user projects are not distributed.");
        if (!Directory.Exists(corpus))
            Assert.Ignore($"Missing local input: PPG_TEST_LOCAL_CORPUS_ROOT directory is unavailable: {corpus}");
        var files = Directory.EnumerateFiles(corpus!, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".phipreview", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".phifree", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal).ToList();
        Assert.That(files, Is.Not.Empty);
        var accepted = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var before = File.ReadAllBytes(file);
            var stamp = File.GetLastWriteTimeUtc(file);
            if (name.StartsWith("future", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("foreign", StringComparison.OrdinalIgnoreCase))
            {
                var error = Assert.Throws<CompatibilityException>(() =>
                    CompatibilityService.Import(file, Options), name);
                Assert.That(error!.Kind, Is.EqualTo(CompatibilityErrorKind.UnsupportedVersion), name);
            }
            else if (name.StartsWith("invalid", StringComparison.OrdinalIgnoreCase))
            {
                var error = Assert.Throws<CompatibilityException>(() =>
                    CompatibilityService.Import(file, Options), name);
                Assert.That(error!.Kind, Is.EqualTo(CompatibilityErrorKind.InvalidGeometry), name);
            }
            else
            {
                var import = CompatibilityService.Import(file, Options);
                TestImages.ValidateImages(import.Document);
                accepted++;
                Assert.That(import.Document.Layers, Is.Not.Empty, name);
                Assert.That(import.Report.SourceVersion, Is.EqualTo(1), name);
            }
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(file), Is.EqualTo(before), $"{name} bytes unchanged");
                Assert.That(File.GetLastWriteTimeUtc(file), Is.EqualTo(stamp), $"{name} timestamp unchanged");
            });
        }
        Assert.That(accepted, Is.GreaterThanOrEqualTo(20), "the corpus should import the real projects");
    }

    [Test]
    public void LegacyFileWithUnexpectedExtensionIsStillReadByContent()
    {
        var path = WriteFile("content.txt", Free(800, 400, soft: false, background: null, layers: [$"""
            <FreeLayer><Id>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</Id><Group /><Name>组件</Name><Kind>Shape</Kind>
            <X>10</X><Y>10</Y><Width>200</Width><Height>100</Height>
            <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
            <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text /><Align>Left</Align>
            <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
            <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>false</KeepAspect></FreeLayer>
            """]));
        Assert.That(LegacyFormatDetector.DetectFile(path), Is.EqualTo(LegacyFormatKind.FreeLayout));
        Assert.DoesNotThrow(() => CompatibilityService.Import(path, Options));
    }

    // --- helpers -------------------------------------------------------------------------------

    private string WriteFile(string name, string content) =>
        WriteFile(name, Encoding.UTF8.GetBytes(content));

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string ReplaceField(string xml, string field, string value)
    {
        var open = $"<{field}>";
        var close = $"</{field}>";
        var start = xml.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return xml.Replace($"<Name>组件</Name>", $"<{field}>{value}</{field}><Name>组件</Name>");
        var end = xml.IndexOf(close, start, StringComparison.Ordinal);
        if (end < 0)
        {
            // self-closing or attribute form: append the value inside the element
            var slash = xml.IndexOf("/>", start, StringComparison.Ordinal);
            return xml[..slash] + $">{value}" + xml[(slash + 2)..];
        }
        return xml[..(start + open.Length)] + value + xml[end..];
    }

    private static string Classic(int backgroundWidth, int backgroundHeight, bool numberOnTop,
        string author)
    {
        var background = TestImages.EncodePng(backgroundWidth, backgroundHeight,
            TestImages.SyntheticBgra(backgroundWidth, backgroundHeight));
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <Version>1</Version><SourceName>预览</SourceName>
              <NumberOnTop>{(numberOnTop ? "true" : "false")}</NumberOnTop>
              <Background>{Convert.ToBase64String(background)}</Background>
              <Labels>
                <ChartLabel><Name>曲目</Name><Difficulty>IN</Difficulty><Level>15</Level>
                  <Author>{author}</Author><ShowBadge>true</ShowBadge><ShowAuthor>true</ShowAuthor>
                  <BadgeX>1450</BadgeX><BadgeY>800</BadgeY><AuthorX>1000</AuthorX><AuthorY>900</AuthorY>
                  <Scale>1</Scale><AuthorWidth>360</AuthorWidth></ChartLabel>
              </Labels>
            </Project>
            """;
    }

    private static string Free(int width, int height, bool soft, byte[]? background, string[] layers)
    {
        var backgroundXml = background is null ? "" :
            $"<Background>{Convert.ToBase64String(background)}</Background>";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <FreeProject xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <Version>1</Version><Width>{width}</Width><Height>{height}</Height>
              <BackgroundColor>-14468281</BackgroundColor>
              <SoftBackground>{(soft ? "true" : "false")}</SoftBackground>
              {backgroundXml}
              <Layers>{string.Join("", layers)}</Layers>
            </FreeProject>
            """;
    }

    private static string[] ShapeLayer() => [$"""
        <FreeLayer><Id>bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb</Id><Group /><Name>色块</Name><Kind>Shape</Kind>
        <X>100</X><Y>250</Y><Width>200</Width><Height>100</Height>
        <FontSize>40</FontSize><FontName>Microsoft YaHei UI</FontName><Bold>false</Bold><Italic>false</Italic>
        <LineSpacing>1</LineSpacing><VerticalAlign>Center</VerticalAlign><Text /><Align>Left</Align>
        <Difficulty>IN</Difficulty><Level>15</Level><ColorArgb>-1</ColorArgb><Visible>true</Visible>
        <Locked>false</Locked><Slanted>false</Slanted><KeepAspect>false</KeepAspect></FreeLayer>
        """];

    private static string? FindCorpus()
        => Environment.GetEnvironmentVariable("PPG_TEST_LOCAL_CORPUS_ROOT");

}
