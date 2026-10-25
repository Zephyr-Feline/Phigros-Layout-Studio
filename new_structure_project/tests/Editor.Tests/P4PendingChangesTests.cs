using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4PendingChangesTests
{
    [Test]
    public void NewProjectDialogOffersThreeChoicesAndCancelPreservesTheDocument()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(new EditorDocument());
        var layer = new Layer { Kind = LayerKind.Text, Name = "Title", Text = "unsaved",
            Bounds = new RectD(20, 20, 200, 80) };
        edit.AddLayer(layer);
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var create = UiTestActions.Find(window, "New");
            Click(create);
            var prompt = window.OwnedWindows.Single(child => child.Title == "未保存的修改");
            var labels = prompt.GetVisualDescendants().OfType<Button>()
                .Select(button => button.Content?.ToString()).ToArray();
            Assert.That(labels, Is.EquivalentTo(new[] { "取消", "保存", "放弃修改" }));

            Click(PromptButton(prompt, "Cancel"));
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(layer.Id).Text, Is.EqualTo("unsaved"));
                Assert.That(edit.IsDirty, Is.True);
                Assert.That(window.Title, Does.Contain("*"));
            });

            Click(create);
            Click(PromptButton(window.OwnedWindows.Single(child => child.Title == "未保存的修改"),
                "Discard changes"));
            Dispatcher.UIThread.RunJobs();
            Assert.That(edit.Document.Layers, Is.Empty);
            Assert.That(edit.IsDirty, Is.False);
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
        }
    }

    [Test]
    public void ClosingDirtyWindowCanBeCanceledOrDiscarded()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(new EditorDocument());
        edit.AddLayer(new Layer { Kind = LayerKind.Shape, Bounds = new RectD(20, 20, 200, 80) });
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            window.Close();
            window.Close();
            Assert.That(window.OwnedWindows.Count(child => child.Title == "未保存的修改"), Is.EqualTo(1));
            Click(PromptButton(window.OwnedWindows.Single(child => child.Title == "未保存的修改"),
                "Cancel"));
            Dispatcher.UIThread.RunJobs();
            Assert.That(window.IsVisible, Is.True);
            Assert.That(edit.IsDirty, Is.True);

            window.Close();
            Click(PromptButton(window.OwnedWindows.Single(child => child.Title == "未保存的修改"),
                "Discard changes"));
            Dispatcher.UIThread.RunJobs();
            Assert.That(window.IsVisible, Is.False);
        }
        finally
        {
            if (window.IsVisible)
            {
                edit.MarkSaved();
                window.Close();
            }
        }
    }

    [Test]
    public async Task CleanDocumentDoesNotPromptOrSave()
    {
        var project = CreateProject();
        var result = await PendingChangesWorkflow.ConfirmAsync(project,
            () => throw new AssertionException("A clean document must not prompt."),
            () => throw new AssertionException("A clean document must not save."));

        Assert.That(result, Is.True);
        Assert.That(project.IsDirty, Is.False);
    }

    [Test]
    public async Task CancelAndCanceledSaveKeepTheEditedDocumentAndHistory()
    {
        var project = CreateProject();
        var layer = AddText(project, "unsaved");
        var undoCount = project.Session.UndoCount;

        Assert.That(await Confirm(project, PendingChangesChoice.Cancel, () =>
            throw new AssertionException("Cancel must not save.")), Is.False);
        Assert.That(await Confirm(project, PendingChangesChoice.Save, () => Task.FromResult(false)), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(project.IsDirty, Is.True);
            Assert.That(project.CurrentPath, Is.Null);
            Assert.That(project.Session.Document.Find(layer.Id).Text, Is.EqualTo("unsaved"));
            Assert.That(project.Session.UndoCount, Is.EqualTo(undoCount));
        });
    }

    [Test]
    public async Task SaveMustActuallyReachTheSavepointBeforeReplacementMayContinue()
    {
        var project = CreateProject();
        var layer = AddText(project, "keep me");
        var path = Path.Combine(Path.GetTempPath(), "p4-pending-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            Assert.That(await Confirm(project, PendingChangesChoice.Save, () => Task.FromResult(true)), Is.False,
                "A reported save that leaves the document dirty cannot authorize replacement.");
            Assert.That(project.IsDirty, Is.True);

            Assert.That(await Confirm(project, PendingChangesChoice.Save, () =>
            {
                project.SaveAs(path);
                return Task.FromResult(true);
            }), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(project.IsDirty, Is.False);
                Assert.That(project.CurrentPath, Is.EqualTo(Path.GetFullPath(path)));
                Assert.That(NativePackageStore.Read(path).Find(layer.Id).Text, Is.EqualTo("keep me"));
            });

            project.New();
            Assert.That(project.Session.Document.Layers, Is.Empty);
            Assert.That(project.Session.UndoCount, Is.Zero);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task FailedSaveDoesNotAuthorizeReplacementOrChangeExistingProject()
    {
        var project = CreateProject();
        var layer = AddText(project, "saved");
        var path = Path.Combine(Path.GetTempPath(), "p4-pending-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            project.SaveAs(path);
            var priorBytes = File.ReadAllBytes(path);
            project.Session.UpdateLayer(layer.Id, item => item.Text = "still unsaved");
            Assert.ThrowsAsync<IOException>(async () => await Confirm(project, PendingChangesChoice.Save,
                () => throw new IOException("simulated disk failure")));

            Assert.Multiple(() =>
            {
                Assert.That(project.IsDirty, Is.True);
                Assert.That(project.CurrentPath, Is.EqualTo(Path.GetFullPath(path)));
                Assert.That(project.Session.Document.Find(layer.Id).Text, Is.EqualTo("still unsaved"));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(priorBytes));
            });
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public async Task DiscardAuthorizesReplacementWithoutWritingTheSource()
    {
        var project = CreateProject();
        AddText(project, "discard me");
        Assert.That(await Confirm(project, PendingChangesChoice.Discard, () =>
            throw new AssertionException("Discard must not save.")), Is.True);
        Assert.That(project.IsDirty, Is.True, "The caller performs the replacement after confirmation.");
        project.New();
        Assert.That(project.Session.Document.Layers, Is.Empty);
        Assert.That(project.IsDirty, Is.False);
    }

    private static NativeProjectWorkflow CreateProject() =>
        new(new EditSession(new EditorDocument()), document => document.Validate(), "2.0.0-p4");

    private static Layer AddText(NativeProjectWorkflow project, string text)
    {
        var layer = new Layer { Kind = LayerKind.Text, Name = "Title", Text = text,
            Bounds = new RectD(20, 20, 200, 80) };
        project.Session.AddLayer(layer);
        return layer;
    }

    private static Task<bool> Confirm(NativeProjectWorkflow project, PendingChangesChoice choice,
        Func<Task<bool>> save) => PendingChangesWorkflow.ConfirmAsync(project,
        () => Task.FromResult(choice), save);

    private static Control PromptButton(Window prompt, string label) =>
        UiTestActions.Find(prompt, label);

    private static void Click(Control button) => UiTestActions.Click(button);
}
