using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R2AutoRecoveryTests
{
    private string _folder = null!;
    private readonly DateTimeOffset _clock = new(2026, 10, 17, 12, 0, 0, TimeSpan.FromHours(8));

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "r2-recovery-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Test]
    public void DirtyAppliedDocumentCapturesAReadableNativeSnapshotWithoutChangingTheSession()
    {
        var (session, layerId) = DirtySession();
        var before = session.Document.Copy();
        var selection = session.SelectedLayerIds.ToArray();
        var history = (session.UndoCount, session.RedoCount, session.IsDirty);
        var service = Service(session, fixedClock: true);
        Assert.That(service.CaptureIfDirty(Path.Combine(_folder, "工程.ppgproj")), Is.True);

        var candidate = new AutoRecoveryService(session, _folder, "r2-test",
            () => _clock, (_, _) => false).LoadCandidates().Candidates.Single();
        Assert.That(candidate.DisplayName, Is.EqualTo("工程.ppgproj"));
        Assert.That(candidate.LayerCount, Is.EqualTo(1));
        Assert.That(candidate.CanvasWidth, Is.EqualTo(640));
        Assert.That(candidate.SavedAtUtc, Is.EqualTo(_clock));
        var recovered = NativePackageStore.Read(candidate.SnapshotPath);
        Assert.That(recovered.StateEquals(before), Is.True);
        Assert.That(recovered.Find(layerId).Name, Is.EqualTo("dirty"));
        Assert.That(session.Document.StateEquals(before), Is.True);
        Assert.That(session.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That((session.UndoCount, session.RedoCount, session.IsDirty), Is.EqualTo(history));
    }

    [Test]
    public void CleanOrActiveGestureDocumentsAreNotCaptured()
    {
        var clean = new EditSession(new EditorDocument { Layers = [new Layer { Kind = LayerKind.Shape }] });
        var cleanService = Service(clean, fixedClock: true);
        Assert.That(cleanService.CaptureIfDirty(null), Is.False);

        var (session, layerId) = DirtySession();
        session.BeginGesture(layerId);
        try
        {
            Assert.That(Service(session, fixedClock: true).CaptureIfDirty(null), Is.False);
        }
        finally { session.CancelGesture(); }
        Assert.That(new AutoRecoveryService(session, _folder, "r2-test", () => _clock,
            (_, _) => false).LoadCandidates().Candidates, Is.Empty);
    }

    [Test]
    public void UnchangedStateIsNotRewrittenAndTheSnapshotCanBeDeleted()
    {
        var (session, _) = DirtySession();
        var service = Service(session, fixedClock: true);
        Assert.That(service.CaptureIfDirty(null), Is.True);
        var candidate = new AutoRecoveryService(session, _folder, "r2-test", () => _clock,
            (_, _) => false).LoadCandidates().Candidates.Single();
        var time = File.GetLastWriteTimeUtc(candidate.SnapshotPath);
        Assert.That(service.CaptureIfDirty(null, _clock.AddMinutes(1)), Is.False);
        Assert.That(File.GetLastWriteTimeUtc(candidate.SnapshotPath), Is.EqualTo(time));
        Assert.That(service.Delete(candidate), Is.True);
        Assert.That(File.Exists(candidate.SnapshotPath), Is.False);
        Assert.That(File.Exists(candidate.MetadataPath), Is.False);
    }

    [Test]
    public void CorruptFutureMissingAndActiveProcessCandidatesAreIsolated()
    {
        var (session, _) = DirtySession();
        var service = Service(session, fixedClock: true);
        Assert.That(service.CaptureIfDirty(null), Is.True);
        var candidate = new AutoRecoveryService(session, _folder, "r2-test", () => _clock,
            (_, _) => false).LoadCandidates().Candidates.Single();
        var corrupt = Path.Combine(_folder, "recovery-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(corrupt, "{broken");
        var future = Path.Combine(_folder, "recovery-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(future,
            "{\"formatVersion\":99,\"id\":\"00000000000000000000000000000000\"}");
        var active = new AutoRecoveryService(session, _folder, "r2-test", () => _clock,
            (_, _) => true).LoadCandidates();
        Assert.That(active.Candidates, Is.Empty);
        Assert.That(File.Exists(corrupt), Is.True);
        Assert.That(File.Exists(future), Is.True);
        Assert.That(File.Exists(candidate.SnapshotPath), Is.True);

        File.Delete(candidate.SnapshotPath);
        var missing = new AutoRecoveryService(session, _folder, "r2-test", () => _clock,
            (_, _) => false).LoadCandidates();
        Assert.That(missing.Candidates, Is.Empty);
        Assert.That(missing.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("快照文件不存在", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void CleanupKeepsTheDirectoryBounded()
    {
        for (var index = 0; index < AutoRecoveryService.MaxCandidates + 2; index++)
        {
            var (session, _) = DirtySession();
            var service = new AutoRecoveryService(session, _folder, "r2-test",
                () => _clock.AddMinutes(index), (_, _) => false);
            Assert.That(service.CaptureIfDirty(null), Is.True);
        }
        var candidates = new AutoRecoveryService(DirtySession().Session, _folder, "r2-test",
            () => _clock.AddHours(1), (_, _) => false).LoadCandidates().Candidates;
        Assert.That(candidates, Has.Count.EqualTo(AutoRecoveryService.MaxCandidates));
    }

    [Test]
    public void CleanupDropsSnapshotsOlderThanThirtyDays()
    {
        var oldService = new AutoRecoveryService(DirtySession().Session, _folder, "r2-test",
            () => _clock.AddDays(-40), (_, _) => false);
        Assert.That(oldService.CaptureIfDirty(null), Is.True);
        var cleaned = new AutoRecoveryService(DirtySession().Session, _folder, "r2-test",
            () => _clock.AddDays(1), (_, _) => false).Cleanup();
        Assert.That(cleaned.Candidates, Is.Empty);
    }

    [Test]
    public void UnwritableSnapshotDirectoryDoesNotTouchTheSession()
    {
        var (session, _) = DirtySession();
        var before = session.Document.Copy();
        var history = (session.UndoCount, session.RedoCount, session.IsDirty);
        var path = Path.Combine(_folder, "not-a-directory");
        File.WriteAllText(path, "x");
        var service = new AutoRecoveryService(session, path, "r2-test", () => _clock, (_, _) => false);
        Assert.That(() => service.CaptureIfDirty(null), Throws.Exception);
        Assert.That(session.Document.StateEquals(before), Is.True);
        Assert.That((session.UndoCount, session.RedoCount, session.IsDirty), Is.EqualTo(history));
    }

    private AutoRecoveryService Service(EditSession session, bool fixedClock) =>
        new(session, _folder, "r2-test", fixedClock ? () => _clock : null, (_, _) => false);

    private static (EditSession Session, Guid LayerId) DirtySession()
    {
        var layer = new Layer
        {
            Kind = LayerKind.Text, Name = "before", Text = "正文",
            Bounds = new RectD(40, 40, 300, 90)
        };
        var session = new EditSession(new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [layer]
        });
        session.UpdateLayer(layer.Id, value => value.Name = "dirty");
        return (session, layer.Id);
    }
}
