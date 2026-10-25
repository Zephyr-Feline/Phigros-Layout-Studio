using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R1CombinedWorkflowTests
{
    [Test]
    public async Task ImagesSongsSearchBatchStyleLibraryReopenAndQueueComposeOneWorkflow()
    {
        var root = Path.Combine(Path.GetTempPath(), "r1-flow-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var imageFolder = Path.Combine(root, "images");
            var libraryFolder = Path.Combine(root, "library");
            Directory.CreateDirectory(imageFolder);
            Directory.CreateDirectory(libraryFolder);
            var imagePaths = Enumerable.Range(0, 3).Select(index =>
                Path.Combine(imageFolder, $"自制曲绘 {index}.png")).ToArray();
            for (var index = 0; index < imagePaths.Length; index++)
            {
                var width = 300 + index * 20;
                File.WriteAllBytes(imagePaths[index],
                    TestImages.EncodePng(width, 220, TestImages.SyntheticBgra(width, 220)));
            }
            var edit = new EditSession(new EditorDocument
            {
                CanvasWidth = 1920, CanvasHeight = 1080, BackgroundArgb = 0xFF102030
            });
            var sources = new ProtectedSourcePaths();
            var importer = new BatchImageImportWorkflow(edit,
                path =>
                {
                    var bytes = File.ReadAllBytes(path);
                    var size = ImageAssetValidator.ReadEncodedDimensions(new AssetBlob(bytes));
                    return Task.FromResult(new LoadedImage(new AssetBlob(bytes), size.Width, size.Height));
                },
                TestImages.ValidateImages, sources, () => null);
            Assert.That(await importer.ImportAsync(() =>
                Task.FromResult<IReadOnlyList<string>>(imagePaths)), Is.EqualTo(3));
            var artworks = edit.Document.Layers.Where(layer => layer.Kind == LayerKind.Image).ToArray();
            Assert.That(artworks, Has.Length.EqualTo(3));
            foreach (var artwork in artworks) edit.CreateSongCard(artwork.Id);
            Assert.That(edit.Document.Layers, Has.Count.EqualTo(18));
            Assert.That(edit.Document.Layers.GroupBy(layer => layer.GroupId).Count(), Is.EqualTo(3));

            var titles = LayerSearch.Matches(edit.Document, "title");
            Assert.That(titles, Has.Count.EqualTo(3));
            edit.SetSelection(titles);
            edit.ApplyLayerStyleBatch(titles, new LayerStylePatch(FontSize: 42, Bold: true,
                ColorArgb: 0xFFFFD24A, Alignment: HorizontalAlignment.Center));
            Assert.That(titles.Select(id => edit.Document.Find(id).FontSize), Is.All.EqualTo(42));
            Assert.That(titles.Select(id => edit.Document.Find(id).Bold), Is.All.True);

            var badges = LayerSearch.Matches(edit.Document, "difficulty");
            Assert.That(badges, Has.Count.EqualTo(3));
            edit.ApplyLayerStyleBatch(badges, new LayerStylePatch(Difficulty: "AT", Level: 17));
            Assert.That(badges.Select(id => edit.Document.Find(id).Difficulty), Is.All.EqualTo("AT"));
            Assert.That(badges.Select(id => edit.Document.Find(id).Level), Is.All.EqualTo(17));
            foreach (var id in badges)
                Assert.That(edit.Document.Find(id).Bounds.Width / edit.Document.Find(id).Bounds.Height,
                    Is.EqualTo(EditorDocument.BadgeRatio).Within(1e-12));

            var decoration = new Layer
            {
                Kind = LayerKind.Shape, Name = "装饰", Bounds = new RectD(40, 40, 120, 60),
                ColorArgb = 0xFF33AA66
            };
            edit.AddLayer(decoration);
            edit.UpdateLayer(decoration.Id, layer => layer.Visible = false);
            edit.UpdateLayer(decoration.Id, layer => layer.Locked = true);
            Assert.That(edit.Document.Find(decoration.Id).Visible, Is.False);
            Assert.That(edit.Document.Find(decoration.Id).Locked, Is.True);

            var layoutIds = artworks.Select(artwork => artwork.Id).ToArray();
            edit.SetSelection(layoutIds);
            var captured = edit.CaptureLayoutTemplate("组合流程布局", layoutIds);
            var library = new TemplateLibrary(libraryFolder);
            var added = library.Add(captured);
            Assert.That(added.Added, Is.True);
            edit.UpdateLayer(layoutIds[0], layer =>
                layer.Bounds = layer.Bounds with { X = layer.Bounds.X + 220 });
            var savedState = edit.Document.Copy();
            var projectPath = Path.Combine(root, "组合流程.ppgproj");
            NativePackageStore.Save(projectPath, edit.Document, "r1-combined");
            var reopenedDocument = NativePackageStore.Read(projectPath);
            Assert.That(reopenedDocument.StateEquals(savedState), Is.True);
            var reopened = new EditSession(reopenedDocument);
            var reopenedLibrary = new TemplateLibrary(libraryFolder).Load();
            Assert.That(reopenedLibrary.Entries.Single().Name, Is.EqualTo("组合流程布局"));
            reopened.SetSelection(layoutIds);
            var beforeApply = reopened.Document.Copy();
            var beforeApplyPng = TestImages.RenderPng(reopened.Document);
            var layoutTemplate = new TemplateLibrary(libraryFolder).Read(reopenedLibrary.Entries.Single().Id);
            reopened.ApplyLayoutTemplate(layoutTemplate, layoutIds);
            Assert.That(reopened.UndoCount, Is.EqualTo(1));
            var appliedPng = TestImages.RenderPng(reopened.Document);
            Assert.That(appliedPng, Is.Not.EqualTo(beforeApplyPng));
            reopened.Undo();
            Assert.That(reopened.Document.StateEquals(beforeApply), Is.True);
            reopened.Redo();
            Assert.That(TestImages.RenderPng(reopened.Document), Is.EqualTo(appliedPng));
            Assert.That(reopened.Document.Find(decoration.Id).Visible, Is.False);
            Assert.That(reopened.Document.Find(decoration.Id).Locked, Is.True);

            var parent = Path.Combine(root, "output");
            Directory.CreateDirectory(parent);
            var queueBefore = reopened.Document.Copy();
            var tasks = new[]
            {
                new PngExportTask(1920, false), new PngExportTask(1920, true),
                new PngExportTask(1280, false), new PngExportTask(960, true)
            };
            var exportDirectory = PngExportQueue.CreateRunDirectory(parent, DateTime.Now);
            var queue = await PngExportQueue.ExecuteAsync(reopened.Document, tasks, exportDirectory,
                TestImages.ValidateImages,
                (document, transparent, width) => TestImages.RenderPng(document, transparent, width),
                (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
                sources, () => projectPath).ConfigureAwait(false);
            Assert.That(queue.Cancelled, Is.False);
            Assert.That(queue.Items.Select(item => item.Status),
                Is.All.EqualTo(PngExportTaskStatus.Completed));
            foreach (var task in tasks)
            {
                var path = Path.Combine(queue.Directory, task.FileName);
                Assert.That(File.Exists(path), Is.True);
                var expected = PngExportSize.Create(reopened.Document, task.Width);
                var (width, height, _) = TestImages.DecodePng(File.ReadAllBytes(path));
                Assert.That((width, height), Is.EqualTo((expected.Width, expected.Height)));
            }
            Assert.That(File.Exists(queue.ManifestPath), Is.True);
            Assert.That(reopened.Document.StateEquals(queueBefore), Is.True,
                "queue rendering does not mutate the reopened document");
        }
        finally
        {
            var expected = Path.GetFullPath(Path.GetTempPath()) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
