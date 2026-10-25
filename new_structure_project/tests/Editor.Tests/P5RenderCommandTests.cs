using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5RenderCommandTests
{
    [TestCase("")]
    [TestCase("--render")]
    [TestCase("--render|input")]
    [TestCase("--render|input|output.png|--wrong")]
    [TestCase("--render|input|output.png|--transparent|extra")]
    [TestCase("--render| |output.png")]
    [TestCase("--wrong|input|output.png")]
    [TestCase("--render|input|output.png|--width")]
    [TestCase("--render|input|output.png|--width|0")]
    [TestCase("--render|input|output.png|--width|-1")]
    [TestCase("--render|input|output.png|--width|16385")]
    [TestCase("--render|input|output.png|--width|1.5")]
    [TestCase("--render|input|output.png|--width|2147483648")]
    [TestCase("--render|input|output.png|--width|640|--width|640")]
    [TestCase("--render|input|output.png|--transparent|--transparent")]
    public void InvalidArgumentsReturnUsageWithoutInitializingGraphics(string command)
    {
        var error = new StringWriter();
        var output = new StringWriter();
        var code = RenderCommand.Run(command.Split('|'), output, error,
            () => throw new AssertionException("Unexpected graphics initialization"));
        Assert.That(code, Is.EqualTo(2));
        Assert.That(error.ToString(), Does.Contain("Usage:"));
        Assert.That(output.ToString(), Is.Empty);
    }

    [TestCase("native", false)]
    [TestCase("native", true)]
    [TestCase("json", false)]
    [TestCase("json", true)]
    [TestCase("free", false)]
    [TestCase("free", true)]
    [TestCase("classic", false)]
    [TestCase("classic", true)]
    public void CommandMatchesDesktopWorkflowAndLeavesSourceBytesAndTimeUnchanged(string kind, bool transparent)
    {
        using var fixture = new Fixture();
        var source = fixture.Path("输入 中文." + kind);
        var destination = fixture.Path("输出 空格.PNG");
        var doc = new EditorDocument { CanvasWidth = 640, CanvasHeight = 480,
            Layers = [new Layer { Kind = LayerKind.Shape, Bounds = new(50, 40, 120, 60), ColorArgb = 0xFFCF1313 }] };
        if (kind == "native") NativePackageStore.Save(source, doc, "cli-test");
        else if (kind == "json") NativeDocumentStore.Save(source, doc);
        else if (kind == "classic") File.WriteAllText(source, $"""
            <Project><Version>1</Version><Background>{Convert.ToBase64String(TestImages.RenderPng(doc))}</Background>
            <Labels><ChartLabel><Name>中文歌曲</Name><Difficulty>IN</Difficulty><Level>15</Level>
            <Author>谱师</Author><BadgeX>400</BadgeX><BadgeY>200</BadgeY></ChartLabel></Labels></Project>
            """);
        else File.WriteAllText(source, """
            <FreeProject><Version>1</Version><Width>640</Width><Height>480</Height><SoftBackground>false</SoftBackground>
            <Layers><FreeLayer><Id>bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb</Id><Kind>Shape</Kind>
            <Name>曲绘</Name><X>50</X><Y>40</Y><Width>120</Width><Height>60</Height>
            <Slanted>true</Slanted><FutureField>keep report</FutureField></FreeLayer></Layers></FreeProject>
            """);
        var bytes = File.ReadAllBytes(source);
        var time = File.GetLastWriteTimeUtc(source);
        // Use the same route that the GUI uses; a source suffix is not a format detector.
        var session = new EditSession(new EditorDocument());
        var native = new NativeProjectWorkflow(session, TestImages.ValidateImages, "test");
        new ProjectOpenWorkflow(native, TestImages.ValidateImages,
            new Editor.Formats.Compatibility.CompatibilityOptions { ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions },
            new ProtectedSourcePaths()).Open(source);
        var expected = TestImages.RenderPng(session.Document, transparent);
        File.WriteAllBytes(destination, [9, 8, 7]);
        var (code, output, error) = Run(source, destination, transparent);
        Assert.That(code, Is.Zero, error);
        Assert.That(File.ReadAllBytes(destination), Is.EqualTo(expected));
        Assert.That(output, Does.Contain("PNG exported:").And.Contain("640 x 480"));
        if (kind == "free") Assert.That(error, Does.Contain("FutureField"));
        if (kind == "classic") Assert.That(error, Does.Contain("LEGACY_CLASSIC_BADGE_STYLE_DEVIATION"));
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(time));
    }

    [Test]
    public void WarningsAreReportedWithoutBlockingOrRepairingTheDocument()
    {
        using var fixture = new Fixture();
        var source = fixture.Path("warnings.ppgproj");
        var doc = new EditorDocument { CanvasWidth = 320, CanvasHeight = 180, Layers = [
            new Layer { Kind = LayerKind.Shape, Name = "hidden", Visible = false },
            new Layer { Kind = LayerKind.Shape, Name = "clipped", Bounds = new(-10, 0, 30, 30) },
            new Layer { Kind = LayerKind.Text, Name = "font", Text = "abc",
                FontFamily = LocalFontRegistry.Prefix + new string('0', 64) + "#Missing" }
        ] };
        NativePackageStore.Save(source, doc, "cli-test");
        var destination = fixture.Path("warnings.png");
        var result = Run(source, destination);
        Assert.That(result.Code, Is.Zero, result.Error);
        Assert.That(result.Error, Does.Contain("HiddenLayer").And.Contain("ClippedByCanvas").And.Contain("MissingLocalFont"));
        Assert.That(File.ReadAllBytes(destination), Is.EqualTo(TestImages.RenderPng(doc)));
        Assert.That(NativePackageStore.Read(source).StateEquals(doc), Is.True);
    }

    [Test]
    public void LowResolutionWarningUsesTheRequestedOutputWidth()
    {
        using var fixture = new Fixture();
        var source = fixture.Path("resolution.ppgproj");
        var asset = Guid.NewGuid();
        var doc = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        doc.AddAsset(asset, TestImages.EncodePng(400, 240,
            TestImages.SyntheticBgra(400, 240)));
        doc.Layers.Add(new Layer
        {
            Kind = LayerKind.Image, Name = "低分辨率曲绘", AssetId = asset,
            Bounds = new RectD(100, 100, 800, 480)
        });
        doc.Validate();
        NativePackageStore.Save(source, doc, "cli-test");
        AvaloniaTestHost.EnsureStarted();
        var largeOutput = new StringWriter();
        var largeError = new StringWriter();
        Assert.That(RenderCommand.Run(["--render", source,
            fixture.Path("large.png"), "--width", "1000"],
            largeOutput, largeError, () => { }), Is.Zero, largeError.ToString());
        Assert.That(largeError.ToString(), Does.Contain("LowResolutionArtwork"));
        var blockedOutput = new StringWriter();
        var blockedError = new StringWriter();
        Assert.That(RenderCommand.Run(["--render", source,
            fixture.Path("blocked.png"), "--width", "1000", "--block-low-resolution"],
            blockedOutput, blockedError, () => { }), Is.EqualTo(1));
        Assert.That(blockedError.ToString(), Does.Contain("RENDER BLOCKED"));
        Assert.That(File.Exists(fixture.Path("blocked.png")), Is.False);
        var smallOutput = new StringWriter();
        var smallError = new StringWriter();
        Assert.That(RenderCommand.Run(["--render", source,
            fixture.Path("small.png"), "--width", "500"],
            smallOutput, smallError, () => { }), Is.Zero, smallError.ToString());
        Assert.That(smallError.ToString(), Does.Not.Contain("LowResolutionArtwork"));
    }

    [TestCase("missing")]
    [TestCase("corrupt")]
    [TestCase("bad-image")]
    [TestCase("future")]
    [TestCase("locked-output")]
    [TestCase("directory-output")]
    public void FailureReturnsNonzeroAndPreservesExistingOutput(string fault)
    {
        using var fixture = new Fixture();
        var source = fixture.Path("input.ppgproj");
        var destination = fixture.Path("output.png");
        var doc = new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 };
        if (fault == "bad-image")
        {
            var id = Guid.NewGuid(); doc.AddAsset(id, [1, 2, 3]);
            doc.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = id });
        }
        if (fault != "missing") NativePackageStore.Save(source, doc, "cli-test");
        if (fault == "corrupt") File.WriteAllText(source, "not a project");
        if (fault == "future") File.WriteAllText(source, "{\"FormatVersion\":999,\"Document\":{}}");
        if (fault == "directory-output") Directory.CreateDirectory(destination);
        else File.WriteAllBytes(destination, [9, 8, 7]);
        using var locked = fault == "locked-output" ? new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var result = Run(source, destination);
        Assert.That(result.Code, Is.EqualTo(1));
        Assert.That(result.Error, Does.Contain("RENDER FAILED:"));
        Assert.That(result.Output, Does.Not.Contain("PNG exported:"));
        if (fault != "directory-output") Assert.That(File.ReadAllBytes(destination), Is.EqualTo(new byte[] { 9, 8, 7 }));
        Assert.That(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SameSourcePathIsRejectedEvenForProjectNamedPng(bool changeCase)
    {
        using var fixture = new Fixture();
        var source = fixture.Path("source.png");
        NativePackageStore.Save(source, new EditorDocument(), "cli-test");
        var bytes = File.ReadAllBytes(source);
        var result = Run(source, changeCase ? source.ToUpperInvariant() : source);
        Assert.That(result.Code, Is.EqualTo(1));
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(bytes));
    }

    [Test]
    public void NonPngOutputAndPlatformInitializationFailureProduceReadableErrors()
    {
        var error = new StringWriter();
        var code = RenderCommand.Run(["--render", "input", "output.ppgproj"], new StringWriter(), error,
            () => throw new AssertionException("Unexpected initialization"));
        Assert.That(code, Is.EqualTo(1));
        Assert.That(error.ToString(), Does.Contain(".png"));
        error = new StringWriter();
        code = RenderCommand.Run(["--render", "input", "output.png"], new StringWriter(), error,
            () => throw new InvalidOperationException("platform unavailable"));
        Assert.That(code, Is.EqualTo(1));
        Assert.That(error.ToString(), Does.Contain("platform unavailable"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void WidthOptionMatchesSizedExportAndAcceptsEitherFlagOrder(bool transparentFirst)
    {
        using var fixture = new Fixture();
        var source = fixture.Path("尺寸.ppgproj"); var target = fixture.Path("尺寸.png");
        var doc = new EditorDocument { CanvasWidth = 640, CanvasHeight = 481,
            Layers = [EditorDocument.NewBadge(new(100, 100))] };
        NativePackageStore.Save(source, doc, "test");
        var bytes = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var output = new StringWriter(); var error = new StringWriter();
        string[] flags = transparentFirst ? ["--transparent", "--width", "800"] : ["--width", "800", "--transparent"];
        var code = RenderCommand.Run(["--render", source, target, .. flags], output, error, () => { });
        Assert.That(code, Is.Zero, error.ToString());
        Assert.That(File.ReadAllBytes(target), Is.EqualTo(TestImages.RenderPng(doc, true, 800)));
        Assert.That(output.ToString(), Does.Contain("800 x 601"));
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(time));
    }

    [Test]
    public void OversizedExportPreservesPriorOutput()
    {
        using var fixture = new Fixture();
        var source = fixture.Path("large.ppgproj"); var target = fixture.Path("existing.png");
        NativePackageStore.Save(source, new EditorDocument { CanvasWidth = 640, CanvasHeight = 640 }, "test");
        File.WriteAllBytes(target, [1, 2, 3]);
        var error = new StringWriter();
        var code = RenderCommand.Run(["--render", source, target, "--width", "10000"], new StringWriter(), error, () => { });
        Assert.That(code, Is.EqualTo(1));
        Assert.That(error.ToString(), Does.Contain("80 million"));
        Assert.That(File.ReadAllBytes(target), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    private static (int Code, string Output, string Error) Run(string source, string destination, bool transparent = false)
    {
        AvaloniaTestHost.EnsureStarted();
        var output = new StringWriter(); var error = new StringWriter();
        string[] args = transparent ? ["--render", source, destination, "--transparent"] : ["--render", source, destination];
        var code = RenderCommand.Run(args, output, error, () => { });
        return (code, output.ToString(), error.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "cli 中文 " + Guid.NewGuid().ToString("N"));
        public Fixture() { AvaloniaTestHost.EnsureStarted(); Directory.CreateDirectory(DirectoryPath); }
        public string Path(string name) => System.IO.Path.Combine(DirectoryPath, name);
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
