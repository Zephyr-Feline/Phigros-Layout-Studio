using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5ProjectLayerImportTests
{
    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    [TestCase(1080, 1920)]
    public void ImportsFullCompositionWithFreshIdsAndOneUndo(int width, int height)
    {
        var source = Source(); var original = source.Copy();
        var target = source.Copy(); target.CanvasWidth = width; target.CanvasHeight = height;
        target.BackgroundArgb = 0xFF010203; target.ExtensionData["owner"] = "target";
        var edit = new EditSession(target); var before = edit.Document.Copy();
        var ids = edit.ImportProjectLayers(source);
        var imported = edit.Document.Layers.Skip(before.Layers.Count).ToArray();
        Assert.That(ids, Is.EqualTo(imported.Select(l => l.Id)));
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(ids));
        Assert.That(imported.Select(l => l.Id).Intersect(source.Layers.Select(l => l.Id)), Is.Empty);
        Assert.That(imported[0].GroupId, Is.Not.EqualTo(source.Layers[0].GroupId));
        Assert.That(imported[0].GroupId, Is.EqualTo(imported[1].GroupId));
        for (var i = 0; i < imported.Length; i++)
        {
            var compare = imported[i].Copy();
            compare.Id = source.Layers[i].Id; compare.GroupId = source.Layers[i].GroupId;
            compare.AssetId = source.Layers[i].AssetId;
            Assert.That(compare.StateEquals(source.Layers[i]), Is.True);
        }
        Assert.That(edit.Document.CanvasWidth, Is.EqualTo(width));
        Assert.That(edit.Document.CanvasHeight, Is.EqualTo(height));
        Assert.That(edit.Document.BackgroundArgb, Is.EqualTo(before.BackgroundArgb));
        Assert.That(edit.Document.BackgroundBlend, Is.EqualTo(before.BackgroundBlend));
        Assert.That(edit.Document.ExtensionData, Is.EqualTo(before.ExtensionData));
        Assert.That(source.StateEquals(original), Is.True);
        Assert.That(edit.UndoCount, Is.EqualTo(1)); Assert.That(edit.IsDirty, Is.True);
        var after = edit.Document.Copy();
        edit.Undo(); Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(edit.IsDirty, Is.False);
        edit.Redo(); Assert.That(edit.Document.StateEquals(after), Is.True);
    }

    [Test]
    public void DeduplicatesPayloadsAndExcludesSourceBackgroundAndUnusedAssets()
    {
        var source = Source(); var imageId = source.Layers[0].AssetId!.Value;
        var duplicate = Guid.NewGuid(); source.AddAsset(duplicate, source.Assets[imageId]);
        var extra = source.Layers[0].Copy(); extra.Id = Guid.NewGuid(); extra.AssetId = duplicate;
        source.Layers.Add(extra);
        var background = Guid.NewGuid(); source.AddAsset(background, new byte[] { 2 });
        source.FixedBackgroundAssetId = background;
        source.AddAsset(Guid.NewGuid(), new byte[] { 3 });
        var existing = Guid.NewGuid(); var target = new EditorDocument();
        target.AddAsset(existing, source.Assets[imageId]); target.FixedBackgroundAssetId = existing;
        var edit = new EditSession(target);
        edit.ImportProjectLayers(source); edit.ImportProjectLayers(source);
        Assert.That(edit.Document.Assets.Keys, Is.EqualTo(new[] { existing }));
        Assert.That(edit.Document.Layers.Where(l => l.Kind == LayerKind.Image).All(l => l.AssetId == existing), Is.True);
        Assert.That(edit.Document.FixedBackgroundAssetId, Is.EqualTo(existing));
        Assert.That(ReferenceEquals(edit.Document.Assets[existing], source.Assets[imageId]), Is.True);
        Assert.That(edit.Document.Layers.Select(l => l.Id).Distinct().Count(), Is.EqualTo(8));
        Assert.That(edit.Document.Layers.Select(l => l.GroupId).OfType<Guid>().Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public void ConflictingAssetIdDoesNotReplaceExistingBytes()
    {
        var source = Source(); var id = source.Layers[0].AssetId!.Value;
        var target = new EditorDocument(); target.AddAsset(id, new byte[] { 7 }); target.FixedBackgroundAssetId = id;
        var edit = new EditSession(target); edit.ImportProjectLayers(source);
        var importedId = edit.Document.Layers[0].AssetId!.Value;
        Assert.That(importedId, Is.Not.EqualTo(id));
        Assert.That(edit.Document.Assets[id].ToArray(), Is.EqualTo(new byte[] { 7 }));
        Assert.That(edit.Document.Assets[importedId], Is.SameAs(source.Assets[id]));
        edit.Undo(); Assert.That(edit.Document.Assets.Count, Is.EqualTo(1));
        edit.Redo(); Assert.That(edit.Document.Assets[importedId], Is.SameAs(source.Assets[id]));
    }

    [TestCase("empty")]
    [TestCase("invalid")]
    [TestCase("limit")]
    [TestCase("gesture")]
    [TestCase("asset-limit")]
    public void FailedImportPreservesDocumentSelectionAndHistory(string failure)
    {
        var source = Source(); var edit = new EditSession(new EditorDocument
            { Layers = [new Layer { Kind = LayerKind.Shape }] });
        var first = edit.Document.Layers[0].Id; edit.SelectedLayerId = first;
        edit.UpdateLayer(first, l => l.Name = "edited"); edit.Undo();
        if (failure == "limit") source.Layers = Enumerable.Range(0, 200).Select(_ => new Layer { Kind = LayerKind.Shape }).ToList();
        if (failure == "empty") source.Layers.Clear();
        if (failure == "invalid") source.Layers[0].AssetId = Guid.NewGuid();
        if (failure == "asset-limit")
            for (var i = 0; i < 200; i++) edit.Document.AddAsset(Guid.NewGuid(), new byte[] { 7 });
        if (failure == "gesture") edit.BeginGesture(first);
        var before = edit.Document.Copy(); var undo = edit.UndoCount; var redo = edit.RedoCount;
        Assert.That(() => edit.ImportProjectLayers(source), Throws.Exception);
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.SelectedLayerId, Is.EqualTo(first));
        Assert.That((edit.UndoCount, edit.RedoCount), Is.EqualTo((undo, redo)));
        if (failure == "gesture") edit.CancelGesture();
    }

    [Test]
    public void ImportAfterUndoReusesRetainedBytesAndClearsRedo()
    {
        var source = Source(); var asset = source.Assets.Values.Single();
        var edit = new EditSession(new EditorDocument());
        edit.ImportProjectLayers(source); edit.Undo();
        var next = Source();
        edit.ImportProjectLayers(next);
        Assert.That(edit.Document.Assets.Values.Single(), Is.SameAs(asset));
        Assert.That(edit.RedoCount, Is.Zero); Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.Document.Layers.Select(l => l.Id).Intersect(next.Layers.Select(l => l.Id)), Is.Empty);
    }

    [Test]
    public void ActiveGestureRejectsBeforePicker()
    {
        var edit = new EditSession(new EditorDocument { Layers = [new Layer { Kind = LayerKind.Shape }] });
        edit.BeginGesture(edit.Document.Layers[0].Id);
        var workflow = new ProjectLayerImportWorkflow(edit, _ => { }, new ProtectedSourcePaths(), () => null);
        Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ImportAsync(
            () => throw new AssertionException("Unexpected picker")));
        edit.CancelGesture();
    }

    [Test]
    public void ChangedSaveTargetRejectsBeforeReadingSource()
    {
        var edit = new EditSession(new EditorDocument()); string current = "first.ppgproj";
        var workflow = new ProjectLayerImportWorkflow(edit, _ => { }, new ProtectedSourcePaths(), () => current);
        Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ImportAsync(() =>
        {
            current = "second.ppgproj"; return Task.FromResult<string?>("never-read.ppgproj");
        }));
    }

    [TestCase("cancel")]
    [TestCase("change")]
    [TestCase("replace")]
    [TestCase("gesture")]
    public void PickerCancellationAndSessionChangesNeverImport(string mode)
    {
        var edit = new EditSession(new EditorDocument { Layers = [new Layer { Kind = LayerKind.Shape }] });
        var workflow = new ProjectLayerImportWorkflow(edit,
            _ => throw new AssertionException("Unexpected decode"), new ProtectedSourcePaths(), () => null);
        Task<int> Run() => workflow.ImportAsync(() =>
        {
            if (mode == "change") edit.UpdateDocument(d => d.BackgroundArgb = 0xFF112233);
            if (mode == "replace") edit.ReplaceDocument(edit.Document.Copy());
            if (mode == "gesture") edit.BeginGesture(edit.Document.Layers[0].Id);
            return Task.FromResult<string?>(mode == "cancel" ? null : "never-read.ppgproj");
        });
        if (mode == "cancel") Assert.That(Run().GetAwaiter().GetResult(), Is.Zero);
        else Assert.ThrowsAsync<InvalidOperationException>(Run);
        if (mode == "gesture") edit.CancelGesture();
    }

    [Test]
    public async Task NativeImportIsSelfContainedPreservesSourceAndProtectsItsPath()
    {
        AvaloniaTestHost.EnsureStarted();
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".ppgproj");
        var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".ppgproj");
        try
        {
            var source = Source(); var asset = source.Layers[0].AssetId!.Value;
            source.Assets[asset] = new AssetBlob(TestImages.RenderPng(new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 }));
            NativePackageStore.Save(path, source, "test");
            var bytes = File.ReadAllBytes(path); var time = File.GetLastWriteTimeUtc(path);
            var edit = new EditSession(new EditorDocument()); var paths = new ProtectedSourcePaths();
            var workflow = new ProjectLayerImportWorkflow(edit, TestImages.ValidateImages, paths, () => output);
            Assert.That(await workflow.ImportAsync(() => Task.FromResult<string?>(path)), Is.EqualTo(3));
            Assert.That(paths.Contains(path), Is.True);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes)); Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(time));
            Assert.Throws<InvalidOperationException>(() => paths.EnsureNotProtected(path));
            var expected = TestImages.RenderPng(source, true);
            Assert.That(TestImages.RenderPng(edit.Document, true), Is.EqualTo(expected));
            NativePackageStore.Save(output, edit.Document, "test"); File.Delete(path);
            Assert.That(TestImages.RenderPng(NativePackageStore.Read(output), true), Is.EqualTo(expected));
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [TestCase("corrupt")]
    [TestCase("image")]
    [TestCase("current")]
    public void FailedFileImportDoesNotProtectOrChangeAnything(string mode)
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".ppgproj");
        try
        {
            if (mode == "corrupt") File.WriteAllText(path, "not a zip");
            else NativePackageStore.Save(path, Source(), "test");
            var edit = new EditSession(new EditorDocument()); var before = edit.Document.Copy();
            var paths = new ProtectedSourcePaths();
            var workflow = new ProjectLayerImportWorkflow(edit, TestImages.ValidateImages, paths,
                () => mode == "current" ? path.ToUpperInvariant() : null);
            Assert.That(async () => await workflow.ImportAsync(() => Task.FromResult<string?>(path)), Throws.Exception);
            Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(paths.Count, Is.Zero);
        }
        finally { File.Delete(path); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DesktopImportButtonUsesPickerAndPreservesNativeSaveTarget(bool cancel)
    {
        AvaloniaTestHost.EnsureStarted();
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".ppgproj");
        var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".ppgproj");
        var edit = new EditSession(new EditorDocument());
        var native = new NativeProjectWorkflow(edit, TestImages.ValidateImages, "test");
        var theme = new Avalonia.Themes.Fluent.FluentTheme(); Avalonia.Application.Current!.Styles.Add(theme);
        var calls = 0;
        var window = new MainWindow(edit, nativeWorkflow: native,
            nativeSavePathPicker: () => Task.FromResult<string?>(path), projectLayerPathPicker: () =>
            { calls++; return Task.FromResult<string?>(cancel ? null : path); });
        try
        {
            NativePackageStore.Save(path, new EditorDocument { Layers = [EditorDocument.NewBadge(new(50, 50))] }, "test");
            native.SaveAs(output); window.Show();
            UiTestActions.Click(UiTestActions.Find(window, "Import project layers"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.That(calls, Is.EqualTo(1)); Assert.That(edit.Document.Layers.Count, Is.EqualTo(cancel ? 0 : 1));
            Assert.That(native.CurrentPath, Is.EqualTo(output)); Assert.That(edit.UndoCount, Is.EqualTo(cancel ? 0 : 1));
            if (!cancel)
            {
                var original = File.ReadAllBytes(path);
                UiTestActions.Click(UiTestActions.Find(window, "Save Project As"));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.That(native.CurrentPath, Is.EqualTo(output));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(original)); Assert.That(edit.IsDirty, Is.True);
            }
        }
        finally { edit.MarkSaved(); window.Close(); Avalonia.Application.Current!.Styles.Remove(theme); File.Delete(path); File.Delete(output); }
    }

    private static EditorDocument Source()
    {
        var id = Guid.NewGuid(); var group = Guid.NewGuid();
        var doc = new EditorDocument { BackgroundArgb = 0xFF654321, Layers = [
            new Layer { Kind = LayerKind.Image, AssetId = id, GroupId = group, Name = "曲绘", Slanted = true,
                Bounds = new(20, 20, 100, 80) },
            new Layer { Kind = LayerKind.Text, GroupId = group, Name = "谱师", Text = "中文 Title", Locked = true,
                Visible = false, Bold = true, FontSize = 24, LineSpacing = 1.5, VerticalAlignment = VerticalAlignment.Bottom },
            EditorDocument.NewBadge(new(200, 100))] };
        doc.Layers[2].GroupId = group;
        doc.AddAsset(id, new byte[] { 1 }); doc.ExtensionData["source"] = "retained in source only";
        return doc;
    }
}
