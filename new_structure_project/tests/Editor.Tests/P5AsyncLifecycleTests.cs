using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5AsyncLifecycleTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void DiscardCannotAuthorizeAnEditedOrReplacedDocument(bool replace)
    {
        var native = Project();
        Assert.ThrowsAsync<InvalidOperationException>(() => PendingChangesWorkflow.ConfirmAsync(native, () =>
        {
            if (replace) native.ImportConverted(native.Session.Document.Copy());
            else native.Session.UpdateLayer(native.Session.Document.Layers[0].Id, l => l.Text = "later edit");
            return Task.FromResult(PendingChangesChoice.Discard);
        }, () => throw new AssertionException("Discard must not save")));
        Assert.That(native.IsDirty, Is.True);
        Assert.That(native.Session.Document.Layers.Count, Is.EqualTo(1));
    }

    [Test]
    public void SaveCannotAuthorizeAReplacementThatWasNotConfirmed()
    {
        var native = Project();
        Assert.ThrowsAsync<InvalidOperationException>(() => PendingChangesWorkflow.ConfirmAsync(native,
            () => Task.FromResult(PendingChangesChoice.Save), () =>
            {
                native.New();
                return Task.FromResult(true);
            }));
        Assert.That(native.Session.Document.Layers, Is.Empty);
    }

    [Test]
    public void CleanActiveGestureCannotAuthorizeClosingOrReplacement()
    {
        var native = Project();
        native.Session.MarkSaved();
        native.Session.BeginGesture(native.Session.Document.Layers[0].Id);
        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => PendingChangesWorkflow.ConfirmAsync(native,
                () => throw new AssertionException("Unexpected prompt"),
                () => throw new AssertionException("Unexpected save")));
        }
        finally { native.Session.CancelGesture(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ExportCannotUseAPathRequestedForAnotherProjectOrSaveTarget(bool retarget)
    {
        var native = Project();
        var path = "first.ppgproj";
        var writes = 0;
        var export = new PngExportWorkflow(native.Session, _ => { }, (_, _, _) => [1],
            (_, _) => writes++, new(), () => path);
        Assert.ThrowsAsync<InvalidOperationException>(() => export.ExportWithReviewAsync(false, _ => false,
            _ => Task.FromResult(true), () =>
            {
                if (retarget) path = "second.ppgproj";
                else native.ImportConverted(native.Session.Document.Copy());
                return Task.FromResult<string?>("output.png");
            }));
        Assert.That(writes, Is.Zero);
    }

    [Test]
    public async Task TemporarySavePreservesPixelsAndHistoryAndClearsNativeTargetOnlyAfterSuccess()
    {
        var native = Project(); var folder = Folder();
        try
        {
            var current = Path.Combine(folder, "native.ppgproj"); native.SaveAs(current);
            var original = File.ReadAllBytes(current);
            native.Session.UpdateLayer(native.Session.Document.Layers[0].Id, l => l.Text = "temporary revision");
            var state = native.Session.Document.Copy(); var undo = native.Session.UndoCount;
            var path = Path.Combine(folder, "临时 项目.json");
            Assert.That(await native.SaveTemporaryAsync(() => Task.FromResult<string?>(path), new()), Is.True);
            Assert.That(NativeDocumentStore.Read(path).StateEquals(state), Is.True);
            Assert.That(File.ReadAllBytes(current), Is.EqualTo(original));
            Assert.That(native.CurrentPath, Is.Null); Assert.That(native.IsDirty, Is.False);
            Assert.That(native.Session.UndoCount, Is.EqualTo(undo));
            native.Session.Undo(); Assert.That(native.IsDirty, Is.True);
            native.Session.Redo(); Assert.That(native.IsDirty, Is.False);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestCase("edit")]
    [TestCase("replace")]
    [TestCase("retarget")]
    public void TemporarySaveRejectsStaleRequestsWithoutOverwritingExistingOutput(string change)
    {
        var native = Project(); var folder = Folder();
        var destination = Path.Combine(folder, "existing.json");
        File.WriteAllBytes(destination, [1, 2, 3]);
        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => native.SaveTemporaryAsync(() =>
            {
                if (change == "edit") native.Session.UpdateLayer(native.Session.Document.Layers[0].Id, l => l.Text = "later");
                else if (change == "replace") native.New();
                else native.SaveAs(Path.Combine(folder, "other.ppgproj"));
                return Task.FromResult<string?>(destination);
            }, new()));
            Assert.That(File.ReadAllBytes(destination), Is.EqualTo(new byte[] { 1, 2, 3 }));
            if (change == "retarget") Assert.That(native.CurrentPath, Is.EqualTo(Path.Combine(folder, "other.ppgproj")));
            else Assert.That(native.CurrentPath, Is.Null);
            if (change == "edit") Assert.That(native.IsDirty, Is.True);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TemporarySaveCannotReplaceTheNativeProjectOrAnImportedSource(bool imported)
    {
        var native = Project(); var folder = Folder();
        try
        {
            var path = Path.Combine(folder, "protected.ppgproj"); native.SaveAs(path);
            var original = File.ReadAllBytes(path); var sources = new ProtectedSourcePaths();
            if (imported) sources.Add(path.ToUpperInvariant());
            native.Session.UpdateLayer(native.Session.Document.Layers[0].Id, l => l.Text = "unsaved");
            Assert.ThrowsAsync<InvalidOperationException>(() => native.SaveTemporaryAsync(() => Task.FromResult<string?>(path), sources));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(original)); Assert.That(native.CurrentPath, Is.EqualTo(path));
            Assert.That(native.IsDirty, Is.True);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TemporarySaveRejectsGesturesBeforeOrAfterThePicker(bool duringPicker)
    {
        var native = Project(); var folder = Folder(); var calls = 0;
        var id = native.Session.Document.Layers[0].Id;
        if (!duringPicker) native.Session.BeginGesture(id);
        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => native.SaveTemporaryAsync(() =>
            {
                calls++; native.Session.BeginGesture(id);
                return Task.FromResult<string?>(Path.Combine(folder, "transient.json"));
            }, new()));
            Assert.That(calls, Is.EqualTo(duringPicker ? 1 : 0));
            Assert.That(Directory.GetFiles(folder), Is.Empty);
        }
        finally { native.Session.CancelGesture(); Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public async Task CancelRemainsCancelEvenWhenTheDocumentChanges()
    {
        var native = Project();
        Assert.That(await PendingChangesWorkflow.ConfirmAsync(native, () =>
        {
            native.New(); return Task.FromResult(PendingChangesChoice.Cancel);
        }, () => throw new AssertionException("Unexpected save")), Is.False);
        Assert.That(await native.SaveTemporaryAsync(() => Task.FromResult<string?>(null), new()), Is.False);
    }

    [Test]
    public void ChangedPromptDoesNotInvokeSaveForUnconfirmedContent()
    {
        var native = Project(); var calls = 0;
        Assert.ThrowsAsync<InvalidOperationException>(() => PendingChangesWorkflow.ConfirmAsync(native, () =>
        {
            native.Session.UpdateLayer(native.Session.Document.Layers[0].Id, l => l.Text = "unconfirmed");
            return Task.FromResult(PendingChangesChoice.Save);
        }, () => { calls++; return Task.FromResult(true); }));
        Assert.That(calls, Is.Zero); Assert.That(native.IsDirty, Is.True);
    }

    [Test]
    public void FailedTemporaryWriteKeepsSavepointAndHistory()
    {
        var native = Project(); var folder = Folder();
        try
        {
            var current = Path.Combine(folder, "current.ppgproj"); native.SaveAs(current);
            var bytes = File.ReadAllBytes(current);
            native.Session.UpdateLayer(native.Session.Document.Layers[0].Id, l => l.Text = "unsaved");
            var state = native.Session.Document.Copy(); var undo = native.Session.UndoCount;
            var blocked = Path.Combine(folder, "directory.json"); Directory.CreateDirectory(blocked);
            Assert.That(async () => await native.SaveTemporaryAsync(() => Task.FromResult<string?>(blocked), new()), Throws.Exception);
            Assert.That(native.Session.Document.StateEquals(state), Is.True);
            Assert.That(native.CurrentPath, Is.EqualTo(current)); Assert.That(native.IsDirty, Is.True);
            Assert.That(native.Session.UndoCount, Is.EqualTo(undo)); Assert.That(File.ReadAllBytes(current), Is.EqualTo(bytes));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "p5-async-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); return folder;
    }

    private static NativeProjectWorkflow Project()
    {
        var session = new EditSession(new EditorDocument());
        session.AddLayer(new Layer { Kind = LayerKind.Text, Text = "original" });
        return new(session, d => d.Validate(), "test");
    }
}
