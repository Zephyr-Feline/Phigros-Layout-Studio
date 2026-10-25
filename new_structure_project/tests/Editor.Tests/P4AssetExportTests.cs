using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4AssetExportTests
{
    [Test]
    public void ReplacingVisibleImageChangesExportAtTheSameCanvasBounds()
    {
        AvaloniaTestHost.EnsureStarted();
        var first = SolidPng(40, 20, 0xFFFF0000);
        var second = SolidPng(20, 40, 0xFF0000FF);
        var image = new Layer { Kind = LayerKind.Image, AssetId = Guid.NewGuid(),
            Bounds = new RectD(40, 30, 160, 80) };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 320, CanvasHeight = 180,
            BackgroundArgb = 0xFF000000 });
        edit.AddImageLayer(image, first);
        var (_, _, before) = TestImages.DecodePng(TestImages.RenderPng(edit.Document));
        edit.ReplaceImageLayer(image.Id, second);
        var (_, _, after) = TestImages.DecodePng(TestImages.RenderPng(edit.Document));
        var at = (70 * 320 + 100) * 4;
        Assert.Multiple(() =>
        {
            Assert.That(before[at..(at + 4)], Is.EqualTo(new byte[] { 0, 0, 255, 255 }));
            Assert.That(after[at..(at + 4)], Is.EqualTo(new byte[] { 255, 0, 0, 255 }));
            Assert.That(edit.Document.Find(image.Id).Bounds, Is.EqualTo(image.Bounds));
        });
    }

    [Test]
    public void ReplaceImagePreservesPlacementAndHistoryWhileRetiringOldAsset()
    {
        var first = SolidPng(40, 20, 0xFFFF0000);
        var second = SolidPng(20, 40, 0xFF0000FF);
        var group = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = Guid.NewGuid(),
            Name = "art", GroupId = group, Locked = true, Visible = false,
            Bounds = new RectD(17, 29, 300, 150) };
        var edit = new EditSession(new EditorDocument());
        edit.AddImageLayer(image, first);
        var originalId = edit.Document.Find(image.Id).AssetId!.Value;
        edit.MarkSaved();
        edit.ReplaceImageLayer(image.Id, (byte[])first.Clone());
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.IsDirty, Is.False);

        edit.ReplaceImageLayer(image.Id, second);
        var replacement = edit.Document.Find(image.Id);
        var replacementId = replacement.AssetId!.Value;
        Assert.Multiple(() =>
        {
            Assert.That(replacement.Id, Is.EqualTo(image.Id));
            Assert.That(replacement.Bounds, Is.EqualTo(image.Bounds));
            Assert.That(replacement.GroupId, Is.EqualTo(group));
            Assert.That(replacement.Locked, Is.True);
            Assert.That(replacement.Visible, Is.False);
            Assert.That(replacement.Name, Is.EqualTo("art"));
            Assert.That(replacementId, Is.Not.EqualTo(originalId));
            Assert.That(edit.Document.Assets.Keys, Is.EquivalentTo(new[] { replacementId }));
            Assert.That(edit.SelectedLayerId, Is.EqualTo(image.Id));
            Assert.That(edit.UndoCount, Is.EqualTo(2));
        });
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p4-replace-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "p4-test");
            Assert.That(NativePackageStore.Read(path).StateEquals(edit.Document), Is.True);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Find(image.Id).AssetId, Is.EqualTo(originalId));
        Assert.That(edit.Document.Assets[originalId].ToArray(), Is.EqualTo(first));
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(image.Id).AssetId, Is.EqualTo(replacementId));
        Assert.That(edit.Document.Assets[replacementId].ToArray(), Is.EqualTo(second));
        Assert.Throws<KeyNotFoundException>(() => edit.ReplaceImageLayer(Guid.NewGuid(), second));
    }

    [Test]
    public void ExportReportsRenderAndWriteFailuresWithoutChangingExistingFileOrEditState()
    {
        AvaloniaTestHost.EnsureStarted();
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p4-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var target = Path.Combine(folder, "output.png");
            File.WriteAllBytes(target, [1, 2, 3, 4]);
            var projectPath = Path.Combine(folder, "project.ppgproj");
            var edit = new EditSession(new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 });
            var native = new NativeProjectWorkflow(edit, TestImages.ValidateImages, "p4-test");
            native.SaveAs(projectPath);
            edit.UpdateDocument(doc => doc.BackgroundArgb = 0xFF112233);
            var before = edit.Document.Copy();
            var originalProject = File.ReadAllBytes(projectPath);
            var sources = new ProtectedSourcePaths();
            PngExportWorkflow Workflow(Func<EditorDocument, bool, int?, byte[]> render,
                Action<string, byte[]> write) => new(edit, TestImages.ValidateImages, render,
                    write, sources, () => native.CurrentPath);

            var renderFailure = Workflow((_, _, _) => throw new IOException("render failed"),
                NativeDocumentStore.WriteBytesAtomically);
            Assert.Throws<IOException>(() => renderFailure.Export(target));
            var writeFailure = Workflow((doc, transparent, width) => TestImages.RenderPng(doc, transparent, width),
                (_, _) => throw new IOException("commit failed"));
            Assert.Throws<IOException>(() => writeFailure.Export(target));
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllBytes(target), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                Assert.That(File.ReadAllBytes(projectPath), Is.EqualTo(originalProject));
                Assert.That(edit.Document.StateEquals(before), Is.True);
                Assert.That(native.CurrentPath, Is.EqualTo(projectPath));
                Assert.That(edit.IsDirty, Is.True);
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });

            sources.Add(target);
            Assert.Throws<InvalidOperationException>(() => writeFailure.Export(target));
            sources.Clear();
            Assert.Throws<InvalidOperationException>(() => writeFailure.Export(projectPath));
            Assert.That(File.ReadAllBytes(projectPath), Is.EqualTo(originalProject));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public void TransparentExportSkipsBackgroundAndNeverMarksProjectSaved()
    {
        AvaloniaTestHost.EnsureStarted();
        var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p4-transparent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var shape = new Layer { Kind = LayerKind.Shape, ColorArgb = 0xFFFF0000,
                Bounds = new RectD(10, 10, 30, 20) };
            var edit = new EditSession(new EditorDocument { CanvasWidth = 320, CanvasHeight = 180,
                Layers = [shape] });
            edit.UpdateLayer(shape.Id, layer => layer.Name = "edited");
            var before = edit.Document.Copy();
            var workflow = new PngExportWorkflow(edit, TestImages.ValidateImages,
                (doc, transparent, width) => TestImages.RenderPng(doc, transparent, width),
                NativeDocumentStore.WriteBytesAtomically, new ProtectedSourcePaths(), () => null);
            var target = Path.Combine(folder, "transparent.png");
            workflow.Export(target, transparentBackground: true);
            var (width, _, pixels) = TestImages.DecodePng(File.ReadAllBytes(target));
            Assert.Multiple(() =>
            {
                Assert.That(pixels[3], Is.Zero, "empty canvas remains transparent");
                Assert.That(pixels[(15 * width + 15) * 4 + 3], Is.EqualTo(255));
                Assert.That(edit.Document.StateEquals(before), Is.True);
                Assert.That(edit.IsDirty, Is.True);
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Test]
    public void ExportRejectsInvalidImageAndActiveGestureBeforeWriting()
    {
        AvaloniaTestHost.EnsureStarted();
        var assetId = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = assetId };
        var doc = new EditorDocument { CanvasWidth = 320, CanvasHeight = 180,
            Layers = [image] };
        doc.AddAsset(assetId, [1, 2, 3]);
        var edit = new EditSession(doc);
        var target = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p4-invalid-export-" + Guid.NewGuid().ToString("N") + ".png");
        var workflow = new PngExportWorkflow(edit, TestImages.ValidateImages,
            (candidate, transparent, width) => TestImages.RenderPng(candidate, transparent, width),
            NativeDocumentStore.WriteBytesAtomically, new ProtectedSourcePaths(), () => null);
        Assert.Throws<InvalidDataException>(() => workflow.Export(target));
        Assert.That(File.Exists(target), Is.False);
        edit.BeginGesture(image.Id);
        Assert.Throws<InvalidOperationException>(() => workflow.Export(target));
        edit.CancelGesture();
        Assert.That(File.Exists(target), Is.False);
    }

    private static byte[] SolidPng(int width, int height, uint argb)
    {
        var pixels = new byte[width * height * 4];
        for (var at = 0; at < pixels.Length; at += 4)
        {
            pixels[at] = (byte)argb;
            pixels[at + 1] = (byte)(argb >> 8);
            pixels[at + 2] = (byte)(argb >> 16);
            pixels[at + 3] = (byte)(argb >> 24);
        }
        return TestImages.EncodePng(width, height, pixels);
    }
}
