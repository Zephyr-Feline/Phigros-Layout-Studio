using Editor.Core;
using Editor.Formats;
using Editor.Formats.Compatibility;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P3VisualBoundaryTests
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
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p3-visual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }
    [TearDown] public void TearDown() => Directory.Delete(_directory, true);

    [TestCase(5, false)]
    [TestCase(90, false)]
    [TestCase(90, true)]
    public void PlaceholderCaptionKeepsV1StyleAndAspectSettingsOnNativeReopen(int height, bool hidden)
    {
        var source = Write("placeholder", $"<FreeProject><Width>320</Width><Height>180</Height><SoftBackground>false</SoftBackground>" +
            $"<Layers><FreeLayer><Id>00000000000000000000000000000001</Id><Kind>Image</Kind><Width>240</Width><Height>{height}</Height>" +
            $"<Bold>true</Bold><Italic>true</Italic><LineSpacing>2.5</LineSpacing><KeepAspect>true</KeepAspect>" +
            $"<Visible>{(!hidden).ToString().ToLowerInvariant()}</Visible><Locked>true</Locked><VerticalAlign>Bottom</VerticalAlign>" +
            "<ColorArgb>-65536</ColorArgb></FreeLayer></Layers></FreeProject>");
        var before = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var imported = CompatibilityService.Import(source, Options);
        var caption = imported.Document.Layers.Single(layer => layer.Kind == LayerKind.Text);
        Assert.Multiple(() =>
        {
            Assert.That(caption.Bold, Is.True);
            Assert.That(caption.Italic, Is.True);
            Assert.That(caption.LineSpacing, Is.EqualTo(2.5));
            Assert.That(caption.KeepAspect, Is.True);
            Assert.That(caption.FontSize, Is.EqualTo(Math.Max(1, Math.Min(height / 6.0, 40))));
            Assert.That(caption.VerticalAlignment, Is.EqualTo(VerticalAlignment.Bottom));
            Assert.That(caption.ColorArgb, Is.EqualTo(0xFFFF0000));
            Assert.That(caption.Visible, Is.EqualTo(!hidden));
            Assert.That(caption.Locked, Is.True);
        });
        var native = Path.Combine(_directory, "saved.ppgproj");
        NativePackageStore.Save(native, imported.Document, "p3-boundary-test");
        var reopened = NativePackageStore.Read(native);
        Assert.That(reopened.StateEquals(imported.Document), Is.True);
        Assert.That(TestImages.RenderPng(reopened, true), Is.EqualTo(TestImages.RenderPng(imported.Document, true)));
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(before));
        Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(time));
    }

    [TestCase(0)]
    [TestCase(128)]
    public void TransparentFixedBackgroundCannotClaimExact(int alpha)
    {
        var bytes = Image(32, 32, alpha);
        var source = Write("background", "<FreeProject><Width>320</Width><Height>180</Height><SoftBackground>false</SoftBackground>" +
            $"<Background>{Convert.ToBase64String(bytes)}</Background><Layers /></FreeProject>");
        var result = CompatibilityService.Import(source, Options);
        Assert.That(result.Report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
        Assert.That(result.Report.Diagnostics.Select(item => item.Code), Does.Contain("LEGACY_FREE_BACKGROUND_ALPHA_DEVIATION"));
        Assert.That(result.Report.Dimensions.Single(item => item.Kind == CompatibilityDimensionKind.VisualLayout).Status,
            Is.EqualTo(DimensionStatus.Lossy));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MissingOrFailedOpacityEvidenceCannotClaimExact(bool throws)
    {
        var source = Write("unprobed", "<FreeProject><Width>320</Width><Height>180</Height><SoftBackground>false</SoftBackground>" +
            $"<Background>{Convert.ToBase64String(Image(32, 32, 255))}</Background><Layers /></FreeProject>");
        var options = new CompatibilityOptions
        {
            ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions,
            ProbeImageOpacity = throws ? _ => throw new InvalidDataException("decode unavailable") : null
        };
        var result = CompatibilityService.Import(source, options);
        Assert.That(result.Report.Level, Is.EqualTo(CompatibilityLevel.Lossy));
        Assert.That(result.Report.Diagnostics.Single(item => item.Code == "LEGACY_BACKGROUND_OPACITY_UNPROBED").Severity,
            Is.EqualTo(DiagnosticSeverity.Deviation));
        Assert.Throws<CompatibilityException>(() => CompatibilityService.Import(source,
            new CompatibilityOptions { ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions, RejectLossy = true }));
    }

    [TestCase(0)]
    [TestCase(128)]
    public void ClassicTransparencyIsReportedAndNeverFlattenedInTheStoredAsset(int alpha)
    {
        var bytes = Image(320, 180, alpha);
        var source = Write("classic", $"<Project><Background>{Convert.ToBase64String(bytes)}</Background><Labels /></Project>");
        var result = CompatibilityService.Import(source, Options);
        Assert.That(result.Report.Diagnostics.Select(item => item.Code), Does.Contain("LEGACY_CLASSIC_BACKGROUND_ALPHA_DEVIATION"));
        Assert.That(result.Document.Assets[result.Document.FixedBackgroundAssetId!.Value].ToArray(), Is.EqualTo(bytes));
    }

    [TestCase(-1, 255, true)]
    [TestCase(0, 0, false)]
    [TestCase(16, 128, false)]
    [TestCase(48, 254, false)]
    public void OpacityChecksTheWholeImageIncludingLastPartialStrip(int row, int alpha, bool opaque)
    {
        var pixels = Enumerable.Repeat((byte)255, 7 * 49 * 4).ToArray();
        if (row >= 0) pixels[(row * 7 + 6) * 4 + 3] = (byte)alpha;
        var bytes = TestImages.EncodePng(7, 49, pixels);
        Assert.That(ImageAssetValidator.IsOpaque(new AssetBlob(bytes), TestImages.Decoder), Is.EqualTo(opaque));
    }

    [Test]
    public void OpacityRejectsOversizedHeaderBeforeDecode()
    {
        var bytes = Image(2, 2, 255);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 32769);
        Assert.That(() => ImageAssetValidator.IsOpaque(new AssetBlob(bytes)),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("exceed the supported limit"));
    }

    [TestCase(0)]
    [TestCase(128)]
    public void StrictImportRefusesAlphaDeviationAndKeepsTheSource(int alpha)
    {
        var source = Write("strict", "<FreeProject><Width>320</Width><Height>180</Height><SoftBackground>false</SoftBackground>" +
            $"<Background>{Convert.ToBase64String(Image(32, 32, alpha))}</Background><Layers /></FreeProject>");
        var before = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var error = Assert.Throws<CompatibilityException>(() => CompatibilityService.Import(source, new CompatibilityOptions
        {
            ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions,
            ProbeImageOpacity = asset => ImageAssetValidator.IsOpaque(asset, TestImages.Decoder), RejectLossy = true
        }));
        Assert.That(error!.Kind, Is.EqualTo(CompatibilityErrorKind.UnsupportedFeature));
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(before));
        Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(time));
    }

    private string Write(string name, string xml)
    {
        var path = Path.Combine(_directory, name + ".phifree"); File.WriteAllText(path, xml); return path;
    }
    private static byte[] Image(int width, int height, int alpha)
    {
        var pixels = new byte[width * height * 4];
        for (var at = 0; at < pixels.Length; at += 4) { pixels[at + 1] = 200; pixels[at + 3] = (byte)alpha; }
        return TestImages.EncodePng(width, height, pixels);
    }
}
