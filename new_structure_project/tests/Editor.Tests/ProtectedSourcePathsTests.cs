using Editor.Application;
using Editor.Core;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>REQ-DOC-004: a save or export must never overwrite the file an image was imported from.</summary>
public sealed class ProtectedSourcePathsTests
{
    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p2-protected",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void ImportedSourcePathsAreRefusedAsOutputTargets()
    {
        var source = Path.Combine(_directory, "曲绘 底图.png");
        var other = Path.Combine(_directory, "导出 结果.ppgproj");
        var paths = new ProtectedSourcePaths();
        paths.Add(source);

        Assert.Multiple(() =>
        {
            Assert.That(paths.Count, Is.EqualTo(1));
            Assert.That(paths.Contains(source), Is.True);
            Assert.That(paths.Contains(other), Is.False);
            Assert.Throws<InvalidOperationException>(() => paths.EnsureNotProtected(source));
            Assert.DoesNotThrow(() => paths.EnsureNotProtected(other));
        });
    }

    [Test]
    public void ComparisonIgnoresCaseAndPathForm()
    {
        var source = Path.Combine(_directory, "Image.PNG");
        var paths = new ProtectedSourcePaths();
        paths.Add(source);
        var differentlyCased = Path.Combine(_directory.ToUpperInvariant(), "image.png");
        Assert.Multiple(() =>
        {
            Assert.That(paths.Contains(differentlyCased), Is.True);
            Assert.That(paths.Contains(Path.Combine(_directory, ".", "Image.PNG")), Is.True);
            Assert.Throws<InvalidOperationException>(() => paths.EnsureNotProtected(differentlyCased));
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    public void EmptyPathsAreRejected(string path) =>
        Assert.Throws<ArgumentException>(() => new ProtectedSourcePaths().Add(path));

    [Test]
    public void ClearingForgetsTheSessionSources()
    {
        var source = Path.Combine(_directory, "source.png");
        var paths = new ProtectedSourcePaths();
        paths.Add(source);
        paths.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(paths.Count, Is.Zero);
            Assert.DoesNotThrow(() => paths.EnsureNotProtected(source));
        });
    }

    [Test]
    public void RefusedSaveLeavesTheImportedSourceUntouched()
    {
        var source = Path.Combine(_directory, "source image.png");
        File.WriteAllBytes(source, TestImages.EncodePng(4, 4, TestImages.SyntheticBgra(4, 4)));
        var before = File.ReadAllBytes(source);

        var session = new EditSession(new EditorDocument());
        var layer = new Layer { Kind = LayerKind.Image, AssetId = Guid.NewGuid() };
        session.AddImageLayer(layer, File.ReadAllBytes(source));
        var workflow = new NativeProjectWorkflow(session, TestImages.ValidateImages, "p2-test");
        var protectedPaths = new ProtectedSourcePaths();
        protectedPaths.Add(source);

        Assert.Throws<InvalidOperationException>(() => protectedPaths.EnsureNotProtected(source));
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(before));
        // The same session still saves normally to any other path.
        var target = Path.Combine(_directory, "工程.ppgproj");
        workflow.SaveAs(target);
        Assert.Multiple(() =>
        {
            Assert.That(workflow.IsDirty, Is.False);
            Assert.That(File.Exists(target), Is.True);
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(before));
        });
    }
}
