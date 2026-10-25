using System.Reflection;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R2RecoveryUiTests
{
    private string _folder = null!;
    private readonly DateTimeOffset _clock = new(2026, 10, 17, 13, 0, 0, TimeSpan.FromHours(8));

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "r2-recovery-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
        AvaloniaTestHost.EnsureStarted();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Test]
    public void StartupOffersRestoreAsUnsavedAndDeletesTheConsumedSnapshot()
    {
        var source = DirtySession();
        var captured = new AutoRecoveryService(source, _folder, "r2-test", () => _clock,
            (_, _) => false);
        Assert.That(captured.CaptureIfDirty(Path.Combine(_folder, "原工程.ppgproj")), Is.True);
        var recoveredDocument = source.Document.Copy();
        var edit = new EditSession(new EditorDocument());
        var recovery = EmptyRecovery(edit);
        var window = new MainWindow(edit, recovery: recovery);
        try
        {
            window.Show();
            PumpUntil(() => window.OwnedWindows.OfType<RecoveryDialog>().Any());
            var dialog = window.OwnedWindows.OfType<RecoveryDialog>().Single();
            Assert.That(dialog.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "RecoveryCandidateList").Items, Has.Count.EqualTo(1));
            Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "RecoveryDetails").Text, Does.Contain("图层：1"));
            UiTestActions.ClickNamed(dialog, "RestoreRecovery");
            PumpUntil(() => edit.Document.StateEquals(recoveredDocument));
            Assert.That(edit.IsDirty, Is.True);
            Assert.That(window.Title, Does.Contain("*"));
            Assert.That(UiTestActions.Status(window), Does.Contain("未保存工程"));
            Assert.That(new AutoRecoveryService(edit, _folder, "r2-test", () => _clock,
                (_, _) => false).LoadCandidates().Candidates, Is.Empty);
        }
        finally
        {
            edit.MarkSaved();
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
        }
    }

    [Test]
    public void LaterKeepsTheSnapshotAndDiscardRemovesOnlyThatCopy()
    {
        var source = DirtySession();
        var captured = new AutoRecoveryService(source, _folder, "r2-test", () => _clock,
            (_, _) => false);
        Assert.That(captured.CaptureIfDirty(null), Is.True);
        var candidate = new AutoRecoveryService(source, _folder, "r2-test", () => _clock,
            (_, _) => false).LoadCandidates().Candidates.Single();
        var edit = new EditSession(new EditorDocument());
        var window = new MainWindow(edit, recovery: EmptyRecovery(edit));
        try
        {
            window.Show();
            PumpUntil(() => window.OwnedWindows.OfType<RecoveryDialog>().Any());
            var dialog = window.OwnedWindows.OfType<RecoveryDialog>().Single();
            UiTestActions.ClickNamed(dialog, "LaterRecovery");
            PumpUntil(() => window.OwnedWindows.Count == 0);
            Assert.That(File.Exists(candidate.SnapshotPath), Is.True);
            Assert.That(File.Exists(candidate.MetadataPath), Is.True);

            var discardWindow = new MainWindow(edit, recovery: EmptyRecovery(edit));
            try
            {
                discardWindow.Show();
                PumpUntil(() => discardWindow.OwnedWindows.OfType<RecoveryDialog>().Any());
                UiTestActions.ClickNamed(
                    discardWindow.OwnedWindows.OfType<RecoveryDialog>().Single(), "DiscardRecovery");
                PumpUntil(() => !File.Exists(candidate.SnapshotPath) && !File.Exists(candidate.MetadataPath));
                Assert.That(UiTestActions.Status(discardWindow), Does.Contain("丢弃"));
            }
            finally
            {
                edit.MarkSaved();
                foreach (var owned in discardWindow.OwnedWindows.ToArray()) owned.Close();
                discardWindow.Close();
            }
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
        }
    }

    [Test]
    public void TimerCaptureUsesAppliedStateAndSkipsSideDraftsAndGestures()
    {
        var layer = new Layer
        {
            Kind = LayerKind.Text, Name = "applied", Text = "已应用",
            Bounds = new RectD(30, 30, 260, 80)
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [layer]
        });
        edit.UpdateLayer(layer.Id, value => value.Name = "dirty");
        var recovery = EmptyRecovery(edit);
        var window = new MainWindow(edit, recovery: recovery);
        try
        {
            window.Show();
            var draft = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerName");
            draft.Text = "尚未应用的草稿";
            var method = typeof(MainWindow).GetMethod("CaptureRecoveryAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)method.Invoke(window, null)!;
            PumpUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
            var candidate = new AutoRecoveryService(edit, _folder, "r2-test", () => _clock,
                (_, _) => false).LoadCandidates().Candidates.Single();
            var recovered = NativePackageStore.Read(candidate.SnapshotPath);
            Assert.That(recovered.Find(layer.Id).Name, Is.EqualTo("dirty"));
            Assert.That(recovered.Find(layer.Id).Name, Is.Not.EqualTo("尚未应用的草稿"));
            Assert.That(edit.IsDirty, Is.True);

            edit.BeginGesture(layer.Id);
            try
            {
                var blocked = (Task)method.Invoke(window, null)!;
                PumpUntil(() => blocked.IsCompleted);
                blocked.GetAwaiter().GetResult();
            }
            finally { edit.CancelGesture(); }
            Assert.That(new AutoRecoveryService(edit, _folder, "r2-test", () => _clock,
                (_, _) => false).LoadCandidates().Candidates, Has.Count.EqualTo(1));
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
        }
    }

    [Test]
    public void SaveAndDiscardCloseRemoveTheSessionSnapshotAndCancelCloseKeepsIt()
    {
        var (edit, _) = DirtySessionWithId();
        var savePath = Path.Combine(_folder, "saved.ppgproj");
        var recovery = EmptyRecovery(edit);
        var window = new MainWindow(edit, recovery: recovery,
            nativeSavePathPicker: () => Task.FromResult<string?>(savePath));
        try
        {
            window.Show();
            var capture = typeof(MainWindow).GetMethod("CaptureRecoveryAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)capture.Invoke(window, null)!;
            PumpUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
            Assert.That(Candidates(edit), Has.Count.EqualTo(1));

            UiTestActions.ClickNamed(window, "SaveProject");
            PumpUntil(() => File.Exists(savePath) && Candidates(edit).Count == 0);
            Assert.That(edit.IsDirty, Is.False);

            edit.UpdateLayer(edit.Document.Layers[0].Id, value => value.Name = "dirty again");
            var second = (Task)capture.Invoke(window, null)!;
            PumpUntil(() => second.IsCompleted);
            second.GetAwaiter().GetResult();
            Assert.That(Candidates(edit), Has.Count.EqualTo(1));

            window.Close();
            PumpUntil(() => window.OwnedWindows.Count > 0);
            var prompt = window.OwnedWindows.Single(owned => owned.Title == "未保存的修改");
            UiTestActions.Click(prompt, "取消");
            PumpUntil(() => window.OwnedWindows.Count == 0);
            for (var index = 0; index < 10; index++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }
            Assert.That(Candidates(edit), Has.Count.EqualTo(1));

            window.Close();
            PumpUntil(() => window.OwnedWindows.Count > 0);
            prompt = window.OwnedWindows.Single(owned => owned.Title == "未保存的修改");
            UiTestActions.Click(prompt, "放弃修改");
            PumpUntil(() => !window.IsVisible);
            Assert.That(Candidates(edit), Is.Empty);
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            if (window.IsVisible) window.Close();
        }
    }

    private AutoRecoveryService EmptyRecovery(EditSession edit) =>
        new(edit, _folder, "r2-test", () => _clock, (_, _) => false);

    private static EditSession DirtySession()
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
        return session;
    }

    private static (EditSession Session, Guid LayerId) DirtySessionWithId()
    {
        var session = DirtySession();
        return (session, session.Document.Layers[0].Id);
    }

    private List<RecoveryCandidate> Candidates(EditSession edit) =>
        new AutoRecoveryService(edit, _folder, "r2-test", () => _clock, (_, _) => false)
            .LoadCandidates().Candidates.ToList();

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(condition(), Is.True, "UI operation did not complete.");
    }
}
