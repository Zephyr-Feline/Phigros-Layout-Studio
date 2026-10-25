using Avalonia.LogicalTree;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P5SelectionProjectTests
{
    [TestCase(true, 3)]
    [TestCase(false, 1)]
    public void CapturesSelectedUnitsIncludingHiddenLockedMembersInPaintOrder(bool groups, int count)
    {
        var doc = Source(); var before = doc.Copy(); var selected = doc.Layers[2];
        var result = SelectionProject.Create(doc, [selected.Id, selected.Id], groups);
        Assert.That(result.Layers.Count, Is.EqualTo(count));
        Assert.That(result.Layers.Select(l => l.Id), Is.EqualTo(groups
            ? doc.Layers.Take(3).Select(l => l.Id) : new[] { selected.Id }));
        foreach (var layer in result.Layers)
        {
            Assert.That(layer.StateEquals(doc.Find(layer.Id)), Is.True);
            Assert.That(layer, Is.Not.SameAs(doc.Find(layer.Id)));
        }
        Assert.That(doc.StateEquals(before), Is.True);
        result.Layers[0].Name = "edit saved copy";
        Assert.That(doc.StateEquals(before), Is.True);
    }

    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    [TestCase(1080, 1920)]
    public void KeepsCanvasAndGeometryButOnlyCarriesRequiredImages(int w, int h)
    {
        var doc = Source(); doc.CanvasWidth = w; doc.CanvasHeight = h;
        var imageId = doc.Layers[0].AssetId!.Value;
        var result = SelectionProject.Create(doc, [doc.Layers[0].Id], true);
        Assert.That((result.CanvasWidth, result.CanvasHeight), Is.EqualTo((w, h)));
        Assert.That(result.BackgroundArgb, Is.EqualTo(doc.BackgroundArgb));
        Assert.That(result.FixedBackgroundAssetId, Is.Null); Assert.That(result.BackgroundBlend.Enabled, Is.False);
        Assert.That(result.ExtensionData, Is.Empty);
        Assert.That(result.Assets.Keys, Is.EqualTo(new[] { imageId }));
        Assert.That(result.Assets[imageId], Is.SameAs(doc.Assets[imageId]));
        Assert.That(result.Layers[0].Bounds, Is.EqualTo(doc.Layers[0].Bounds));
    }

    [Test]
    public void SelectionOrderNeverReordersPaintAndSharedBackgroundImageIsRetainedWhenUsedByLayer()
    {
        var doc = Source(); doc.FixedBackgroundAssetId = doc.Layers[0].AssetId;
        var result = SelectionProject.Create(doc, [doc.Layers[3].Id, doc.Layers[0].Id], false);
        Assert.That(result.Layers.Select(l => l.Id), Is.EqualTo(new[] { doc.Layers[0].Id, doc.Layers[3].Id }));
        Assert.That(result.Assets.Count, Is.EqualTo(1));
        Assert.That(result.FixedBackgroundAssetId, Is.Null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EmptyOrMissingSelectionIsRejected(bool stale)
    {
        Assert.That(() => SelectionProject.Create(Source(), stale ? [Guid.NewGuid()] : [], true), Throws.Exception);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeFileRoundTripAndReimportKeepPixelsAndCurrentSession(bool dirty)
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = Session(); var first = edit.Document.Layers[0].Id;
        if (dirty) edit.UpdateLayer(first, l => l.Name = "unsaved");
        else { edit.UpdateLayer(first, l => l.Name = "redo"); edit.Undo(); }
        var before = edit.Document.Copy(); var undo = edit.UndoCount; var redo = edit.RedoCount;
        var path = NewPath();
        try
        {
            var expected = SelectionProject.Create(edit.Document, edit.SelectedLayerIds, true);
            var workflow = Workflow(edit);
            Assert.That(await workflow.SaveAsync(true, () => Task.FromResult<string?>(path)), Is.True);
            var saved = NativePackageStore.Read(path);
            Assert.That(saved.StateEquals(expected), Is.True);
            var target = new EditSession(new EditorDocument { CanvasWidth = saved.CanvasWidth, CanvasHeight = saved.CanvasHeight });
            await new ProjectLayerImportWorkflow(target, TestImages.ValidateImages, new ProtectedSourcePaths(), () => null)
                .ImportAsync(() => Task.FromResult<string?>(path));
            Assert.That(TestImages.RenderPng(target.Document, true), Is.EqualTo(TestImages.RenderPng(expected, true)));
            Assert.That(edit.Document.StateEquals(before), Is.True);
            Assert.That((edit.UndoCount, edit.RedoCount, edit.IsDirty), Is.EqualTo((undo, redo, dirty)));
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { first }));
        }
        finally { File.Delete(path); }
    }

    [TestCase("cancel")]
    [TestCase("document")]
    [TestCase("selection")]
    [TestCase("replace")]
    [TestCase("path")]
    [TestCase("gesture")]
    public void ChangesDuringPickerAndCancellationNeverWrite(string mode)
    {
        var edit = Session(); var path = NewPath(); string? current = null;
        var workflow = Workflow(edit, current: () => current);
        Task<bool> Run() => workflow.SaveAsync(true, () =>
        {
            if (mode == "document") edit.UpdateDocument(d => d.BackgroundArgb = 0xFF112233);
            if (mode == "selection") edit.SetSelection([edit.Document.Layers[3].Id]);
            if (mode == "replace") edit.ReplaceDocument(edit.Document.Copy());
            if (mode == "path") current = "other.ppgproj";
            if (mode == "gesture") edit.BeginGesture(edit.Document.Layers[0].Id);
            return Task.FromResult<string?>(mode == "cancel" ? null : path);
        });
        if (mode == "cancel") Assert.That(Run().GetAwaiter().GetResult(), Is.False);
        else Assert.ThrowsAsync<InvalidOperationException>(Run);
        Assert.That(File.Exists(path), Is.False);
        if (mode == "gesture") edit.CancelGesture();
    }

    [TestCase("empty")]
    [TestCase("gesture")]
    [TestCase("image")]
    public void InvalidSelectionStopsBeforePicker(string mode)
    {
        var edit = Session();
        if (mode == "empty") edit.SetSelection([]);
        if (mode == "gesture") edit.BeginGesture(edit.Document.Layers[0].Id);
        if (mode == "image") edit.Document.Assets[edit.Document.Layers[0].AssetId!.Value] = new AssetBlob([1]);
        Task<bool> Run() => Workflow(edit).SaveAsync(true,
            () => throw new AssertionException("Unexpected picker"));
        if (mode == "image") Assert.ThrowsAsync<InvalidDataException>(Run);
        else Assert.ThrowsAsync<InvalidOperationException>(Run);
        if (mode == "gesture") edit.CancelGesture();
    }

    [TestCase("current")]
    [TestCase("source")]
    [TestCase("locked")]
    public void FailedWritePreservesExistingFileAndDirtyState(string mode)
    {
        var path = NewPath(); File.WriteAllBytes(path, [8, 7, 6]);
        var edit = Session(); edit.UpdateLayer(edit.Document.Layers[0].Id, l => l.Name = "dirty");
        var before = edit.Document.Copy(); var sources = new ProtectedSourcePaths();
        if (mode == "source") sources.Add(path);
        using var locked = mode == "locked" ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        try
        {
            var workflow = Workflow(edit, sources, () => mode == "current" ? path.ToUpperInvariant() : null);
            Assert.That(async () => await workflow.SaveAsync(true, () => Task.FromResult<string?>(path)), Throws.Exception);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 8, 7, 6 }));
            Assert.That(edit.Document.StateEquals(before), Is.True); Assert.That(edit.IsDirty, Is.True);
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        }
        finally { locked?.Dispose(); File.Delete(path); }
    }

    [Test]
    public async Task AddsNativeExtensionAndReplacesExistingOutputAtomically()
    {
        var path = NewPath(); File.WriteAllBytes(path, [1]);
        try
        {
            var edit = Session();
            await Workflow(edit).SaveAsync(false, () => Task.FromResult<string?>(Path.ChangeExtension(path, null)));
            Assert.That(NativePackageStore.Read(path).Layers.Count, Is.EqualTo(1));
            Assert.That(edit.IsDirty, Is.False);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task UnselectedBadBackgroundIsExcludedAndNotDecoded()
    {
        var edit = Session();
        edit.Document.Assets[edit.Document.FixedBackgroundAssetId!.Value] = new AssetBlob([1, 2]);
        var path = NewPath();
        try
        {
            await Workflow(edit).SaveAsync(true, () => Task.FromResult<string?>(path));
            var saved = NativePackageStore.Read(path);
            TestImages.ValidateImages(saved);
            Assert.That(saved.Assets.Count, Is.EqualTo(1));
            Assert.That(edit.Document.Assets.Count, Is.EqualTo(2));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void RepeatedDesktopRequestsShareOnePickerAndCancelCleanly()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = Session(); var pending = new TaskCompletionSource<string?>(); var calls = 0;
        var theme = new Avalonia.Themes.Fluent.FluentTheme(); Avalonia.Application.Current!.Styles.Add(theme);
        var window = new MainWindow(edit, selectionProjectPathPicker: () => { calls++; return pending.Task; });
        try
        {
            window.Show();
            var button = UiTestActions.Find(window, "Save selection as project");
            UiTestActions.Click(button);
            UiTestActions.Click(button);
            Assert.That(calls, Is.EqualTo(1));
            pending.SetResult(null); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.That(edit.UndoCount, Is.Zero); Assert.That(edit.IsDirty, Is.False);
        }
        finally { pending.TrySetResult(null); edit.MarkSaved(); window.Close(); Avalonia.Application.Current!.Styles.Remove(theme); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DesktopButtonHonorsWholeGroupAndKeepsSaveTarget(bool groups)
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = Session(); var native = new NativeProjectWorkflow(edit, TestImages.ValidateImages, "test");
        var path = NewPath(); var original = NewPath();
        var theme = new Avalonia.Themes.Fluent.FluentTheme(); Avalonia.Application.Current!.Styles.Add(theme);
        var window = new MainWindow(edit, nativeWorkflow: native, selectionProjectPathPicker: () => Task.FromResult<string?>(path));
        try
        {
            native.SaveAs(original); window.Show();
            window.GetLogicalDescendants().OfType<CheckBox>().Single(c => c.Name == "EditWholeGroup").IsChecked = groups;
            UiTestActions.Click(UiTestActions.Find(window, "Save selection as project"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.That(File.Exists(path), Is.True);
            Assert.That(NativePackageStore.Read(path).Layers.Count, Is.EqualTo(groups ? 3 : 1));
            Assert.That(native.CurrentPath, Is.EqualTo(original)); Assert.That(edit.IsDirty, Is.False);
            Assert.That(edit.UndoCount, Is.Zero);
        }
        finally { edit.MarkSaved(); window.Close(); Avalonia.Application.Current!.Styles.Remove(theme); File.Delete(path); File.Delete(original); }
    }

    private static SelectionProjectWorkflow Workflow(EditSession edit, ProtectedSourcePaths? sources = null,
        Func<string?>? current = null) => new(edit, TestImages.ValidateImages, sources ?? new(), current ?? (() => null), "test");
    private static string NewPath() => Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid() + ".ppgproj");
    private static EditSession Session()
    {
        var doc = Source(); var id = doc.Layers[0].AssetId!.Value;
        doc.Assets[id] = new AssetBlob(TestImages.RenderPng(new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 }));
        doc.Assets[doc.FixedBackgroundAssetId!.Value] = doc.Assets[id];
        // The unrelated fixed background is not part of the selected composition.
        var edit = new EditSession(doc); edit.SetSelection([doc.Layers[0].Id]); return edit;
    }
    private static EditorDocument Source()
    {
        var image = Guid.NewGuid(); var background = Guid.NewGuid(); var group = Guid.NewGuid();
        var doc = new EditorDocument { BackgroundArgb = 0xFF345678, FixedBackgroundAssetId = background,
            BackgroundBlend = new(true), Layers = [
                new Layer { Kind = LayerKind.Image, AssetId = image, GroupId = group, Bounds = new(50, 50, 150, 100), Slanted = true },
                new Layer { Kind = LayerKind.Text, GroupId = group, Text = "隐藏谱师", Visible = false, Locked = true, FontSize = 24 },
                EditorDocument.NewBadge(new(200, 100)),
                new Layer { Kind = LayerKind.Shape, Name = "unselected" }] };
        doc.Layers[2].GroupId = group; doc.AddAsset(image, new byte[] { 1 }); doc.AddAsset(background, new byte[] { 2 });
        doc.ExtensionData["source-only"] = "metadata"; return doc;
    }
}
