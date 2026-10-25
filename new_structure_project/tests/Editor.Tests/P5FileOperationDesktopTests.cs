using Avalonia.LogicalTree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5FileOperationDesktopTests
{
    [Test]
    public void PendingReadBlocksOtherFileRequestsAndClosingThenAllowsRetry()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(new EditorDocument()); var reads = 0; var picks = 0; var saves = 0;
        var loading = new TaskCompletionSource<LoadedImage>();
        var window = new MainWindow(edit, nativeSavePathPicker: () => { saves++; return Task.FromResult<string?>(null); },
            filePathPicker: _ => { picks++; return Task.FromResult<string?>(picks == 1 ? "image.png" : null); },
            imageLoader: _ => { reads++; return loading.Task; });
        try
        {
            window.Show(); Click(window, "Import Image");
            Click(window, "Import Image"); Click(window, "Save Temporary JSON"); Click(window, "New");
            window.GetVisualDescendants().OfType<SceneCanvas>().Single().Focus();
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            window.Close();
            Assert.That(window.IsVisible, Is.True); Assert.That(window.OwnedWindows, Is.Empty);
            Assert.That(picks, Is.EqualTo(1)); Assert.That(reads, Is.EqualTo(1)); Assert.That(saves, Is.Zero);
            Assert.That(edit.Document.Layers, Is.Empty);
            loading.SetResult(Picture()); Dispatcher.UIThread.RunJobs();
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(1)); Assert.That(edit.UndoCount, Is.EqualTo(1));
            Click(window, "Import Image"); Assert.That(picks, Is.EqualTo(2)); Assert.That(reads, Is.EqualTo(1));
        }
        finally { edit.MarkSaved(); window.Close(); }
    }

    [TestCase(EditorFileAction.ImportImage, "Import Image")]
    [TestCase(EditorFileAction.ReplaceImage, "Replace image")]
    [TestCase(EditorFileAction.BackgroundImage, "Set background image")]
    public void ImageLoadedForAnEarlierDocumentCannotModifyTheReplacement(EditorFileAction action, string button)
    {
        AvaloniaTestHost.EnsureStarted(); var edit = ImageSession();
        var loading = new TaskCompletionSource<LoadedImage>();
        var window = new MainWindow(edit, filePathPicker: requested =>
        {
            Assert.That(requested, Is.EqualTo(action)); return Task.FromResult<string?>("new.png");
        }, imageLoader: _ => loading.Task);
        try
        {
            window.Show(); Click(window, button);
            edit.ReplaceDocument(edit.Document.Copy()); var current = edit.Document; var state = current.Copy();
            loading.SetResult(Picture()); Dispatcher.UIThread.RunJobs();
            Assert.That(edit.Document, Is.SameAs(current)); Assert.That(edit.Document.StateEquals(state), Is.True);
            Assert.That(edit.UndoCount, Is.Zero); Assert.That(edit.IsDirty, Is.False);
            Assert.That(Error(window), Does.Contain("发生变化"));
        }
        finally { edit.MarkSaved(); window.Close(); }
    }

    [TestCase("valid")]
    [TestCase("corrupt")]
    [TestCase("empty")]
    [TestCase("oversize")]
    [TestCase("missing")]
    public void ActualLocalImageReaderValidatesBeforeAddingAndReleasesTheFile(string kind)
    {
        AvaloniaTestHost.EnsureStarted(); var folder = Folder();
        var path = Path.Combine(folder, "自制 图片.png");
        var edit = new EditSession(new EditorDocument());
        var window = new MainWindow(edit, filePathPicker: _ => Task.FromResult<string?>(path));
        try
        {
            if (kind == "valid") File.WriteAllBytes(path, Picture().Image.ToArray());
            else if (kind == "oversize") { using var file = File.Create(path); file.SetLength(100L * 1024 * 1024 + 1); }
            else if (kind != "missing") File.WriteAllBytes(path, kind == "empty" ? [] : [1, 2, 3]);
            window.Show(); Click(window, "Import Image");
            PumpUntil(() => edit.UndoCount > 0 || Error(window) is not null);
            if (kind == "valid")
            {
                Assert.That(edit.Document.Layers.Single().Bounds, Is.EqualTo(new RectD(90, 90, 320, 180)));
                Assert.That(edit.UndoCount, Is.EqualTo(1)); TestImages.ValidateImages(edit.Document);
            }
            else
            {
                Assert.That(Error(window), Does.StartWith("错误："));
                Assert.That(edit.Document.Layers, Is.Empty); Assert.That(edit.Document.Assets, Is.Empty); Assert.That(edit.UndoCount, Is.Zero);
            }
            if (File.Exists(path))
            {
                using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        finally { edit.MarkSaved(); window.Close(); Directory.Delete(folder, recursive: true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FontRequestRejectsNewDocumentOrSelectionBeforeCopyingIntoTheLibrary(bool selection)
    {
        AvaloniaTestHost.EnsureStarted(); var folder = Folder();
        var edit = new EditSession(new EditorDocument { Layers = [new Layer { Kind = LayerKind.Text }, new Layer { Kind = LayerKind.Text }] });
        edit.SelectedLayerId = edit.Document.Layers[0].Id;
        var library = new LocalFontLibrary(Path.Combine(folder, "fonts"));
        var picker = new TaskCompletionSource<string?>();
        var window = new MainWindow(edit, library, filePathPicker: _ => picker.Task);
        try
        {
            window.Show(); Click(window, "Import local font (TTF / OTF)");
            if (selection) edit.SelectedLayerId = edit.Document.Layers[1].Id;
            else edit.ReplaceDocument(edit.Document.Copy());
            var state = edit.Document.Copy();
            picker.SetResult(Path.Combine(folder, "does-not-exist.ttf")); Dispatcher.UIThread.RunJobs();
            Assert.That(Error(window), Does.Contain("发生变化")); Assert.That(library.Fonts, Is.Empty);
            Assert.That(Directory.Exists(library.DirectoryPath), Is.False);
            Assert.That(edit.Document.StateEquals(state), Is.True); Assert.That(edit.UndoCount, Is.Zero);
        }
        finally { edit.MarkSaved(); window.Close(); Directory.Delete(folder, recursive: true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void LayoutPickerCannotReadOrWriteForAReplacedProject(bool save)
    {
        AvaloniaTestHost.EnsureStarted(); var folder = Folder(); var path = Path.Combine(folder, "existing.ppglayout");
        File.WriteAllBytes(path, [1, 2, 3]); var edit = new EditSession(new EditorDocument());
        var picker = new TaskCompletionSource<string?>();
        var window = new MainWindow(edit, filePathPicker: _ => picker.Task);
        try
        {
            window.Show(); var combo = window.GetLogicalDescendants().OfType<ComboBox>().Single(x => x.Name == "LayoutTemplate");
            var selected = combo.SelectedItem;
            Click(window, save ? "Save layout template" : "Open layout template");
            edit.ReplaceDocument(edit.Document.Copy());
            picker.SetResult(path); Dispatcher.UIThread.RunJobs();
            Assert.That(Error(window), Does.Contain("发生变化")); Assert.That(combo.SelectedItem, Is.SameAs(selected));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 })); Assert.That(edit.UndoCount, Is.Zero);
        }
        finally { edit.MarkSaved(); window.Close(); Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public void ChangedLayoutNameCannotOverwriteThePreviouslyChosenTarget()
    {
        AvaloniaTestHost.EnsureStarted(); var folder = Folder(); var path = Path.Combine(folder, "existing.ppglayout");
        File.WriteAllBytes(path, [1, 2, 3]); var edit = new EditSession(new EditorDocument());
        var picker = new TaskCompletionSource<string?>(); var window = new MainWindow(edit, filePathPicker: _ => picker.Task);
        try
        {
            window.Show(); Click(window, "Save layout template");
            window.GetLogicalDescendants().OfType<TextBox>().Single(x => x.Name == "LayoutTemplateName").Text = "later name";
            picker.SetResult(path); Dispatcher.UIThread.RunJobs();
            Assert.That(Error(window), Does.Contain("布局模板已发生变化"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
        finally { window.Close(); Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public void ProjectOpenPickerCannotReplaceTheProjectThatChangedWhileChoosing()
    {
        AvaloniaTestHost.EnsureStarted(); var folder = Folder(); var path = Path.Combine(folder, "new.ppgproj");
        NativePackageStore.Save(path, new EditorDocument(), "test");
        var edit = new EditSession(new EditorDocument { Layers = [new Layer { Kind = LayerKind.Text, Text = "keep" }] });
        var picker = new TaskCompletionSource<string?>(); var window = new MainWindow(edit, filePathPicker: _ => picker.Task);
        try
        {
            window.Show(); Click(window, "Open Project"); edit.ReplaceDocument(edit.Document.Copy());
            picker.SetResult(path); Dispatcher.UIThread.RunJobs();
            Assert.That(Error(window), Does.Contain("发生变化")); Assert.That(window.OwnedWindows, Is.Empty);
            Assert.That(edit.Document.Layers.Single().Text, Is.EqualTo("keep"));
        }
        finally { edit.MarkSaved(); window.Close(); Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public void NewPromptCannotDuplicateOrDiscardNewerContent()
    {
        AvaloniaTestHost.EnsureStarted(); var edit = new EditSession(new EditorDocument());
        edit.AddLayer(new Layer { Kind = LayerKind.Text, Text = "initial" }); var window = new MainWindow(edit);
        try
        {
            window.Show(); Click(window, "New"); Click(window, "New");
            var prompt = window.OwnedWindows.Single();
            edit.UpdateLayer(edit.Document.Layers[0].Id, l => l.Text = "newer content");
            Click(prompt, "Discard changes"); Dispatcher.UIThread.RunJobs();
            Assert.That(edit.Document.Layers.Single().Text, Is.EqualTo("newer content"));
            Assert.That(edit.IsDirty, Is.True); Assert.That(Error(window), Does.Contain("发生变化"));
            Click(window, "New"); Click(window.OwnedWindows.Single(), "Cancel"); Dispatcher.UIThread.RunJobs();
            Assert.That(edit.Document.Layers.Single().Text, Is.EqualTo("newer content"));
        }
        finally { edit.MarkSaved(); window.Close(); }
    }

    [Test]
    public void CleanWindowCannotCloseDuringAnActiveGesture()
    {
        AvaloniaTestHost.EnsureStarted(); var edit = new EditSession(new EditorDocument { Layers = [new Layer { Kind = LayerKind.Shape }] });
        var window = new MainWindow(edit);
        try
        {
            window.Show(); edit.BeginGesture(edit.Document.Layers[0].Id); edit.MoveBy(30, 20); window.Close();
            Assert.That(window.IsVisible, Is.True); Assert.That(Error(window), Does.Contain("拖动或缩放"));
            edit.CancelGesture(); window.Close(); Assert.That(window.IsVisible, Is.False);
        }
        finally { edit.CancelGesture(); edit.MarkSaved(); if (window.IsVisible) window.Close(); }
    }

    private static EditSession ImageSession()
    {
        var image = new Layer { Kind = LayerKind.Image, AssetId = Guid.NewGuid(), Bounds = new(40, 50, 80, 60) };
        var document = new EditorDocument { Layers = [image] }; document.AddAsset(image.AssetId!.Value, Picture().Image);
        return new(document) { SelectedLayerId = image.Id };
    }
    private static LoadedImage Picture() => new(new(TestImages.RenderPng(new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 })), 320, 180);
    private static void Click(Window window, string content) => UiTestActions.Click(UiTestActions.Find(window, content));
    private static string? Error(Window window) => UiTestActions.Error(window);
    private static void PumpUntil(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!completed() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Assert.That(completed(), Is.True, "The file operation did not complete.");
    }
    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "p5-file-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); return folder;
    }
}
