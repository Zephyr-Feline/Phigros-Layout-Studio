using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5NativeSaveSafetyTests
{
    [TestCase("edit")]
    [TestCase("replace")]
    [TestCase("retarget")]
    public void DesktopRejectsAnOutdatedSavePickerWithoutWritingOrChangingTheCurrentProject(string change)
    {
        AvaloniaTestHost.EnsureStarted();
        var directory = Path.Combine(Path.GetTempPath(), "p5-save-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "original.ppgproj");
        var destination = Path.Combine(directory, "existing destination.ppgproj");
        var other = Path.Combine(directory, "other.ppgproj");
        var edit = new EditSession(new EditorDocument());
        var layer = new Layer { Kind = LayerKind.Text, Text = "original" };
        edit.AddLayer(layer);
        var native = new NativeProjectWorkflow(edit, d => d.Validate(), "test");
        native.SaveAs(original);
        File.Copy(original, destination);
        var originalBytes = File.ReadAllBytes(original);
        var destinationBytes = File.ReadAllBytes(destination);
        var picker = new TaskCompletionSource<string?>();
        var window = new MainWindow(edit, nativeWorkflow: native, nativeSavePathPicker: () => picker.Task);
        try
        {
            window.Show();
            window.GetVisualDescendants().OfType<SceneCanvas>().Single().Focus();
            window.KeyPress(Key.S, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
            if (change == "edit") edit.UpdateLayer(layer.Id, l => l.Text = "new edit while choosing a path");
            else if (change == "replace") native.New();
            else native.SaveAs(other);
            var current = edit.Document;
            var state = current.Copy();
            var currentPath = native.CurrentPath;
            var dirty = edit.IsDirty;
            var undo = edit.UndoCount;
            var redo = edit.RedoCount;

            picker.SetResult(destination);
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(destination), Is.EqualTo(destinationBytes));
                Assert.That(File.ReadAllBytes(original), Is.EqualTo(originalBytes));
                Assert.That(edit.Document, Is.SameAs(current));
                Assert.That(edit.Document.StateEquals(state), Is.True);
                Assert.That(native.CurrentPath, Is.EqualTo(currentPath));
                Assert.That(edit.IsDirty, Is.EqualTo(dirty));
                Assert.That(edit.UndoCount, Is.EqualTo(undo));
                Assert.That(edit.RedoCount, Is.EqualTo(redo));
                Assert.That(window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(block => block.Text?.StartsWith("错误：", StringComparison.Ordinal) == true), Is.True);
            });

            // The failed request must release the desktop's save guard so a fresh request works.
            picker = new TaskCompletionSource<string?>();
            var retry = Path.Combine(directory, "retry.ppgproj");
            window.KeyPress(Key.S, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
            picker.SetResult(retry);
            Dispatcher.UIThread.RunJobs();
            Assert.That(native.CurrentPath, Is.EqualTo(retry));
            Assert.That(edit.IsDirty, Is.False);
            Assert.That(NativePackageStore.Read(retry).StateEquals(state), Is.True);
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StableProjectSavesWithItsExistingExtensionAndHistory(bool named)
    {
        var directory = MakeDirectory();
        var native = CreateProject();
        try
        {
            var current = Path.Combine(directory, "current.ppgproj");
            if (named) native.SaveAs(current);
            var id = native.Session.Document.Layers[0].Id;
            native.Session.UpdateLayer(id, l => l.Text = "save this revision");
            var snapshot = native.Session.Document.Copy();
            var undo = native.Session.UndoCount;
            var name = Path.Combine(directory, named ? "chosen.package" : "chosen");
            var destination = named ? name : name + ".ppgproj";
            Assert.That(await native.SaveAsAsync(() => Task.FromResult<string?>(name), new()), Is.True);
            Assert.That(native.CurrentPath, Is.EqualTo(destination));
            Assert.That(NativePackageStore.Read(destination).StateEquals(snapshot), Is.True);
            Assert.That(native.Session.UndoCount, Is.EqualTo(undo));
            Assert.That(native.IsDirty, Is.False);
            native.Session.Undo(); Assert.That(native.IsDirty, Is.True);
            native.Session.Redo(); Assert.That(native.IsDirty, Is.False);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestCase("identical replacement")]
    [TestCase("edit and undo")]
    public void IdenticalPixelsFromAnotherDocumentDoNotAuthorizeTheOldRequest(string change)
    {
        var directory = MakeDirectory();
        var native = CreateProject();
        var before = native.Session.Document.Copy();
        var destination = Path.Combine(directory, "untouched.ppgproj");
        var prior = new byte[] { 7, 8, 9 };
        File.WriteAllBytes(destination, prior);
        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => native.SaveAsAsync(() =>
            {
                if (change == "identical replacement") native.ImportConverted(before.Copy());
                else
                {
                    native.Session.UpdateLayer(before.Layers[0].Id, l => l.Text = "temporary edit");
                    native.Session.Undo();
                }
                return Task.FromResult<string?>(destination);
            }, new()));
            Assert.That(native.Session.Document.StateEquals(before), Is.True);
            Assert.That(native.CurrentPath, Is.Null);
            Assert.That(File.ReadAllBytes(destination), Is.EqualTo(prior));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GestureBeforeOrDuringThePickerCannotWriteTransientGeometry(bool beforePicker)
    {
        var directory = MakeDirectory();
        var native = CreateProject();
        var edit = native.Session;
        var id = edit.Document.Layers[0].Id;
        var destination = Path.Combine(directory, "gesture.ppgproj");
        var calls = 0;
        if (beforePicker) edit.BeginGesture(id);
        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => native.SaveAsAsync(() =>
            {
                calls++;
                edit.BeginGesture(id);
                edit.MoveBy(20, 30);
                return Task.FromResult<string?>(destination);
            }, new()));
            Assert.That(calls, Is.EqualTo(beforePicker ? 0 : 1));
            Assert.That(File.Exists(destination), Is.False);
            Assert.That(native.CurrentPath, Is.Null);
        }
        finally { edit.CancelGesture(); Directory.Delete(directory, recursive: true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ImportedSourceProtectionAppliesAfterExtensionNormalization(bool omitExtension)
    {
        var directory = MakeDirectory();
        var native = CreateProject();
        var destination = Path.Combine(directory, "protected.ppgproj");
        var sources = new ProtectedSourcePaths();
        sources.Add(destination.ToUpperInvariant());
        var prior = new byte[] { 7, 8, 9 };
        File.WriteAllBytes(destination, prior);
        var before = native.Session.Document.Copy();
        var undo = native.Session.UndoCount;
        try
        {
            var chosen = omitExtension ? destination[..^".ppgproj".Length] : destination;
            Assert.ThrowsAsync<InvalidOperationException>(() => native.SaveAsAsync(() => Task.FromResult<string?>(chosen), sources));
            Assert.That(File.ReadAllBytes(destination), Is.EqualTo(prior));
            Assert.That(native.Session.Document.StateEquals(before), Is.True);
            Assert.That(native.Session.UndoCount, Is.EqualTo(undo));
            Assert.That(native.IsDirty, Is.True);
            Assert.That(native.CurrentPath, Is.Null);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task CancelAfterDocumentReplacementReturnsWithoutSaving()
    {
        var native = CreateProject();
        Assert.That(await native.SaveAsAsync(() =>
        {
            native.New();
            return Task.FromResult<string?>(null);
        }, new()), Is.False);
        Assert.That(native.Session.Document.Layers, Is.Empty);
        Assert.That(native.CurrentPath, Is.Null);
        Assert.That(native.IsDirty, Is.False);
    }

    [Test]
    public void FailedPickerKeepsDocumentHistoryAndSavepoint()
    {
        var native = CreateProject();
        var before = native.Session.Document.Copy();
        var undo = native.Session.UndoCount;
        Assert.ThrowsAsync<IOException>(() => native.SaveAsAsync(() => throw new IOException("picker failed"), new()));
        Assert.That(native.Session.Document.StateEquals(before), Is.True);
        Assert.That(native.Session.UndoCount, Is.EqualTo(undo));
        Assert.That(native.IsDirty, Is.True);
        Assert.That(native.CurrentPath, Is.Null);
    }

    [Test]
    public void FailedWriteKeepsTheExistingSavePathAndDirtyRevision()
    {
        var directory = MakeDirectory();
        var native = CreateProject();
        var current = Path.Combine(directory, "current.ppgproj");
        var blocked = Path.Combine(directory, "directory.ppgproj");
        Directory.CreateDirectory(blocked);
        try
        {
            native.SaveAs(current);
            var prior = File.ReadAllBytes(current);
            native.Session.UpdateLayer(native.Session.Document.Layers[0].Id, l => l.Text = "unsaved");
            var before = native.Session.Document.Copy();
            var undo = native.Session.UndoCount;
            Assert.That(async () => await native.SaveAsAsync(() => Task.FromResult<string?>(blocked), new()), Throws.Exception);
            Assert.That(File.ReadAllBytes(current), Is.EqualTo(prior));
            Assert.That(native.Session.Document.StateEquals(before), Is.True);
            Assert.That(native.Session.UndoCount, Is.EqualTo(undo));
            Assert.That(native.IsDirty, Is.True);
            Assert.That(native.CurrentPath, Is.EqualTo(current));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task SelectionChangesDoNotInvalidateTheProjectBeingSaved()
    {
        var directory = MakeDirectory();
        var native = CreateProject();
        var destination = Path.Combine(directory, "selection.ppgproj");
        try
        {
            Assert.That(await native.SaveAsAsync(() =>
            {
                native.Session.SelectedLayerId = null;
                return Task.FromResult<string?>(destination);
            }, new()), Is.True);
            Assert.That(native.Session.SelectedLayerId, Is.Null);
            Assert.That(native.CurrentPath, Is.EqualTo(destination));
            Assert.That(native.IsDirty, Is.False);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static NativeProjectWorkflow CreateProject()
    {
        var session = new EditSession(new EditorDocument());
        session.AddLayer(new Layer { Kind = LayerKind.Text, Text = "original" });
        return new(session, d => d.Validate(), "test");
    }

    private static string MakeDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "p5-save-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
