using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class NativeProjectFileTests
{
    [TestCase("abc", "abc.ppgproj")]
    [TestCase("abc.ppgproj", "abc.ppgproj")]
    [TestCase("abc.PPGPROJ", "abc.PPGPROJ")]
    [TestCase("abc.PpgProj", "abc.PpgProj")]
    [TestCase("abc.", "abc.ppgproj")]
    [TestCase("abc..", "abc.ppgproj")]
    [TestCase("abc.tar.gz", "abc.tar.gz")]
    [TestCase("abc.txt", "abc.txt")]
    [TestCase(".hidden", ".hidden")]
    [TestCase("曲绘 工程", "曲绘 工程.ppgproj")]
    [TestCase("曲绘 工程.ppgproj", "曲绘 工程.ppgproj")]
    [TestCase("曲绘 工程.PPGPROJ", "曲绘 工程.PPGPROJ")]
    public void EnsureExtensionProducesThePublicSuffixExactlyOnce(string input, string expected) =>
        Assert.That(NativeProjectFile.EnsureExtension(input), Is.EqualTo(expected));

    [Test]
    public void EnsureExtensionKeepsTheChosenDirectory()
    {
        var path = Path.Combine("sub 目录", "名 称");
        Assert.That(NativeProjectFile.EnsureExtension(path),
            Is.EqualTo(Path.Combine("sub 目录", "名 称.ppgproj")));
        var rooted = Path.Combine(Path.GetTempPath(), "p2 扩展名", "工程");
        Assert.That(NativeProjectFile.EnsureExtension(rooted), Is.EqualTo(rooted + ".ppgproj"));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("dir/")]
    public void EnsureExtensionRejectsNamesWithoutAFileName(string input) =>
        Assert.Throws<ArgumentException>(() => NativeProjectFile.EnsureExtension(input));

    [TestCase("x.ppgproj", true)]
    [TestCase("x.PPGPROJ", true)]
    [TestCase("x.PpgProj", true)]
    [TestCase("x.ppgprojx", false)]
    [TestCase("x", false)]
    [TestCase("sub.dir/x", false)]
    public void HasExtensionOnlyAcceptsThePublicSuffix(string input, bool expected) =>
        Assert.That(NativeProjectFile.HasExtension(input), Is.EqualTo(expected));

    [Test]
    public void ExtensionIsNameConventionOnlyAndNeverGrantsTrust()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p2-extension",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var document = new EditorDocument();
            document.Layers.Add(new Layer { Kind = LayerKind.Text, Name = "kept", Text = "内容" });
            // Valid container under names the public filter would never offer.
            foreach (var name in new[] { "weird.PPGPROJ", "package.tar.gz", "no-extension",
                "多个.点.名称.ppgproj" })
            {
                var path = Path.Combine(directory, name);
                NativePackageStore.Save(path, document, "p2-test");
                Assert.That(NativePackageStore.Read(path).Layers[0].Text, Is.EqualTo("内容"), name);
            }
            // A public-looking name with a broken container is still rejected by the reader.
            var broken = Path.Combine(directory, "looks-right.ppgproj");
            File.WriteAllText(broken, "not a zip");
            Assert.Throws<InvalidDataException>(() => NativePackageStore.Read(broken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SaveAsThroughThePublicNameConventionWritesOneSuffix()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p2-saveas",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var session = new EditSession(new EditorDocument());
            var workflow = new NativeProjectWorkflow(session, _ => { }, "p2-test");
            session.AddLayer(new Layer { Kind = LayerKind.Text, Text = "public name" });

            var withoutExtension = Path.Combine(directory, "abc");
            workflow.SaveAs(NativeProjectFile.EnsureExtension(withoutExtension));
            Assert.Multiple(() =>
            {
                Assert.That(workflow.CurrentPath, Is.EqualTo(Path.GetFullPath(withoutExtension + ".ppgproj")));
                Assert.That(File.Exists(withoutExtension + ".ppgproj"), Is.True);
                Assert.That(File.Exists(withoutExtension), Is.False);
            });

            var withExtension = Path.Combine(directory, "abc.PPGPROJ");
            workflow.SaveAs(NativeProjectFile.EnsureExtension(withExtension));
            Assert.Multiple(() =>
            {
                Assert.That(workflow.CurrentPath, Is.EqualTo(Path.GetFullPath(withExtension)));
                Assert.That(File.Exists(withExtension + ".ppgproj"), Is.False);
                Assert.That(File.Exists(withExtension), Is.True);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
