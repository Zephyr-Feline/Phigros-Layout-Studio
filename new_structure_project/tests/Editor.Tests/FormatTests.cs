using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class FormatTests
{
    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p1-format-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void NativeRoundTripKeepsEditableStateButNotSelection()
    {
        var doc = new EditorDocument();
        var id = Guid.NewGuid();
        doc.AddAsset(id, [1, 2, 3, 4]);
        var image = new Layer { Kind = LayerKind.Image, AssetId = id, Bounds = new RectD(20, 30, 400, 300) };
        var text = new Layer { Kind = LayerKind.Text, Text = "中文 English\nline 2", FontSize = 42,
            Alignment = HorizontalAlignment.Center, LineSpacing = 1.4, Bounds = new RectD(80, 90, 550, 150) };
        doc.Layers.AddRange([image, text, EditorDocument.NewBadge(new PointD(900, 200))]);
        var path = Path.Combine(_directory, "空 格 P1.json");
        NativeDocumentStore.Save(path, doc);
        var loaded = NativeDocumentStore.Read(path);
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Layers.Select(x => x.Id), Is.EqualTo(doc.Layers.Select(x => x.Id)));
            Assert.That(loaded.Layers[1].Text, Is.EqualTo(text.Text));
            Assert.That(loaded.Layers[1].Alignment, Is.EqualTo(HorizontalAlignment.Center));
            Assert.That(loaded.Layers[1].LineSpacing, Is.EqualTo(1.4));
            Assert.That(loaded.Assets[id].ToArray(), Is.EqualTo(doc.Assets[id].ToArray()));
        });
    }

    [Test]
    public void P1JsonWithBase64AssetRemainsReadable()
    {
        var path = Path.Combine(_directory, "prior-p1.json");
        File.WriteAllText(path, """
            {"FormatVersion":1,"Document":{"Assets":{
              "11111111-1111-1111-1111-111111111111":"AQID"
            },"FixedBackgroundAssetId":"11111111-1111-1111-1111-111111111111"}}
            """);
        var read = NativeDocumentStore.Read(path);
        Assert.That(read.Assets[Guid.Parse("11111111-1111-1111-1111-111111111111")].ToArray(),
            Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void InvalidSaveDoesNotDamageExistingDocument()
    {
        var path = Path.Combine(_directory, "existing.json");
        var valid = new EditorDocument();
        NativeDocumentStore.Save(path, valid);
        var before = File.ReadAllBytes(path);
        var invalid = valid.Copy();
        invalid.CanvasWidth = 0;
        Assert.Throws<InvalidDataException>(() => NativeDocumentStore.Save(path, invalid));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
    }

    [Test]
    public void ClassicRepresentativeReadAndUnknownFieldDiagnostic()
    {
        var path = Path.Combine(_directory, "classic.phipreview");
        File.WriteAllText(path, """
            <Project><Version>1</Version><Background>AQID</Background><FutureField>untouched input</FutureField>
            <Labels><ChartLabel><Name>自制曲目</Name><Difficulty>IN</Difficulty><Level>15</Level>
            <Author>谱师甲</Author><BadgeX>1450</BadgeX><BadgeY>800</BadgeY>
            <AuthorX>1000</AuthorX><AuthorY>900</AuthorY><Scale>1</Scale><AuthorWidth>360</AuthorWidth>
            </ChartLabel></Labels></Project>
            """);
        var original = File.ReadAllBytes(path);
        var result = LegacyReader.Read(path);
        Assert.Multiple(() =>
        {
            Assert.That(result.Supported, Is.True);
            Assert.That(result.Document!.CanvasWidth, Is.EqualTo(1920));
            Assert.That(result.Document.Layers[0].Bounds.X, Is.EqualTo(1450));
            Assert.That(result.Document.Layers[0].Difficulty, Is.EqualTo("IN"));
            // P3 maps the classic author bar to a plate plus a text layer, so the prose is found by kind.
            Assert.That(result.Document.Layers.First(layer => layer.Kind == LayerKind.Text).Text,
                Is.EqualTo("谱师甲"));
            Assert.That(result.Diagnostics, Has.Some.Contains("FutureField"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
        });
    }

    [Test]
    public void FreeRepresentativeReadAndUnsupportedKindRejection()
    {
        var path = Path.Combine(_directory, "free.phifree");
        File.WriteAllText(path, """
            <FreeProject><Version>1</Version><Width>2740</Width><Height>1080</Height>
            <Layers><FreeLayer><Id>9edfb874b3ee45508ab9720557d4b116</Id><Kind>Text</Kind>
            <X>220</X><Y>50</Y><Width>400</Width><Height>100</Height>
            <Text>中文 English</Text><FontName>Microsoft YaHei UI</FontName>
            <FontSize>40</FontSize><Align>Center</Align><LineSpacing>1.3</LineSpacing>
            </FreeLayer><FreeLayer><Id>5f8c1fd5d24b46feb299a713180d440e</Id><Kind>Badge</Kind>
            <X>10</X><Y>20</Y><Width>256</Width><Height>216</Height>
            <Difficulty>HD</Difficulty><Level>12</Level></FreeLayer></Layers></FreeProject>
            """);
        var result = LegacyReader.Read(path);
        Assert.Multiple(() =>
        {
            Assert.That(result.Supported, Is.True);
            Assert.That(result.Document!.CanvasWidth, Is.EqualTo(2740));
            Assert.That(result.Document.Layers[0].Text, Is.EqualTo("中文 English"));
            Assert.That(result.Document.Layers[0].Alignment, Is.EqualTo(HorizontalAlignment.Center));
            Assert.That(result.Document.Layers[1].Bounds.Width, Is.EqualTo(256));
            Assert.That(result.Document.Layers[1].Difficulty, Is.EqualTo("HD"));
        });
        var invalid = File.ReadAllText(path).Replace("<Kind>Badge</Kind>", "<Kind>Star</Kind>");
        File.WriteAllText(path, invalid);
        var rejected = LegacyReader.Read(path);
        Assert.That(rejected.Supported, Is.False);
        Assert.That(rejected.Diagnostics, Has.Some.Contains("Unsupported FreeLayer Kind Star"));

        File.WriteAllText(path, invalid.Replace("<Width>2740</Width>", "<Width>invalid</Width>"));
        var badNumber = LegacyReader.Read(path);
        Assert.That(badNumber.Supported, Is.False);
        Assert.That(badNumber.Diagnostics, Has.Some.Contains("Invalid integer field Width"));
    }

    [Test]
    public void TinyLegacyBadgeIsImportedAtTheNativeMinimum()
    {
        var path = Path.Combine(_directory, "tiny.phifree");
        File.WriteAllText(path, """
            <FreeProject><Version>1</Version><Width>2740</Width><Height>1080</Height>
            <Layers><FreeLayer><Id>98d5c374861749a68669ac6b3c1e5511</Id>
            <Kind>Badge</Kind><X>1</X><Y>2</Y><Width>1</Width><Height>1</Height>
            <Difficulty>IN</Difficulty><Level>1</Level></FreeLayer></Layers></FreeProject>
            """);
        // P1 could not represent a 1 x 1 badge at all. P3 imports it at the native minimum width and
        // records the loss, because V2 validation requires badge width >= 8 (see P3_STATUS.md).
        var result = LegacyReader.Read(path);
        Assert.That(result.Supported, Is.True);
        var badge = result.Document!.Layers.Single(layer => layer.Kind == LayerKind.Badge);
        Assert.Multiple(() =>
        {
            Assert.That(badge.Bounds.Width, Is.EqualTo(8));
            Assert.That(badge.Bounds.Width / badge.Bounds.Height, Is.EqualTo(128.0 / 108.0).Within(1e-9));
            Assert.That(result.Diagnostics, Has.Some.Contains("native model requires at least 8 px"));
        });
    }
}
