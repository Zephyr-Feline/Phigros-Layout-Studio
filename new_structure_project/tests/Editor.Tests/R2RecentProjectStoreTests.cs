using Editor.Application;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R2RecentProjectStoreTests
{
    private string _folder = null!;
    private string _index = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "r2-recent-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
        _index = Path.Combine(_folder, "RecentProjects.json");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Test]
    public void RecordsNewestFirstDeduplicatesAndReportsMissingEntries()
    {
        var first = Path.Combine(_folder, "第一工程.ppgproj");
        var second = Path.Combine(_folder, "第二工程.ppgproj");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);
        var store = new RecentProjectStore(_index);
        Assert.That(store.Record(first, new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.FromHours(8))), Is.True);
        Assert.That(store.Record(second, new DateTimeOffset(2026, 10, 17, 11, 0, 0, TimeSpan.FromHours(8))), Is.True);
        Assert.That(store.Record(first, new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.FromHours(8))), Is.True);
        var snapshot = store.Load();
        Assert.That(snapshot.Diagnostics, Is.Empty);
        Assert.That(snapshot.Entries.Select(entry => entry.Path), Is.EqualTo(new[] { first, second }));
        Assert.That(snapshot.Entries.All(entry => entry.Exists), Is.True);
        Assert.That(snapshot.Entries[0].DisplayName, Is.EqualTo("第一工程.ppgproj"));

        File.Delete(second);
        Assert.That(store.Load().Entries.Single(entry => entry.Path == second).Exists, Is.False);
        Assert.That(store.RemoveMissing(), Is.EqualTo(1));
        Assert.That(store.Load().Entries.Select(entry => entry.Path), Is.EqualTo(new[] { first }));
        Assert.That(store.Remove(first), Is.True);
        Assert.That(store.Load().Entries, Is.Empty);
    }

    [Test]
    public void KeepsAtMostTwelveEntries()
    {
        var store = new RecentProjectStore(_index);
        for (var index = 0; index < 15; index++)
        {
            var path = Path.Combine(_folder, $"project-{index:00}.ppgproj");
            Assert.That(store.Record(path, DateTimeOffset.UtcNow.AddMinutes(index)), Is.True);
        }
        var entries = store.Load().Entries;
        Assert.That(entries, Has.Count.EqualTo(RecentProjectStore.MaxEntries));
        Assert.That(entries[0].DisplayName, Is.EqualTo("project-14.ppgproj"));
        Assert.That(entries.Select(entry => entry.DisplayName), Does.Not.Contain("project-00.ppgproj"));
    }

    [Test]
    public void CorruptIndexIsIgnoredAndARecordCanReplaceIt()
    {
        File.WriteAllText(_index, "{broken");
        var store = new RecentProjectStore(_index);
        var broken = store.Load();
        Assert.That(broken.Entries, Is.Empty);
        Assert.That(broken.Diagnostics, Has.Count.EqualTo(1));
        var path = Path.Combine(_folder, "recovered.ppgproj");
        Assert.That(store.Record(path, DateTimeOffset.UtcNow), Is.True);
        Assert.That(store.Load().Entries.Select(entry => entry.Path), Is.EqualTo(new[] { path }));
    }

    [Test]
    public void WriteFailureIsNonFatalAndLockContentionReturnsFalse()
    {
        Directory.CreateDirectory(_index);
        var store = new RecentProjectStore(_index);
        Assert.That(store.Record(Path.Combine(_folder, "x.ppgproj"), DateTimeOffset.UtcNow), Is.False);
        Assert.That(store.LastError, Is.Not.Null);
    }
}
