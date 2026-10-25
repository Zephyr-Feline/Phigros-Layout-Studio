using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P2AtomicFileTests
{
    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p2-atomic-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void FirstSaveOverwriteAndRepeatLeaveOnlyCommittedFile()
    {
        var path = Path.Combine(_directory, "first.json");
        AtomicFile.Write(path, stream => stream.Write([1, 2, 3]));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
        AtomicFile.Write(path, stream => stream.Write([4, 5, 6]));
        AtomicFile.Write(path, stream => stream.Write([4, 5, 6]));
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 4, 5, 6 }));
            Assert.That(Directory.GetFiles(_directory), Is.EqualTo(new[] { path }));
        });
    }

    [Test]
    public void FailedWriterPreservesPreviousFileAndRemovesTemporaryFile()
    {
        var path = Path.Combine(_directory, "工程.json");
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, stream =>
        {
            stream.Write([9, 9]);
            throw new InvalidOperationException("simulated writer failure");
        }));
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        });
    }

    [Test]
    public void FailedFirstWriteDoesNotPublishPartialDocument()
    {
        var path = Path.Combine(_directory, "new.json");
        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, stream =>
        {
            stream.Write([9, 9]);
            throw new InvalidOperationException("asset serialization failed");
        }));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(path), Is.False);
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        });
    }

    [Test]
    [Platform("Win")]
    public void PersistentDestinationLockFailsWithoutChangingPreviousFile()
    {
        var path = Path.Combine(_directory, "locked.json");
        File.WriteAllBytes(path, [1, 2, 3]);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => AtomicFile.Write(path, stream => stream.Write([4, 5, 6])));
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        });
    }

    [Test]
    [Platform("Win")]
    public void TransientDestinationLockAllowsBoundedRetry()
    {
        var path = Path.Combine(_directory, "retry.json");
        File.WriteAllBytes(path, [1, 2, 3]);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(40);
            held.Dispose();
        });
        try { AtomicFile.Write(path, stream => stream.Write([4, 5, 6])); }
        finally { release.GetAwaiter().GetResult(); }
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 4, 5, 6 }));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        });
    }

    [Test]
    [Platform("Win")]
    public void TransientReaderWithoutDeleteSharingIsRetried()
    {
        // A reader that keeps the destination open without sharing deletion is what indexers and
        // anti-virus scanners look like: File.Replace cannot remove the old file until it lets go.
        var path = Path.Combine(_directory, "shared-reader.json");
        File.WriteAllBytes(path, [1, 2, 3]);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(40);
            held.Dispose();
        });
        try { AtomicFile.Write(path, stream => stream.Write([4, 5, 6])); }
        finally { release.GetAwaiter().GetResult(); }
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 4, 5, 6 }));
            Assert.That(Directory.GetFiles(_directory, "*.tmp"), Is.Empty);
        });
    }
}
