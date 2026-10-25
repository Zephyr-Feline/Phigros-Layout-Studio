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

public sealed class R2RecentProjectsUiTests
{
    private string _folder = null!;
    private string _recovery = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "r2-recent-ui-" + Guid.NewGuid().ToString("N")[..8]);
        _recovery = Path.Combine(_folder, "recovery");
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Test]
    public void SuccessfulOpenAndSaveRecordRecentProjectsAndFailedOpenDoesNot()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var store = new RecentProjectStore(Path.Combine(_folder, "recent.json"));
        var projectPath = Path.Combine(_folder, "最近工程.ppgproj");
        var project = new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360,
            Layers = [new Layer { Kind = LayerKind.Text, Name = "来自最近工程", Text = "内容" }]
        };
        NativePackageStore.Save(projectPath, project, "r2-test");
        var edit = new EditSession(new EditorDocument());
        var window = new MainWindow(edit, recentProjects: store,
            recovery: EmptyRecovery(edit), filePathPicker: _ =>
                Task.FromResult<string?>(Path.Combine(_folder, "missing.ppgproj")));
        try
        {
            window.Show();
            PumpUntil(() => window.IsVisible);
            Assert.That(store.Load().Entries, Is.Empty);
            UiTestActions.ClickNamed(window, "OpenProject");
            PumpUntil(() => UiTestActions.Error(window) is not null);
            Assert.That(store.Load().Entries, Is.Empty, "failed open must not pollute the list");

            var savedPath = Path.Combine(_folder, "保存的工程.ppgproj");
            var saveEdit = new EditSession(new EditorDocument());
            var saveWindow = new MainWindow(saveEdit, recentProjects: store,
                recovery: EmptyRecovery(saveEdit),
                nativeSavePathPicker: () => Task.FromResult<string?>(savedPath));
            try
            {
                saveWindow.Show();
                UiTestActions.ClickNamed(saveWindow, "SaveProject");
                PumpUntil(() => File.Exists(savedPath));
                PumpUntil(() => store.Load().Entries.Any(entry => entry.Path == savedPath));
                Assert.That(store.Load().Entries[0].Path, Is.EqualTo(savedPath));
            }
            finally
            {
                saveEdit.MarkSaved();
                foreach (var dialog in saveWindow.OwnedWindows.ToArray()) dialog.Close();
                saveWindow.Close();
            }

            var openEdit = new EditSession(new EditorDocument());
            var openStore = new RecentProjectStore(Path.Combine(_folder, "open-recent.json"));
            Assert.That(openStore.Record(projectPath, DateTimeOffset.Now), Is.True);
            var openWindow = new MainWindow(openEdit, recentProjects: openStore,
                recovery: EmptyRecovery(openEdit));
            try
            {
                openWindow.Show();
                PumpUntil(() => openWindow.OwnedWindows.Count == 0);
                var menu = openWindow.GetLogicalDescendants().OfType<MenuItem>()
                    .Single(item => item.Name == "RecentProjectsMenu");
                var recent = menu.Items.OfType<MenuItem>().First(item =>
                    item.Header?.ToString()?.Contains("最近工程.ppgproj", StringComparison.Ordinal) == true);
                UiTestActions.Click(recent);
                PumpUntil(() => openEdit.Document.StateEquals(project));
                Assert.That(openStore.Load().Entries[0].LastOpenedUtc,
                    Is.GreaterThan(DateTimeOffset.Now.AddMinutes(-1)));
            }
            finally
            {
                openEdit.MarkSaved();
                foreach (var dialog in openWindow.OwnedWindows.ToArray()) dialog.Close();
                openWindow.Close();
            }
        }
        finally
        {
            edit.MarkSaved();
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void MissingRecentsAreMarkedAndCanBeCleaned()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var store = new RecentProjectStore(Path.Combine(_folder, "missing.json"));
        Assert.That(store.Record(Path.Combine(_folder, "gone.ppgproj"), DateTimeOffset.Now), Is.True);
        var edit = new EditSession(new EditorDocument());
        var window = new MainWindow(edit, recentProjects: store, recovery: EmptyRecovery(edit));
        try
        {
            window.Show();
            var menu = window.GetLogicalDescendants().OfType<MenuItem>()
                .Single(item => item.Name == "RecentProjectsMenu");
            Assert.That(menu.Items.OfType<MenuItem>().Any(item =>
                item.Header?.ToString()?.Contains("文件不存在", StringComparison.Ordinal) == true), Is.True);
            var clean = menu.Items.OfType<MenuItem>()
                .Single(item => item.Name == "CleanMissingRecentProjects");
            UiTestActions.Click(clean);
            Assert.That(store.Load().Entries, Is.Empty);
            Assert.That(UiTestActions.Status(window), Does.Contain("缺失项"));
        }
        finally
        {
            edit.MarkSaved();
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private AutoRecoveryService EmptyRecovery(EditSession edit) =>
        new(edit, _recovery, "r2-test", () => DateTimeOffset.Now, (_, _) => false);

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
