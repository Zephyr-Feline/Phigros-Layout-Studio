using System.Text;
using System.Xml.Linq;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Formats.Compatibility;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P3AuditRegressionTests
{
    private string _directory = "";
    private static readonly CompatibilityOptions Options = new()
    {
        ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions,
        ProbeImageOpacity = asset => ImageAssetValidator.IsOpaque(asset, TestImages.Decoder)
    };

    [SetUp]
    public void SetUp()
    {
        AvaloniaTestHost.EnsureStarted();
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p3-audit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static string Image() => Convert.ToBase64String(
        TestImages.EncodePng(400, 200, TestImages.SyntheticBgra(400, 200)));

    private static string Item(string kind, int id, string extra = "") =>
        $"<FreeLayer><Id>{id}</Id><Group>song</Group><Kind>{kind}</Kind><Name>item</Name>" +
        $"<Width>200</Width><Height>100</Height><VerticalAlign>Top</VerticalAlign><Text>caption</Text>{extra}</FreeLayer>";

    private static string Free(string layers, string extra = "") =>
        $"<FreeProject><Width>800</Width><Height>400</Height><SoftBackground>false</SoftBackground>{extra}<Layers>{layers}</Layers></FreeProject>";

    private static string Classic(string labels) =>
        $"<Project><NumberOnTop>false</NumberOnTop><Background>{Image()}</Background><Labels>{labels}</Labels></Project>";

    private static string Label(bool author = true) =>
        $"<ChartLabel><Name>song</Name><Author>author</Author><ShowAuthor>{author.ToString().ToLowerInvariant()}</ShowAuthor></ChartLabel>";

    private static string Scenario(string kind) => kind switch
    {
        "classic-background" => Classic(""),
        "classic-labels" => Classic(Label() + Label()),
        "free-images" => Free(Item("Image", 1, $"<Image>{Image()}</Image>") +
            Item("Image", 2, $"<Image>{Image()}</Image>"), $"<Background>{Image()}</Background>"),
        "free-tag" => Free(Item("Tag", 1) + Item("Text", 2)),
        "free-placeholder" => Free(Item("Image", 1)),
        _ => throw new ArgumentException(kind)
    };

    [TestCase("classic-background")]
    [TestCase("classic-labels")]
    [TestCase("free-images")]
    [TestCase("free-tag")]
    [TestCase("free-placeholder")]
    public void EveryGeneratedIdentityAndDiagnosticIsStableAcrossPathsAndNativeRoundTrip(string scenario)
    {
        var bytes = Scenario(scenario);
        var a = Write("first.project", bytes);
        var b = Write("different path.project", bytes);
        var first = CompatibilityService.Import(a, Options);
        // The service is stateless. Recreated options and a different path must not change identity.
        var second = CompatibilityService.Import(b, new CompatibilityOptions
        {
            ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions,
            ProbeImageOpacity = asset => ImageAssetValidator.IsOpaque(asset, TestImages.Decoder)
        });
        Assert.Multiple(() =>
        {
            Assert.That(first.Document.StateEquals(second.Document), Is.True);
            Assert.That(second.Report.Diagnostics, Is.EqualTo(first.Report.Diagnostics));
            Assert.That(second.Report.Dimensions, Is.EqualTo(first.Report.Dimensions));
            Assert.That(first.Document.Layers.Select(x => x.Id).Distinct().Count(), Is.EqualTo(first.Document.Layers.Count));
            Assert.That(first.Document.Assets.Keys, Does.Not.Contain(Guid.Empty));
        });
        var native = Path.Combine(_directory, "migration.ppgproj");
        NativePackageStore.Save(native, first.Document, "p3-test");
        var reopened = NativePackageStore.Read(native);
        Assert.That(reopened.StateEquals(second.Document), Is.True);
        File.Delete(a);
        File.Delete(b);
        Assert.That(TestImages.RenderPng(reopened), Is.EqualTo(TestImages.RenderPng(first.Document)));
    }

    [TestCase(true, false)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    public void UnavailableImageGeometryCannotClaimExactOrNormalized(bool classic, bool throws)
    {
        var xml = classic ? Classic("") : Free("", $"<Background>{Image()}</Background>");
        var options = throws ? new CompatibilityOptions
        {
            ProbeImageSize = _ => throw new InvalidDataException("Unavailable decoder.")
        } : new CompatibilityOptions();
        var result = CompatibilityService.Import(Write("unprobed", xml), options);
        Assert.Multiple(() =>
        {
            Assert.That(result.Report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
            Assert.That(result.Report.Diagnostics.Any(d => d.Code == "LEGACY_ASSET_SIZE_UNPROBED" &&
                d.Severity == DiagnosticSeverity.Deviation), Is.True);
            Assert.That(result.Report.Dimensions.Single(d => d.Kind == CompatibilityDimensionKind.VisualLayout).Status,
                Is.EqualTo(DimensionStatus.Lossy));
        });
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(100)]
    public void LegalEmptyImageSizesImportWithExplicitCaptionNormalizationAndKeepTheSource(int height)
    {
        var xml = Free(Item("Image", 1)).Replace("<Height>100</Height>", $"<Height>{height}</Height>")
            .Replace("<Width>200</Width>", "<Width>1</Width>")
            .Replace("<Kind>Image</Kind>", "<Kind>Image</Kind><X>17</X><Y>23</Y><Visible>false</Visible><Locked>true</Locked>");
        var source = Write("tiny-placeholder.phifree", xml);
        var bytes = File.ReadAllBytes(source);
        var timestamp = File.GetLastWriteTimeUtc(source);
        var result = CompatibilityService.Import(source, Options);
        var plate = result.Document.Layers.Single(layer => layer.Kind == LayerKind.Shape);
        var caption = result.Document.Layers.Single(layer => layer.Kind == LayerKind.Text);
        var normalization = result.Report.Diagnostics.Where(d =>
            d.Code == "LEGACY_IMAGE_PLACEHOLDER_FONT_SIZE_NORMALIZED").ToList();
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(result.Document.Validate);
            Assert.That(caption.FontSize, Is.EqualTo(Math.Max(1, Math.Min(height / 6.0, 40))));
            Assert.That(caption.Bounds, Is.EqualTo(new RectD(17, 23, 1, height)));
            Assert.That(plate.Bounds, Is.EqualTo(caption.Bounds));
            Assert.That(caption.Text, Is.EqualTo("导入曲绘"));
            Assert.That(caption.GroupId, Is.EqualTo(plate.GroupId).And.Not.Null);
            Assert.That(caption.Visible, Is.False);
            Assert.That(caption.Locked, Is.True);
            Assert.That(normalization.Count, Is.EqualTo(height < 6 ? 1 : 0));
            Assert.That(normalization, Is.All.Matches<ConversionDiagnostic>(d =>
                d.Severity == DiagnosticSeverity.Deviation && d.LayerId == plate.Id));
            Assert.That(result.Report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(timestamp));
        });
        var native = Path.Combine(_directory, "placeholder.ppgproj");
        NativePackageStore.Save(native, result.Document, "p3-test");
        Assert.That(NativePackageStore.Read(native).StateEquals(result.Document), Is.True);
        Assert.That(() => CompatibilityService.Import(source, new CompatibilityOptions
        {
            ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions,
            RejectLossy = true
        }), Throws.TypeOf<CompatibilityException>().With.Property("Kind")
            .EqualTo(CompatibilityErrorKind.UnsupportedFeature));
    }

    [TestCase(400, 200)]
    [TestCase(200, 400)]
    public void FixedBackgroundCenterCoverMatchesCurrentGeometryAndDoesNotInventALoss(int width, int height)
    {
        // Coloured outer bands are cropped away; stretching would leave them visible.
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var along = width > height ? x : y;
            var at = (y * width + x) * 4;
            pixels[at] = along >= 300 ? (byte)255 : (byte)0;
            pixels[at + 1] = along is >= 100 and < 300 ? (byte)255 : (byte)0;
            pixels[at + 2] = along < 100 ? (byte)255 : (byte)0;
            pixels[at + 3] = 255;
        }
        var image = TestImages.EncodePng(width, height, pixels);
        var source = Write("cover.phifree", Free("", $"<Background>{Convert.ToBase64String(image)}</Background>")
            .Replace("<Width>800</Width>", "<Width>320</Width>")
            .Replace("<Height>400</Height>", "<Height>320</Height>"));
        var bytes = File.ReadAllBytes(source);
        var timestamp = File.GetLastWriteTimeUtc(source);
        var result = CompatibilityService.Import(source, new CompatibilityOptions
        {
            ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions,
            ProbeImageOpacity = asset => ImageAssetValidator.IsOpaque(asset, TestImages.Decoder),
            RejectLossy = true
        });
        Assert.Multiple(() =>
        {
            Assert.That(result.Report.Level, Is.EqualTo(CompatibilityLevel.Exact));
            Assert.That(result.Report.Diagnostics.Select(d => d.Code),
                Does.Not.Contain("LEGACY_FREE_BACKGROUND_FIT_DEVIATION"));
            Assert.That(result.Report.Diagnostics.Any(d => d.Severity is
                DiagnosticSeverity.Loss or DiagnosticSeverity.Deviation), Is.False);
            Assert.That(result.Document.Layers, Is.Empty);
            Assert.That(result.Document.BackgroundBlend.Enabled, Is.False);
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(timestamp));
        });
        var (renderWidth, renderHeight, rendered) = TestImages.DecodePng(TestImages.RenderPng(result.Document));
        Assert.That((renderWidth, renderHeight), Is.EqualTo((320, 320)));
        foreach (var coordinate in new[] { 10, 160, 309 })
        {
            var at = (width > height ? 160 * renderWidth + coordinate : coordinate * renderWidth + 160) * 4;
            Assert.That(rendered[at..(at + 4)], Is.EqualTo(new byte[] { 0, 255, 0, 255 }),
                $"centre cover at {coordinate}");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SoftBackgroundKeepsTheActualUnmigratedCompositionLoss(bool fixedBackground)
    {
        var source = Write("soft.phifree", Free("", fixedBackground ? $"<Background>{Image()}</Background>" : "")
            .Replace("<SoftBackground>false</SoftBackground>", "<SoftBackground>true</SoftBackground>"));
        var result = CompatibilityService.Import(source, Options);
        var loss = result.Report.Diagnostics.Single(d => d.Code == "LEGACY_FREE_SOFT_BACKGROUND_NOT_REPRESENTED");
        Assert.Multiple(() =>
        {
            Assert.That(loss.Severity, Is.EqualTo(DiagnosticSeverity.Loss));
            Assert.That(loss.Message, Does.Contain("not migrated").And.Contain("composition is available"));
            Assert.That(loss.Message, Does.Not.Contain("no background composition stage"));
            Assert.That(result.Document.BackgroundBlend.Enabled, Is.False);
            Assert.That(result.Report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
            Assert.That(result.Report.Dimensions.Single(d => d.Kind == CompatibilityDimensionKind.EditableSemantics).Status,
                Is.EqualTo(DimensionStatus.Lossy));
        });
        Assert.That(() => CompatibilityService.Import(source, new CompatibilityOptions
        {
            ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions,
            RejectLossy = true
        }), Throws.TypeOf<CompatibilityException>().With.Property("Kind")
            .EqualTo(CompatibilityErrorKind.UnsupportedFeature));
    }

    [Test]
    public void ChangedInputAndDifferentGeneratedRolesHaveDifferentIds()
    {
        var first = CompatibilityService.Import(Write("a", Classic(Label())), Options).Document;
        var second = CompatibilityService.Import(Write("b", Classic(Label()).Replace("author", "other")), Options).Document;
        Assert.That(first.FixedBackgroundAssetId, Is.Not.EqualTo(second.FixedBackgroundAssetId));
        Assert.That(first.Layers.Select(x => x.Id).Intersect(second.Layers.Select(x => x.Id)), Is.Empty);
        Assert.That(first.Layers.Select(x => x.Id).Append(first.FixedBackgroundAssetId!.Value).Distinct().Count(),
            Is.EqualTo(first.Layers.Count + 1));
    }

    [TestCase("<Future>value</Future>")]
    [TestCase("<Future>value</Future><Another>value</Another>")]
    [TestCase("<BackgroundColor custom='value'>-1</BackgroundColor>")]
    [TestCase("<BackgroundColor>-1<Nested>value</Nested></BackgroundColor>")]
    public void DiscardedUnknownFieldsAlwaysLowerTheReportAndCanBeRejected(string unknown)
    {
        // Nested text under a numeric field must remain numerically valid to isolate the loss policy.
        unknown = unknown.Replace("<Nested>value</Nested>", "<Nested />");
        var path = Write("unknown", Free("", unknown));
        var result = CompatibilityService.Import(path, Options);
        Assert.Multiple(() =>
        {
            Assert.That(result.Report.Diagnostics.Where(x => x.Code == "LEGACY_UNKNOWN_FIELD"),
                Is.Not.Empty.And.All.Matches<ConversionDiagnostic>(x => x.Severity == DiagnosticSeverity.Loss));
            Assert.That(result.Report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
            Assert.That(result.Report.Dimensions.Single(x => x.Kind == CompatibilityDimensionKind.EditableSemantics).Status,
                Is.EqualTo(DimensionStatus.Lossy));
        });
        Assert.That(() => CompatibilityService.Import(path, new CompatibilityOptions { RejectLossy = true }),
            Throws.TypeOf<CompatibilityException>());
    }

    [TestCase("root-attribute")]
    [TestCase("layer-attribute")]
    [TestCase("nested-element")]
    [TestCase("container-element")]
    public void UnknownXmlAtEachScopeIsReported(string scope)
    {
        var xml = XDocument.Parse(Free(Item("Text", 1)));
        switch (scope)
        {
            case "root-attribute": xml.Root!.SetAttributeValue("future", "value"); break;
            case "layer-attribute": xml.Descendants("FreeLayer").Single().SetAttributeValue("future", "value"); break;
            case "nested-element": xml.Descendants("Text").Single().Add(new XElement("Future", "value")); break;
            case "container-element": xml.Root!.Element("Layers")!.Add(new XElement("Future", "value")); break;
        }
        var result = CompatibilityService.Import(Write("unknown", xml.ToString()), Options);
        Assert.That(result.Report.Diagnostics, Has.Some.Matches<ConversionDiagnostic>(
            x => x.Code == "LEGACY_UNKNOWN_FIELD" && x.Severity == DiagnosticSeverity.Loss));
    }

    [Test]
    public void DuplicateKnownXmlFieldsAreRejectedInsteadOfSilentlyDiscarded()
    {
        Assert.That(() => CompatibilityService.Import(Write("duplicate", Free("", "<Width>800</Width>")), Options),
            Throws.TypeOf<CompatibilityException>().With.Property("Kind").EqualTo(CompatibilityErrorKind.Malformed));
    }

    [TestCase(100)]
    [TestCase(4095)]
    [TestCase(4096)]
    [TestCase(4097)]
    [TestCase(65536)]
    public void FileRouterDetectsCompleteJsonAcrossTheFormerPrefixBoundary(int length)
    {
        const string json = "{\"FormatVersion\":1,\"Document\":{}}";
        var path = Write("project.anything", json.PadRight(length));
        Assert.That(new FileInfo(path).Length, Is.EqualTo(length));
        Assert.That(LegacyFormatDetector.DetectFile(path), Is.EqualTo(LegacyFormatKind.TemporaryJson));
        File.AppendAllText(path, "not a valid JSON tail");
        Assert.That(LegacyFormatDetector.DetectFile(path), Is.EqualTo(LegacyFormatKind.Unknown));
    }

    [TestCase("{\"FormatVersion\":1,\"Document\":")]
    [TestCase("{\"FormatVersion\":1,\"Document\":{},}")]
    [TestCase("{\"foreign\":1}")]
    [TestCase("{\"FormatVersion\":\"future\",\"Document\":{}}")]
    [TestCase("arbitrary binary \\u0001")]
    public void InvalidAndForeignInputsNeverBecomeTemporaryJson(string content)
    {
        Assert.That(LegacyFormatDetector.DetectFile(Write("unknown", content)), Is.EqualTo(LegacyFormatKind.Unknown));
    }

    [Test]
    public void ActualDesktopRouterOpensLargeP1JsonAndRejectsFutureVersionTransactionally()
    {
        var document = new EditorDocument();
        for (var i = 0; i < 12; i++) document.Layers.Add(new Layer { Kind = LayerKind.Text, Text = new string('文', 500) });
        var path = Path.Combine(_directory, "large.json");
        NativeDocumentStore.Save(path, document);
        Assert.That(new FileInfo(path).Length, Is.GreaterThan(4096));
        var (native, router, paths) = Workflow();
        router.Open(path);
        Assert.Multiple(() =>
        {
            Assert.That(native.Session.Document.StateEquals(document), Is.True);
            Assert.That(native.IsDirty, Is.True);
            Assert.That(native.CurrentPath, Is.Null);
            Assert.That(paths.Contains(path), Is.True);
        });
        var before = native.Session.Document.Copy();
        var source = router.SourceLabel;
        File.WriteAllText(path, File.ReadAllText(path).Replace($"\"FormatVersion\": {NativeDocumentStore.CurrentVersion}", "\"FormatVersion\": 99"));
        Assert.That(LegacyFormatDetector.DetectFile(path), Is.EqualTo(LegacyFormatKind.TemporaryJson));
        Assert.Throws<InvalidDataException>(() => router.Open(path));
        Assert.That(native.Session.Document.StateEquals(before), Is.True);
        Assert.That(router.SourceLabel, Is.EqualTo(source));
    }

    [Test]
    public void DetectionPrecedenceUsesContentAndKeepsNativeFailuresNative()
    {
        Assert.That(LegacyFormatDetector.DetectFile(Write("native.ppgproj", Classic(""))), Is.EqualTo(LegacyFormatKind.ClassicPreview));
        Assert.That(LegacyFormatDetector.DetectFile(Write("free.ppgproj", Free(""))), Is.EqualTo(LegacyFormatKind.FreeLayout));
        var path = Path.Combine(_directory, "native.phifree");
        NativePackageStore.Save(path, new EditorDocument(), "p3-test");
        Assert.That(LegacyFormatDetector.DetectFile(path), Is.EqualTo(LegacyFormatKind.Native));
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("project.json")!;
            string json;
            using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("project.json").Open());
            writer.Write(json.Replace($"\"schemaVersion\": {NativePackageStore.SchemaVersion}", "\"schemaVersion\": 99"));
        }
        Assert.That(LegacyFormatDetector.DetectFile(path), Is.EqualTo(LegacyFormatKind.Native));
        var (_, router, _) = Workflow();
        Assert.Throws<InvalidDataException>(() => router.Open(path));
    }

    [TestCase(LegacyFormatKind.ClassicPreview, -1)]
    [TestCase(LegacyFormatKind.ClassicPreview, 0)]
    [TestCase(LegacyFormatKind.ClassicPreview, 1)]
    [TestCase(LegacyFormatKind.FreeLayout, -1)]
    [TestCase(LegacyFormatKind.FreeLayout, 0)]
    [TestCase(LegacyFormatKind.FreeLayout, 1)]
    [TestCase(LegacyFormatKind.TemporaryJson, -1)]
    [TestCase(LegacyFormatKind.TemporaryJson, 0)]
    [TestCase(LegacyFormatKind.TemporaryJson, 1)]
    public void EncodedSizePolicyHasInclusiveBoundaries(LegacyFormatKind kind, int offset)
    {
        var limit = kind switch
        {
            LegacyFormatKind.ClassicPreview => CompatibilityInputLimits.ClassicBytes,
            LegacyFormatKind.TemporaryJson => CompatibilityInputLimits.TemporaryJsonBytes,
            _ => CompatibilityInputLimits.FreeBytes
        };
        if (offset <= 0) Assert.DoesNotThrow(() => CompatibilityInputLimits.Check(limit + offset, kind));
        else Assert.That(() => CompatibilityInputLimits.Check(limit + offset, kind),
            Throws.TypeOf<CompatibilityException>().With.Property("Kind").EqualTo(CompatibilityErrorKind.TooLarge));
    }

    [TestCase("classic")]
    [TestCase("free")]
    [TestCase("json")]
    public void OversizedFilesAreRejectedBeforeWholeInputAllocation(string kind)
    {
        var path = Oversized(kind);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.That(() => LegacyFormatDetector.DetectFile(path),
            Throws.TypeOf<CompatibilityException>().With.Property("Kind").EqualTo(CompatibilityErrorKind.TooLarge));
        Assert.That(() => CompatibilityService.Import(path, Options),
            Throws.TypeOf<CompatibilityException>().With.Property("Kind").EqualTo(CompatibilityErrorKind.TooLarge));
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(2 * 1024 * 1024),
            "preflight must not allocate a document-sized byte array/string");
    }

    private string Oversized(string kind)
    {
        var path = Write(kind + "-large", kind == "classic" ? "<Project>" : kind == "free" ? "<FreeProject>" : "{");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        stream.SetLength((kind == "classic" ? CompatibilityInputLimits.ClassicBytes :
            kind == "json" ? CompatibilityInputLimits.TemporaryJsonBytes : CompatibilityInputLimits.FreeBytes) + 1);
        return path;
    }

    private static string Expanded(string kind, int count)
    {
        if (kind == "classic")
            return Classic(string.Concat(Enumerable.Repeat(Label(), count / 3)) +
                string.Concat(Enumerable.Repeat(Label(false), count % 3)));
        var items = new List<string>();
        for (var i = 0; i < count / 2; i++)
            items.Add(Item(kind == "mixed" && i % 2 == 0 ? "Image" : "Tag", i));
        if (count % 2 != 0) items.Add(Item("Shape", count));
        return Free(string.Concat(items));
    }

    [TestCase("classic", 199)]
    [TestCase("classic", 200)]
    [TestCase("classic", 201)]
    [TestCase("tag", 199)]
    [TestCase("tag", 200)]
    [TestCase("tag", 201)]
    [TestCase("mixed", 199)]
    [TestCase("mixed", 200)]
    [TestCase("mixed", 201)]
    public void ConversionEnforcesExpandedNativeLayerLimit(string kind, int count)
    {
        var path = Write("expanded", Expanded(kind, count));
        if (count <= 200)
        {
            var result = CompatibilityService.Import(path, Options);
            Assert.That(result.Document.Layers.Count, Is.EqualTo(count));
            Assert.DoesNotThrow(result.Document.Validate);
        }
        else Assert.That(() => CompatibilityService.Import(path, Options),
            Throws.TypeOf<CompatibilityException>().With.Property("Kind").EqualTo(CompatibilityErrorKind.TooLarge)
                .And.Message.Contains("Conversion expands project beyond native layer limit"));
    }

    [Test]
    public void GeneratedFieldBoundsAreValidatedBeforeReturningTheConversion()
    {
        var xml = Free(Item("Tag", 1)).Replace("<Name>item</Name>", $"<Name>{new string('n', 200)}</Name>");
        Assert.That(() => CompatibilityService.Import(Write("long-name", xml), Options),
            Throws.TypeOf<CompatibilityException>().With.Property("Kind").EqualTo(CompatibilityErrorKind.InvalidGeometry));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ExactAndNormalizedNeverHideKnownPresentationLoss(bool stableLegacyId)
    {
        var xml = Free(Item("Shape", 1), $"<Background>{Image()}</Background>");
        if (stableLegacyId) xml = xml.Replace("<Id>1</Id>", "<Id>aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa</Id>")
            .Replace("<Group>song</Group>", "<Group />");
        var report = CompatibilityService.Import(Write("classification", xml), Options).Report;
        Assert.That(report.Level, Is.EqualTo(stableLegacyId ? CompatibilityLevel.Exact : CompatibilityLevel.Normalized));
        Assert.That(report.Diagnostics.Any(x => x.Severity is DiagnosticSeverity.Loss or DiagnosticSeverity.Deviation), Is.False);
    }

    [Test]
    public void RendererDeviationIsLossyWithoutApprovingItsAcceptability()
    {
        var path = Write("deviation", Free(Item("Shape", 1, "<Slanted>true</Slanted>")));
        var report = CompatibilityService.Import(path, Options).Report;
        Assert.That(report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
        Assert.That(report.Dimensions.Single(x => x.Kind == CompatibilityDimensionKind.VisualLayout).Status,
            Is.EqualTo(DimensionStatus.Lossy));
        Assert.Throws<CompatibilityException>(() => CompatibilityService.Import(path,
            new CompatibilityOptions { RejectLossy = true }));
    }

    [TestCase(true, -1)]
    [TestCase(true, 0)]
    [TestCase(false, -1)]
    [TestCase(false, 0)]
    public void FilePreflightAcceptsXmlExactlyAtAndBelowItsLimit(bool classic, int offset)
    {
        var path = Write("boundary", classic ? "<Project>" : "<FreeProject>");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            stream.SetLength((classic ? CompatibilityInputLimits.ClassicBytes : CompatibilityInputLimits.FreeBytes) + offset);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.That(LegacyFormatDetector.DetectFile(path),
            Is.EqualTo(classic ? LegacyFormatKind.ClassicPreview : LegacyFormatKind.FreeLayout));
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(2 * 1024 * 1024));
    }

    [TestCase(199)]
    [TestCase(200)]
    public void BackgroundPlusImageAssetsMustAlsoFitTheNativeLimit(int imageCount)
    {
        var encoded = Image();
        var xml = Free(string.Concat(Enumerable.Range(1, imageCount)
            .Select(i => Item("Image", i, $"<Image>{encoded}</Image>"))), $"<Background>{encoded}</Background>");
        var path = Write("asset-limit", xml);
        if (imageCount == 199)
            Assert.That(CompatibilityService.Import(path, Options).Document.Assets.Count, Is.EqualTo(200));
        else Assert.That(() => CompatibilityService.Import(path, Options),
            Throws.TypeOf<CompatibilityException>().With.Property("Kind").EqualTo(CompatibilityErrorKind.InvalidGeometry));
    }

    [Test]
    public void UnrepresentedClassicMetadataAndFreeBadgeOrderAreExplicitLosses()
    {
        var classic = CompatibilityService.Import(Write("hidden", Classic(Label()).Replace(
            "<ShowAuthor>true</ShowAuthor>", "<ShowAuthor>false</ShowAuthor>")), Options);
        Assert.That(classic.Report.Diagnostics, Has.Some.Matches<ConversionDiagnostic>(
            d => d.Code == "LEGACY_HIDDEN_LABEL_FIELDS_NOT_REPRESENTED" && d.Severity == DiagnosticSeverity.Loss));
        Assert.That(classic.Report.Diagnostics, Has.Some.Matches<ConversionDiagnostic>(
            d => d.Code == "LEGACY_SOURCE_NAME_NOT_REPRESENTED" && d.Severity == DiagnosticSeverity.Loss));
        var free = CompatibilityService.Import(Write("badge", Free(Item("Badge", 1))), Options);
        Assert.That(free.Report.Diagnostics, Has.Some.Matches<ConversionDiagnostic>(
            d => d.Code == "LEGACY_FREE_BADGE_ORDER_NOT_REPRESENTED" && d.Severity == DiagnosticSeverity.Loss));
    }

    private static (NativeProjectWorkflow Native, ProjectOpenWorkflow Router, ProtectedSourcePaths Paths) Workflow()
    {
        var native = new NativeProjectWorkflow(new EditSession(new EditorDocument()), TestImages.ValidateImages, "p3-test");
        var paths = new ProtectedSourcePaths();
        return (native, new ProjectOpenWorkflow(native, TestImages.ValidateImages, Options, paths), paths);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SuccessfulNewOrNativeOpenClearsEveryConversionField(bool openNative)
    {
        var (native, router, paths) = Workflow();
        router.Open(Write("legacy", Free(Item("Tag", 1))));
        Assert.That(router.SourceLabel, Is.Not.Null);
        Assert.That(router.Details, Is.Not.Empty);
        if (openNative)
        {
            var path = Path.Combine(_directory, "saved.ppgproj");
            native.SaveAs(path);
            router.Open(path);
        }
        else router.New();
        Assert.Multiple(() =>
        {
            Assert.That(router.SourceLabel, Is.Null);
            Assert.That(router.Details, Is.Empty);
            Assert.That(router.Summary, Is.EqualTo(openNative ? "Project loaded." : "Ready."));
            Assert.That(paths.Count, Is.Zero);
            Assert.That(native.IsDirty, Is.False);
        });
    }

    [TestCase("malformed")]
    [TestCase("oversized")]
    [TestCase("expansion")]
    [TestCase("future")]
    public void FailedOpenKeepsDirtyNativeSessionAndConversionState(string failure)
    {
        var (native, router, paths) = Workflow();
        var source = Write("original", Free(Item("Text", 1)));
        router.Open(source);
        var saved = Path.Combine(_directory, "saved.ppgproj");
        native.SaveAs(saved);
        native.Session.AddLayer(new Layer { Kind = LayerKind.Text, Text = "edited" });
        native.Session.AddLayer(new Layer { Kind = LayerKind.Text, Text = "redo entry" });
        native.Session.Undo();
        var before = native.Session.Document.Copy();
        var undo = native.Session.UndoCount;
        var redo = native.Session.RedoCount;
        var selection = native.Session.SelectedLayerId;
        var summary = router.Summary;
        var details = router.Details.ToArray();
        var bad = failure switch
        {
            "malformed" => Write("bad", "<FreeProject><Layers>"),
            "oversized" => Oversized("free"),
            "expansion" => Write("bad", Expanded("tag", 201)),
            _ => Write("bad", Free("").Replace("<FreeProject>", "<FreeProject><Version>99</Version>"))
        };
        Assert.Throws<CompatibilityException>(() => router.Open(bad));
        Assert.Multiple(() =>
        {
            Assert.That(native.Session.Document.StateEquals(before), Is.True);
            Assert.That(native.Session.UndoCount, Is.EqualTo(undo));
            Assert.That(native.Session.RedoCount, Is.EqualTo(redo));
            Assert.That(native.Session.SelectedLayerId, Is.EqualTo(selection));
            Assert.That(native.CurrentPath, Is.EqualTo(saved));
            Assert.That(native.IsDirty, Is.True);
            Assert.That(router.SourceLabel, Is.EqualTo(Path.GetFileName(source)));
            Assert.That(router.Summary, Is.EqualTo(summary));
            Assert.That(router.Details, Is.EqualTo(details));
            Assert.That(paths.Contains(source), Is.True);
        });
        var sourceBytes = File.ReadAllBytes(source);
        Assert.Throws<InvalidOperationException>(() => paths.EnsureNotProtected(source));
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(sourceBytes));
    }
}
