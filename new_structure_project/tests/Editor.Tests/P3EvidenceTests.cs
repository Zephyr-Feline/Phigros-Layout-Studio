using System.Text.Json;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats.Compatibility;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P3EvidenceTests
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
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p3-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown] public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void IdenticalPixelsProduceBlackHeatmapWithoutDifferenceBounds()
    {
        var source = new DecodedRaster(2, 2, Enumerable.Repeat((byte)45, 16).ToArray());
        var result = CompatibilityEvidence.Measure(source, source, out var heatmap);
        Assert.Multiple(() =>
        {
            Assert.That(result.SameDimensions, Is.True);
            Assert.That(result.ChangedPixels, Is.Zero);
            Assert.That(result.MeanChannelDelta, Is.Zero);
            Assert.That(result.ChangedBounds, Is.Null);
            Assert.That(heatmap!.Bgra, Is.EqualTo(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 }));
        });
    }

    [TestCase(0, 1)]
    [TestCase(1, 32)]
    [TestCase(2, 255)]
    [TestCase(3, 100)]
    public void DifferenceStatisticsIncludeEveryChannelAndExactPixelBounds(int channel, byte delta)
    {
        var source = new DecodedRaster(3, 2, new byte[24]);
        var changed = new byte[24]; changed[16 + channel] = delta;
        var result = CompatibilityEvidence.Measure(source, new(3, 2, changed), out var heatmap);
        Assert.Multiple(() =>
        {
            Assert.That(result.ChangedPixels, Is.EqualTo(1));
            Assert.That(result.ChangedPercent, Is.EqualTo(100.0 / 6).Within(0.000001));
            Assert.That(result.MeanChannelDelta, Is.EqualTo(delta / 24.0));
            Assert.That(result.MaxChannelDelta, Is.EqualTo(delta));
            Assert.That(result.ChangedBounds, Is.EqualTo(new RectD(1, 1, 1, 1)));
            Assert.That(heatmap!.Bgra[16], Is.EqualTo(Math.Min(255, delta * 8)));
            Assert.That(heatmap.Bgra[17], Is.Zero);
        });
    }

    [Test]
    public void DimensionMismatchIsReportedWithoutResamplingOrPercentage()
    {
        var result = CompatibilityEvidence.Measure(new(2, 1, new byte[8]), new(1, 2, new byte[8]), out var heatmap);
        Assert.Multiple(() =>
        {
            Assert.That(result.SameDimensions, Is.False);
            Assert.That(result.ReferenceWidth, Is.EqualTo(2));
            Assert.That(result.CandidateHeight, Is.EqualTo(2));
            Assert.That(result.ChangedPixels, Is.Null);
            Assert.That(result.ChangedPercent, Is.Null);
            Assert.That(heatmap, Is.Null);
        });
    }

    [TestCase(0, 1, 0)]
    [TestCase(2, 1, 4)]
    public void InvalidRastersCannotYieldAComparison(int width, int height, int bytes) =>
        Assert.Throws<InvalidDataException>(() => CompatibilityEvidence.Measure(new(width, height, new byte[bytes]), new(1, 1, new byte[4]), out _));

    [Test]
    public void FixturesAreDeterministicAndIncludeIndependentGeometryAndRejectionCases()
    {
        var first = CompatibilityEvidenceFixtures.Create();
        var second = CompatibilityEvidenceFixtures.Create();
        Assert.That(second.Select(item => item.Xml.ToString()), Is.EqualTo(first.Select(item => item.Xml.ToString())));
        Assert.That(first.Select(item => item.Id).Distinct().Count(), Is.EqualTo(first.Count));
        foreach (var fixture in first)
        {
            var path = Path.Combine(_directory, fixture.Id + fixture.Extension);
            fixture.Xml.Save(path);
            if (fixture.ExpectedError is { } expected)
                Assert.That(Assert.Throws<CompatibilityException>(() => CompatibilityService.Import(path, Options))!.Kind, Is.EqualTo(expected), fixture.Id);
            else
                Assert.DoesNotThrow(() => TestImages.ValidateImages(CompatibilityService.Import(path, Options).Document), fixture.Id);
        }
        Assert.That(first.Select(item => item.Id), Does.Contain("classic-4-3").And.Contain("free-placeholder-1").And.Contain("free-soft-5"));
    }

    [Test]
    public void TechnicalPassKeepsVisualAndWriterDecisionsPendingAndEscapesSourceFields()
    {
        var corpus = Path.Combine(_directory, "corpus"); Directory.CreateDirectory(corpus);
        var source = Path.Combine(corpus, "<unsafe-not-a-filename>.phifree".Replace('<', '_').Replace('>', '_'));
        var fixture = CompatibilityEvidenceFixtures.Create().Single(item => item.Id == "free-unknown-field");
        fixture.Xml.Root!.SetElementValue("FutureSetting", "<script>untrusted()</script>");
        fixture.Xml.Save(source);
        var before = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var output = Path.Combine(_directory, "evidence");
        Assert.That(CompatibilityEvidence.Run(output, RenderCandidateAsReference, corpus), Is.Zero);
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "compatibility-evidence.json")));
        var root = summary.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("PASS"));
            Assert.That(root.GetProperty("visualAcceptance").GetString(), Is.EqualTo("PENDING"));
            Assert.That(root.GetProperty("legacyWriterDecision").GetString(), Is.EqualTo("PENDING"));
            Assert.That(root.GetProperty("technicalFailures").GetInt32(), Is.Zero);
            Assert.That(root.GetProperty("local").GetInt32(), Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(before));
            Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(time));
        });
        var accepted = root.GetProperty("samples").EnumerateArray().Where(sample => sample.GetProperty("technicalStatus").GetString() == "PASS").ToArray();
        Assert.That(accepted.All(sample => sample.GetProperty("sourceUnchanged").GetBoolean()), Is.True);
        Assert.That(accepted.All(sample => sample.GetProperty("roundTrip").GetString()!.StartsWith("PASS")), Is.True);
        Assert.That(accepted.All(sample => sample.GetProperty("pixels").GetProperty("changedPixels").GetInt64() == 0), Is.True);
        var html = File.ReadAllText(Path.Combine(output, "index.html"));
        Assert.That(html, Does.Not.Contain("<script>untrusted()</script>"));
        Assert.That(html, Does.Contain("Content-Security-Policy").And.Contain("data:image/png;base64,"));
    }

    [TestCase("reference-exit")]
    [TestCase("source-mutation")]
    public void ReferenceFailureAndSourceMutationProduceReviewableFailureEvidence(string fault)
    {
        var output = Path.Combine(_directory, "evidence");
        int Render(string source, string target)
        {
            if (source.Contains("classic-16-9"))
            {
                if (fault == "reference-exit") return 9;
                File.AppendAllText(source, "<!-- changed -->");
            }
            return RenderCandidateAsReference(source, target);
        }
        Assert.That(CompatibilityEvidence.Run(output, Render), Is.EqualTo(1));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "compatibility-evidence.json")));
        Assert.That(summary.RootElement.GetProperty("status").GetString(), Is.EqualTo("FAILED"));
        Assert.That(summary.RootElement.GetProperty("technicalFailures").GetInt32(), Is.EqualTo(1));
        var failed = summary.RootElement.GetProperty("samples")[0];
        Assert.That(failed.GetProperty("technicalStatus").GetString(), Is.EqualTo("FAILED"));
        if (fault == "source-mutation") Assert.That(failed.GetProperty("sourceUnchanged").GetBoolean(), Is.False);
        Assert.That(File.Exists(Path.Combine(output, "index.html")), Is.True);
    }

    [TestCase("existing")]
    [TestCase("missing-corpus")]
    [TestCase("empty-corpus")]
    [TestCase("overlap")]
    public void UnsafeOrUnavailableInputsAreRejectedBeforeFixtureWriting(string fault)
    {
        var output = Path.Combine(_directory, "output");
        if (fault == "existing") { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "keep"), "original"); }
        if (fault == "existing") Assert.Throws<IOException>(() => CompatibilityEvidence.Run(output, RenderCandidateAsReference));
        if (fault == "missing-corpus") Assert.Throws<DirectoryNotFoundException>(() => CompatibilityEvidence.Run(output, RenderCandidateAsReference, Path.Combine(_directory, "missing")));
        if (fault == "overlap") Assert.Throws<IOException>(() => CompatibilityEvidence.Run(output, RenderCandidateAsReference, _directory));
        if (fault == "empty-corpus")
        {
            var corpus = Path.Combine(_directory, "empty"); Directory.CreateDirectory(corpus);
            Assert.Throws<InvalidDataException>(() => CompatibilityEvidence.Run(output, RenderCandidateAsReference, corpus));
        }
        if (fault == "existing") Assert.That(File.ReadAllText(Path.Combine(output, "keep")), Is.EqualTo("original"));
        else Assert.That(Directory.Exists(output), Is.False);
    }

    [Test]
    public void MissingReferenceExecutableIsRejectedBeforeOutputCreation()
    {
        var output = Path.Combine(_directory, "output");
        Assert.Throws<FileNotFoundException>(() => CompatibilityEvidence.Run(output, Path.Combine(_directory, "missing.exe")));
        Assert.That(Directory.Exists(output), Is.False);
    }

    private static int RenderCandidateAsReference(string source, string target)
    {
        File.WriteAllBytes(target, TestImages.RenderPng(CompatibilityService.Import(source, Options).Document));
        return 0;
    }
}
