using System.Diagnostics;
using Editor.Application;
using Editor.Core;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>Real Windows junctions; the batch must bind and recheck the resolved source identity.</summary>
public sealed class R1BatchSourceAliasTests
{
    private string _root = "";
    private string _real = "";
    private string _other = "";
    private string _alias = "";
    private readonly List<string> _junctions = [];

    [SetUp]
    public void SetUp()
    {
        _root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r1-batch-alias", Guid.NewGuid().ToString("N")));
        _real = Path.Combine(_root, "真实目录");
        _other = Path.Combine(_root, "另一目录");
        _alias = Path.Combine(_root, "联接别名");
        Directory.CreateDirectory(_real);
        Directory.CreateDirectory(_other);
        Junction(_alias, _real);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var link in _junctions.AsEnumerable().Reverse())
            if (Directory.Exists(link) || new DirectoryInfo(link).LinkTarget is not null)
                Directory.Delete(link, recursive: false);
        _junctions.Clear();
        var expected = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r1-batch-alias")) + Path.DirectorySeparatorChar;
        Assert.That(Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase), Is.True);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task SuccessfulImportProtectsTheResolvedRealSource()
    {
        var realFile = Path.Combine(_real, "曲绘.png");
        File.WriteAllBytes(realFile, [1, 2, 3]);
        var aliasFile = Path.Combine(_alias, "曲绘.png");
        var edit = new EditSession(new EditorDocument());
        var sources = new ProtectedSourcePaths();
        var workflow = new BatchImageImportWorkflow(edit, _ =>
            Task.FromResult(new LoadedImage(new AssetBlob([1, 2, 3]), 20, 10)),
            _ => { }, sources, () => null);
        Assert.That(await workflow.ImportAsync(() =>
            Task.FromResult<IReadOnlyList<string>>([aliasFile])), Is.EqualTo(1));
        Assert.That(sources.Contains(realFile), Is.True);
        Assert.That(sources.Contains(aliasFile), Is.True);
        Assert.That(edit.Document.Layers, Has.Count.EqualTo(1));
    }

    [Test]
    public void RetargetingTheJunctionDuringReadRejectsTheWholeBatch()
    {
        var realFile = Path.Combine(_real, "曲绘.png");
        File.WriteAllBytes(realFile, [1, 2, 3]);
        var aliasFile = Path.Combine(_alias, "曲绘.png");
        var edit = new EditSession(new EditorDocument());
        var sources = new ProtectedSourcePaths();
        var workflow = new BatchImageImportWorkflow(edit, _ =>
        {
            RemoveJunction(_alias);
            Junction(_alias, _other);
            return Task.FromResult(new LoadedImage(new AssetBlob([1, 2, 3]), 20, 10));
        }, _ => { }, sources, () => null);
        Assert.That(async () => await workflow.ImportAsync(() =>
            Task.FromResult<IReadOnlyList<string>>([aliasFile])), Throws.InstanceOf<IOException>());
        Assert.That(edit.Document.Layers, Is.Empty);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(sources.Count, Is.Zero);
    }

    private void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new IOException("Could not create a junction.");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException("Could not create a junction: " + process.StandardError.ReadToEnd());
        _junctions.Add(link);
    }

    private void RemoveJunction(string link)
    {
        if (Directory.Exists(link) || new DirectoryInfo(link).LinkTarget is not null)
            Directory.Delete(link, recursive: false);
        _junctions.Remove(link);
    }
}
