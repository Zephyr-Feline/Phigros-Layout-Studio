using System.Security.Cryptography;
using Editor.Application;
using Editor.Desktop;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class SecurityPathFontRegressionTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Directory.CreateTempSubdirectory("ppg-security-path-font-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [TestCase(@"\\server\share\image.png")]
    [TestCase("//server/share/image.png")]
    [TestCase(@"\/server\share\image.png")]
    [TestCase(@"/\server\share\image.png")]
    [TestCase(@"\\?\UNC\server\share\image.png")]
    [TestCase(@"\\.\UNC\server\share\image.png")]
    [TestCase(@"\\?\GLOBALROOT\Device\Mup\server\share\image.png")]
    [TestCase("file://server/share/image.png")]
    [TestCase("https://example.invalid/image.png")]
    [TestCase("smb://server/share/image.png")]
    public void NetworkDropsAreIgnoredWithoutAnyExistenceQuery(string candidate)
    {
        var calls = 0;
        var selection = ImageImportPathFilter.Filter([candidate], _ => { calls++; return true; });
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.Zero, "no real or injected filesystem lookup may contact an explicit network candidate");
            Assert.That(selection.Paths, Is.Empty);
            Assert.That(selection.Ignored, Is.EqualTo(1));
        });
    }

    [Test]
    public void UnsupportedExtensionsAreIgnoredBeforeExistenceQueries()
    {
        var calls = 0;
        var selection = ImageImportPathFilter.Filter([
            Path.Combine(_directory, "notes.txt"), Path.Combine(_directory, "folder"), "https://example.invalid/image.txt"
        ], _ => { calls++; return true; });
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.Zero);
            Assert.That(selection.Paths, Is.Empty);
            Assert.That(selection.Ignored, Is.EqualTo(3));
        });
    }

    [Test]
    public void LocalDropsKeepNormalizationOrderCaseInsensitiveDedupAndMissingCounts()
    {
        var first = Path.Combine(_directory, "first.png");
        var second = Path.Combine(_directory, "second.TIFF");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, first);
        var missing = Path.Combine(_directory, "missing.png");
        var selection = ImageImportPathFilter.Filter([relative, second, first.ToUpperInvariant(), missing],
            path => !string.Equals(path, missing, StringComparison.OrdinalIgnoreCase));
        Assert.Multiple(() =>
        {
            Assert.That(selection.Paths, Is.EqualTo(new[] { first, second }));
            Assert.That(selection.Ignored, Is.EqualTo(2));
        });
    }

    [Test]
    public void ExtendedLocalDrivePathsRemainEligibleWithoutOpeningTheFile()
    {
        var candidate = @"\\?\" + Path.Combine(_directory, new string('a', 200), new string('b', 100) + ".png");
        var queried = new List<string>();
        var selection = ImageImportPathFilter.Filter([candidate], path => { queried.Add(path); return true; });
        Assert.Multiple(() =>
        {
            Assert.That(queried, Is.EqualTo(new[] { candidate }));
            Assert.That(selection.Paths, Is.EqualTo(new[] { candidate }));
            Assert.That(selection.Ignored, Is.Zero);
        });
    }

    [TestCase(".ttf")]
    [TestCase(".otf")]
    public void HashMismatchIsReportedBeforeAnyNativeFontLoader(string extension)
    {
        var bytes = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var path = Path.Combine(_directory, new string('0', 64) + extension);
        File.WriteAllBytes(path, bytes);
        var calls = 0;
        var library = new LocalFontLibrary(_directory, _ =>
        {
            calls++;
            throw new InvalidOperationException("native font loader must not receive mismatched bytes");
        });
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.Zero);
            Assert.That(library.Fonts, Is.Empty);
            Assert.That(library.Warnings.Single(), Does.Contain("content-hash check"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "loading only reports corruption and never rewrites it");
        });
    }

    [Test]
    public void OnlyHashVerifiedBytesReachTheFontLoaderAndOtherEntriesStillLoad()
    {
        // A small managed loader spy verifies ordering without feeding malformed data to a
        // native parser. Existing P4/P5 tests cover real bundled-font load, repair and reload.
        var bytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllBytes(Path.Combine(_directory, hash + ".ttf"), bytes);
        File.WriteAllBytes(Path.Combine(_directory, new string('0', 64) + ".otf"), bytes);
        var calls = new List<byte[]>();
        var expected = new LocalFontInfo("Managed test", LocalFontRegistry.Prefix + hash + "#Managed test", hash, "Regular");
        var library = new LocalFontLibrary(_directory, loaded => { calls.Add(loaded); return expected; });
        Assert.Multiple(() =>
        {
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls.Single(), Is.EqualTo(bytes));
            Assert.That(library.Fonts, Is.EqualTo(new[] { expected }));
            Assert.That(library.Warnings, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void MatchingHashStillRequiresAUsableFontAndNeverRewritesTheEntry()
    {
        // The managed font-header validation rejects these tiny bytes before native parsing.
        var bytes = new byte[32];
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = Path.Combine(_directory, hash + ".ttf");
        File.WriteAllBytes(path, bytes);
        var library = new LocalFontLibrary(_directory);
        Assert.Multiple(() =>
        {
            Assert.That(library.Fonts, Is.Empty);
            Assert.That(library.Warnings.Single(), Does.Contain("Select a TTF/OTF"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        });
    }
}
