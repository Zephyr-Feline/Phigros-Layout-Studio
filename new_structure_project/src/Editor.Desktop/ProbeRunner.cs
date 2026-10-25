using System.Buffers.Binary;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Imaging.Windows;
using Editor.Rendering;

namespace Editor.Desktop;

internal static class ProbeRunner
{
    internal static string NativeProjectFileName => $"internal-native-v{NativePackageStore.SchemaVersion}.package";

    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "self-created-image.png");
        using (var bitmap = new RenderTargetBitmap(new PixelSize(160, 100), new Vector(96, 96)))
        {
            using (var context = bitmap.CreateDrawingContext())
            {
                context.FillRectangle(Brushes.DarkBlue, new Rect(0, 0, 160, 100));
                context.FillRectangle(Brushes.Gold, new Rect(15, 15, 70, 70));
                context.DrawEllipse(Brushes.Red, null, new Point(115, 50), 25, 25);
            }
            bitmap.Save(imagePath, new PngBitmapEncoderOptions());
            var jpegPath = Path.Combine(directory, "self-created-image.jpg");
            bitmap.Save(jpegPath, new JpegBitmapEncoderOptions());
            var formatDocument = new EditorDocument();
            var jpegId = Guid.NewGuid();
            formatDocument.AddAsset(jpegId, File.ReadAllBytes(jpegPath));
            formatDocument.Layers.Add(new Layer { Kind = LayerKind.Image, Name = "JPEG", AssetId = jpegId });
            var bmp = new byte[58];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), bmp.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), 54);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 24);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), 4);
            bmp[54] = 0x20; bmp[55] = 0x40; bmp[56] = 0x80;
            var bmpId = Guid.NewGuid();
            formatDocument.AddAsset(bmpId, bmp);
            formatDocument.Layers.Add(new Layer { Kind = LayerKind.Image, Name = "BMP", AssetId = bmpId,
                Bounds = new RectD(100, 100, 100, 100) });
            ImageAssetValidator.Validate(formatDocument, NativeImaging.Decoder);
            _ = PngExporter.Render(formatDocument, decoder: NativeImaging.Decoder);

            // TIFF (Windows imaging codec) must render exactly like the same pixels stored as PNG.
            var raster = SyntheticBgra(64, 32);
            var tiffDocument = ImageDocument(raster, tiff: true);
            var pngDocument = ImageDocument(raster, tiff: false);
            ImageAssetValidator.Validate(tiffDocument, NativeImaging.Decoder);
            if (!PngExporter.Render(tiffDocument, decoder: NativeImaging.Decoder)
                    .SequenceEqual(PngExporter.Render(pngDocument, decoder: NativeImaging.Decoder)))
                throw new InvalidDataException("TIFF rendering does not match the equivalent PNG.");
            File.Delete(jpegPath);
        }

        var document = new EditorDocument();
        var session = new EditSession(document);
        var assetId = Guid.NewGuid();
        var importedBytes = File.ReadAllBytes(imagePath);
        var image = new Layer { Kind = LayerKind.Image, Name = "Self-created image", AssetId = assetId,
            Bounds = new RectD(80, 80, 600, 375) };
        session.AddImageLayer(image, importedBytes);
        session.AddLayer(new Layer { Kind = LayerKind.Text, Name = "Chinese text", Text = "中文 English\n第二行",
            FontFamily = "Microsoft YaHei UI", FontSize = 72, ColorArgb = 0xFFFFFFFF,
            Bounds = new RectD(100, 500, 1000, 220) });
        var badge = EditorDocument.NewBadge(new PointD(900, 190));
        session.AddLayer(badge);
        session.BeginGesture(badge.Id);
        session.MoveBy(60, 20);
        session.CommitGesture();
        session.BeginGesture(badge.Id);
        session.ResizeBy(64, 0);
        session.CommitGesture();
        if (!session.Undo() || !session.Redo()) throw new InvalidOperationException("Undo/redo probe failed.");

        var projectPath = Path.Combine(directory, "临时 P1 项目.json");
        NativeDocumentStore.Save(projectPath, session.Document);
        var reopened = NativeDocumentStore.Read(projectPath);
        var png = PngExporter.Render(reopened);
        session.SelectedLayerId = badge.Id;
        if (!png.SequenceEqual(PngExporter.Render(session.Document)))
            throw new InvalidDataException("Session selection affected PNG output.");
        var exportPath = Path.Combine(directory, "导出 PNG.png");
        File.WriteAllBytes(exportPath, png);
        using var decoded = new Bitmap(exportPath);
        if (decoded.PixelSize != new PixelSize(reopened.CanvasWidth, reopened.CanvasHeight))
            throw new InvalidDataException("Export dimensions changed.");

        // The native package must remain renderable after its import source is removed.
        var nativePath = Path.Combine(directory, NativeProjectFileName);
        NativePackageStore.Save(nativePath, session.Document, "p2-probe");
        File.Delete(imagePath);
        var native = NativePackageStore.Read(nativePath);
        if (!png.SequenceEqual(PngExporter.Render(native)))
            throw new InvalidDataException("Native package changed the self-contained PNG output.");
        var opened = new EditSession(new EditorDocument());
        var workflow = new NativeProjectWorkflow(opened, NativeImaging.ValidateImages, "p2-probe");
        workflow.Open(nativePath);
        if (workflow.IsDirty || opened.UndoCount != 0)
            throw new InvalidDataException("Native open did not start a clean edit session.");
        opened.BeginGesture(image.Id);
        opened.MoveBy(12, 7);
        opened.CommitGesture();
        if (!workflow.IsDirty || !opened.Undo() || !opened.Redo())
            throw new InvalidDataException("Native edit/undo/redo workflow failed.");
        var editedPng = PngExporter.Render(opened.Document);
        workflow.Save();
        WriteRestartExports(nativePath, opened.Document);
        workflow.Open(nativePath);
        if (workflow.IsDirty || !editedPng.SequenceEqual(PngExporter.Render(opened.Document)))
            throw new InvalidDataException("Native save/reopen changed the edited output.");

        var broken = opened.Document.Copy();
        broken.Assets[assetId] = new AssetBlob(importedBytes[..24]); // Valid PNG header, truncated body.
        var brokenPath = Path.Combine(directory, "corrupt-image.package");
        NativePackageStore.Save(brokenPath, broken, "p2-probe");
        opened.UpdateLayer(image.Id, layer => layer.Name = "unsaved image");
        var undoBeforeBadOpen = opened.UndoCount;
        try { workflow.Open(brokenPath); throw new InvalidDataException("Corrupt image was accepted."); }
        catch (InvalidDataException error) when (error.Message.Contains("Image asset", StringComparison.Ordinal)) { }
        if (!workflow.IsDirty || opened.UndoCount != undoBeforeBadOpen ||
            opened.Document.Find(image.Id).Name != "unsaved image")
            throw new InvalidDataException("Failed native open damaged the active edit session.");

        // TIFF end-to-end: public extension, embedded asset, source file removed before reopen.
        var tiffSource = Path.Combine(directory, "source 曲绘.tiff");
        File.WriteAllBytes(tiffSource, WicTiffEncoder.EncodeBgra(64, 32, SyntheticBgra(64, 32)));
        var tiffSession = new EditSession(new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 });
        var tiffLayer = new Layer { Kind = LayerKind.Image, Name = "TIFF layer", AssetId = Guid.NewGuid(),
            Bounds = new RectD(20, 20, 200, 100) };
        tiffSession.AddImageLayer(tiffLayer, File.ReadAllBytes(tiffSource));
        var tiffWorkflow = new NativeProjectWorkflow(tiffSession, NativeImaging.ValidateImages, "p2-probe");
        var tiffProject = NativeProjectFile.EnsureExtension(Path.Combine(directory, "tiff-project"));
        tiffWorkflow.SaveAs(tiffProject);
        WriteRestartExports(tiffProject, tiffSession.Document);
        var expectedTiffPng = PngExporter.Render(tiffSession.Document, decoder: NativeImaging.Decoder);
        File.Delete(tiffSource);
        var reopenedTiff = new NativeProjectWorkflow(new EditSession(new EditorDocument()),
            NativeImaging.ValidateImages, "p2-probe");
        reopenedTiff.Open(tiffProject);
        if (!expectedTiffPng.SequenceEqual(
                PngExporter.Render(reopenedTiff.Session.Document, decoder: NativeImaging.Decoder)))
            throw new InvalidDataException("Reopened TIFF project changed the exported pixels.");

        Console.WriteLine($"PROBE PASS: {projectPath}");
        Console.WriteLine($"PNG: {exportPath} ({decoded.PixelSize.Width} x {decoded.PixelSize.Height})");
        Console.WriteLine($"NATIVE PACKAGE PASS: {nativePath} (source removed; edit/save/reopen and corrupt-image rejection)");
        Console.WriteLine($"PPGPROJ PASS: {tiffProject} (TIFF embedded; source removed; reopen renders identically)");
        Console.WriteLine("IMAGE FORMAT PASS: self-created PNG, JPEG, BMP and TIFF decoded and rendered");
        CompatibilityProbe.Run(Path.Combine(directory, "compat"), null);
        RunAutomaticBackgroundProbe(directory);
        RunSongCreationProbe(directory);
        RunCustomTemplateProbe(directory);
        RunArrangementProbe(directory);
        RunProjectLayerImportProbe(directory);
        RunSelectionProjectProbe(directory);
        RunSongBatchProbe(directory, 3);
        RunSongBatchProbe(directory, 5);
        RunSongStyleProbe(directory);
        PreviewCacheProbe.Run(directory);
        return 0;
    }

    private static void RunSongStyleProbe(string directory)
    {
        var sourceBytes = File.ReadAllBytes(Path.Combine(directory, "song-card.png"));
        var document = new EditorDocument { CanvasWidth = 1600, CanvasHeight = 900 };
        var asset = Guid.NewGuid();
        document.AddAsset(asset, sourceBytes);

        Guid AddGroup(string name, double x, int variant)
        {
            var group = Guid.NewGuid();
            var plate = new Layer
            {
                Kind = LayerKind.Shape, Name = name + " · plate", GroupId = group,
                SongRole = SongRole.Plate, Bounds = new RectD(x - 8, 270, 416, 105),
                Slanted = true, ColorArgb = variant == 0 ? 0xCC102A38u : 0xCC403020u
            };
            var artwork = new Layer
            {
                Kind = LayerKind.Image, Name = name + " · artwork", GroupId = group,
                SongRole = SongRole.Artwork, AssetId = asset,
                Bounds = new RectD(x, 70, variant == 0 ? 400 : 320,
                    variant == 0 ? 225 : 180), Slanted = true, KeepAspect = true
            };
            var title = new Layer
            {
                Kind = LayerKind.Text, Name = name + " · title", GroupId = group,
                SongRole = SongRole.Title, Text = "Style title " + name,
                Bounds = new RectD(x + 10, 300, 320, 40),
                FontFamily = variant == 0 ? "Arial" : "Microsoft YaHei UI",
                FontSize = variant == 0 ? 48 : 30
            };
            var artist = new Layer
            {
                Kind = LayerKind.Text, Name = name + " · artist", GroupId = group,
                SongRole = SongRole.Artist, Text = "Artist " + name,
                Bounds = new RectD(x + 10, 340, 320, 30),
                FontFamily = variant == 0 ? "Arial" : "Microsoft YaHei UI", FontSize = 22
            };
            var charter = new Layer
            {
                Kind = LayerKind.Text, Name = name + " · charter", GroupId = group,
                SongRole = SongRole.Charter, Text = "Charter " + name,
                Bounds = new RectD(x + 10, 370, 320, 28),
                FontFamily = variant == 0 ? "Arial" : "Microsoft YaHei UI", FontSize = 20
            };
            var badge = EditorDocument.NewBadge(new PointD(x + 280, 60));
            badge.Name = name + " · difficulty";
            badge.GroupId = group;
            badge.SongRole = SongRole.Badge;
            badge.Difficulty = variant == 0 ? "IN" : "HD";
            badge.Level = variant == 0 ? 15 : 12;
            document.Layers.AddRange([plate, artwork, title, artist, charter, badge]);
            return group;
        }

        var groupA = AddGroup("A", 60, 0);
        var groupB = AddGroup("B", 760, 1);
        document.Validate();
        var session = new EditSession(document);
        var style = SongStyles.Capture(session.Document, groupA, "便携歌曲样式");
        var libraryPath = Path.Combine(directory, "song styles 中文");
        var library = new SongStyleLibrary(libraryPath);
        var added = library.Add(style);
        if (!added.Added) throw new InvalidDataException("Song style was not added to the portable library.");
        var reloadedLibrary = new SongStyleLibrary(libraryPath).Load();
        if (reloadedLibrary.Diagnostics.Count != 0 || reloadedLibrary.Entries.Count != 1)
            throw new InvalidDataException("Song style library did not survive a fresh load.");
        var reloaded = library.Read(added.Entry.Id);
        session.SetSelection(session.Document.Layers.Where(layer => layer.GroupId == groupB)
            .Select(layer => layer.Id));
        session.ApplySongStyle(reloaded, session.SelectedLayerIds);
        if (session.UndoCount != 1 || !session.Undo() || !session.Redo())
            throw new InvalidDataException("Song style apply/history failed.");
        var sourceTitle = session.Document.Layers.Single(layer =>
            layer.GroupId == groupB && layer.SongRole == SongRole.Title);
        var created = session.InstantiateSongStyle(reloaded, sourceTitle.Id);
        if (created.Count != 6 || session.UndoCount != 2 ||
            !session.Document.Find(created[0]).GroupId.HasValue)
            throw new InvalidDataException("Song style instance did not create a complete new group.");
        var project = Path.Combine(directory, "song-style.ppgproj");
        NativePackageStore.Save(project, session.Document, "r3b-probe");
        WriteRestartExports(project, session.Document);
        var reopened = NativePackageStore.Read(project);
        if (reopened.Layers.Count(layer => layer.SongRole != SongRole.None) != 18)
            throw new InvalidDataException("Persisted song roles were lost after save/reopen.");
        Console.WriteLine($"SONG STYLE PASS: {project} (roles, independent library, apply, new IDs, undo/redo, PNG)");
    }

    private static void RunSongBatchProbe(string directory, int count)
    {
        var paths = new List<string>();
        uint[] colors = [0xFF149FAD, 0xFF852352, 0xFF263AB0, 0xFFDB7624, 0xFF226238];
        for (var i = 0; i < count; i++)
        {
            using var art = new RenderTargetBitmap(new PixelSize(160, 100));
            using (var context = art.CreateDrawingContext())
            {
                context.FillRectangle(new SolidColorBrush(Color.FromUInt32(colors[i])), new Rect(0, 0, 160, 100));
                context.DrawEllipse(Brushes.Gold, null, new Point(80, 50), 24 + i * 3, 24 + i * 3);
            }
            var path = Path.Combine(directory, $"batch{count} 曲目 {i + 1}.png");
            art.Save(path, new PngBitmapEncoderOptions()); paths.Add(path);
        }
        var session = new EditSession(new EditorDocument { CanvasWidth = count == 3 ? 1920 : 1440,
            CanvasHeight = 1080, BackgroundBlend = new(true) });
        var before = session.Document.Copy(); var protectedPaths = new ProtectedSourcePaths();
        var importer = new SongBatchWorkflow(session,
            path => Task.FromResult(new AssetBlob(File.ReadAllBytes(path))), NativeImaging.ValidateImages, protectedPaths);
        if (importer.ImportAsync(() => Task.FromResult<IReadOnlyList<string>>(paths)).GetAwaiter().GetResult() != count)
            throw new InvalidDataException("Batch song count mismatch.");
        var after = session.Document.Copy();
        if (session.UndoCount != 1 || after.Layers.Count != count * 6 || protectedPaths.Count != count ||
            !session.Undo() || !session.Document.StateEquals(before) || !session.Redo() || !session.Document.StateEquals(after))
            throw new InvalidDataException("Batch song import/history failed.");
        var project = Path.Combine(directory, $"batch-{count}-songs.ppgproj");
        NativePackageStore.Save(project, after, "song-batch-probe");
        WriteRestartExports(project, after);
        foreach (var path in paths) File.Delete(path); // Generated fixtures only; native assets are embedded.
        ProjectReopenProbe.Verify(project, project + ".opaque.png", project + ".transparent.png");
        Console.WriteLine($"SONG BATCH PASS: {project} ({count} groups, one undo, source images removed)");
    }

    private static void RunSelectionProjectProbe(string directory)
    {
        var source = Path.Combine(directory, "imported-song-groups.ppgproj");
        var bytes = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var session = new EditSession(new EditorDocument());
        var native = new NativeProjectWorkflow(session, NativeImaging.ValidateImages, "selection-probe");
        native.Open(source);
        var selected = session.Document.Layers[8];
        session.SelectedLayerId = selected.Id;
        session.UpdateLayer(selected.Id, layer => layer.Name = "Reusable middle song");
        var before = session.Document.Copy();
        var expected = before.Copy();
        expected.Layers.RemoveAll(layer => layer.GroupId != selected.GroupId);
        expected.FixedBackgroundAssetId = null; expected.BackgroundBlend = new();
        expected.ExtensionData.Clear(); expected.RemoveUnreferencedAssets();
        var path = Path.Combine(directory, "selected-song.ppgproj");
        var workflow = new SelectionProjectWorkflow(session, NativeImaging.ValidateImages,
            new ProtectedSourcePaths(), () => native.CurrentPath, "selection-probe");
        if (!workflow.SaveAsync(true, () => Task.FromResult<string?>(path)).GetAwaiter().GetResult() ||
            !session.Document.StateEquals(before) || !session.IsDirty || session.UndoCount != 1 ||
            native.CurrentPath != source || !bytes.SequenceEqual(File.ReadAllBytes(source)) ||
            time != File.GetLastWriteTimeUtc(source))
            throw new InvalidDataException("Saving selection changed the original project or edit state.");
        var saved = NativePackageStore.Read(path);
        if (saved.Layers.Count != 8 || saved.Assets.Count != 1 || !saved.StateEquals(expected))
            throw new InvalidDataException("Selected song package contains missing or unrelated content.");
        WriteRestartExports(path, expected);
        var target = new EditSession(new EditorDocument { CanvasWidth = expected.CanvasWidth, CanvasHeight = expected.CanvasHeight });
        new ProjectLayerImportWorkflow(target, NativeImaging.ValidateImages, new ProtectedSourcePaths(), () => null)
            .ImportAsync(() => Task.FromResult<string?>(path)).GetAwaiter().GetResult();
        if (!PngExporter.Render(target.Document, true, NativeImaging.Decoder).SequenceEqual(
            PngExporter.Render(expected, true, NativeImaging.Decoder)))
            throw new InvalidDataException("Reimporting selected song changed its foreground pixels.");
        Console.WriteLine($"SELECTION PROJECT PASS: {path} (one song from three, source/savepoint preserved, reimport pixels match)");
    }

    private static void RunProjectLayerImportProbe(string directory)
    {
        var sourcePath = Path.Combine(directory, "reusable song source.ppgproj");
        File.Copy(Path.Combine(directory, "song-card.ppgproj"), sourcePath, overwrite: true);
        var original = File.ReadAllBytes(sourcePath);
        var time = File.GetLastWriteTimeUtc(sourcePath);
        var session = new EditSession(new EditorDocument { CanvasWidth = 1920, CanvasHeight = 1080,
            BackgroundBlend = new(true) });
        var sources = new ProtectedSourcePaths();
        var importer = new ProjectLayerImportWorkflow(session, NativeImaging.ValidateImages, sources, () => null);
        var groups = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var before = session.Document.Copy();
            var count = importer.ImportAsync(() => Task.FromResult<string?>(sourcePath)).GetAwaiter().GetResult();
            if (count != 8) throw new InvalidDataException("Incomplete song-card import.");
            groups.Add(session.SelectedLayerIds[0]);
            var after = session.Document.Copy();
            if (!session.Undo() || !session.Document.StateEquals(before) ||
                !session.Redo() || !session.Document.StateEquals(after))
                throw new InvalidDataException("Project layer import history failed.");
        }
        if (session.UndoCount != 3 || session.Document.Assets.Count != 1 ||
            session.Document.Layers.Select(layer => layer.GroupId).Distinct().Count() != 3 ||
            !sources.Contains(sourcePath) || !original.SequenceEqual(File.ReadAllBytes(sourcePath)) ||
            time != File.GetLastWriteTimeUtc(sourcePath))
            throw new InvalidDataException("Import changed source, duplicated assets or merged song groups.");
        session.ApplyLayoutTemplate("three-columns", groups);
        var path = Path.Combine(directory, "imported-song-groups.ppgproj");
        NativePackageStore.Save(path, session.Document, "project-layer-import-probe");
        WriteRestartExports(path, session.Document);
        File.Delete(sourcePath); // This synthetic copy is no longer needed by the saved composition.
        ProjectReopenProbe.Verify(path, path + ".opaque.png", path + ".transparent.png");
        Console.WriteLine($"PROJECT LAYER IMPORT PASS: {path} (three independent songs, shared asset, template, undo/redo, source removed)");
    }

    private static void RunArrangementProbe(string directory)
    {
        var document = new EditorDocument { CanvasWidth = 1440, CanvasHeight = 1080 };
        var selected = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var group = Guid.NewGuid();
            var x = 80 + i * 340;
            var y = 90 + i * 70;
            var plate = new Layer { Kind = LayerKind.Shape, Name = $"Song {i + 1}", GroupId = group,
                Bounds = new RectD(x, y, 270, 230), Slanted = true,
                ColorArgb = new uint[] { 0xFF187F67, 0xFF286ED0, 0xFFCF1313 }[i] };
            var badge = EditorDocument.NewBadge(new PointD(x + 110, y + 200));
            badge.GroupId = group;
            badge.Level = 12 + i;
            document.Layers.AddRange([plate, badge]);
            selected.Add(plate.Id);
        }
        var session = new EditSession(document);
        session.SetSelection(selected.AsEnumerable().Reverse());
        session.ArrangeSelection(ArrangeAction.CenterVertical, ArrangeReference.Canvas);
        var before = session.Document.Copy();
        session.ArrangeSelection(ArrangeAction.HorizontalGaps, ArrangeReference.Canvas);
        var arranged = session.Document.Copy();
        if (session.UndoCount != 2 || !session.Undo() || !session.Document.StateEquals(before) ||
            !session.Redo() || !session.Document.StateEquals(arranged))
            throw new InvalidDataException("Group arrangement history failed.");
        var boxes = selected.Select(id => session.Document.Find(id).Bounds).ToArray();
        if (Math.Abs(boxes[0].X) > 1e-8 || Math.Abs(boxes[2].X + boxes[2].Width - 1440) > 1e-8 ||
            Math.Abs((boxes[1].X - boxes[0].X) - (boxes[2].X - boxes[1].X)) > 1e-8)
            throw new InvalidDataException("Group arrangement geometry failed.");
        var project = Path.Combine(directory, "arranged-groups.ppgproj");
        NativePackageStore.Save(project, arranged, "arrangement-probe");
        WriteRestartExports(project, arranged);
        Console.WriteLine($"ARRANGEMENT PASS: {project} (group alignment/equal gaps, undo/redo and PNG)");
    }

    private static void RunCustomTemplateProbe(string directory)
    {
        var session = new EditSession(new EditorDocument { CanvasWidth = 1024, CanvasHeight = 512,
            Layers = Enumerable.Range(0, 3).Select(index => new Layer
            {
                Kind = LayerKind.Shape, Name = $"Template slot {index + 1}",
                Bounds = new RectD(64 + index * 320, 128, 256, 256),
                ColorArgb = new uint[] { 0xFFCF1313, 0xFF2060CC, 0xFF208040 }[index]
            }).ToList() });
        var ids = session.Document.Layers.Select(layer => layer.Id).ToArray();
        var expected = PngExporter.Render(session.Document);
        var template = session.CaptureLayoutTemplate("Reusable three slots", ids);
        var path = Path.Combine(directory, "自制三列.ppglayout");
        LayoutTemplateStore.Save(path, template);
        session.UpdateDocument(document =>
        {
            foreach (var layer in document.Layers) layer.Bounds = layer.Bounds with { X = 0, Y = 0 };
        });
        session.ApplyLayoutTemplate(LayoutTemplateStore.Read(path), ids);
        if (!expected.SequenceEqual(PngExporter.Render(session.Document)) || !session.Undo() || !session.Redo())
            throw new InvalidDataException("Custom template read/apply/export/history failed.");
        var project = Path.Combine(directory, "custom-layout.ppgproj");
        NativePackageStore.Save(project, session.Document, "template-probe");
        WriteRestartExports(project, session.Document);
        Console.WriteLine($"CUSTOM TEMPLATE PASS: {path} (capture/read/apply, one-edit history and PNG)");
    }

    private static void RunSongCreationProbe(string directory)
    {
        using var artwork = new RenderTargetBitmap(new PixelSize(520, 250), new Vector(96, 96));
        using (var context = artwork.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.MidnightBlue, new Rect(0, 0, 520, 250));
            context.FillRectangle(Brushes.DodgerBlue, new Rect(130, 0, 260, 250));
            context.DrawEllipse(Brushes.Gold, null, new Point(260, 125), 72, 72);
        }
        using var stream = new MemoryStream();
        artwork.Save(stream, new PngBitmapEncoderOptions());
        var id = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = id,
            Name = "Self-created song artwork", Bounds = new RectD(220, 50, 520, 250) };
        var document = new EditorDocument { CanvasWidth = 960, CanvasHeight = 540,
            BackgroundBlend = new(true), Layers = [image] };
        document.AddAsset(id, stream.ToArray());
        var session = new EditSession(document);
        session.CreateSongCard(image.Id);
        session.AddOriginalTag(image.Id);
        if (session.UndoCount != 2 || !session.Undo() || !session.Redo())
            throw new InvalidDataException("Song creation undo/redo failed.");
        var png = PngExporter.Render(session.Document, decoder: NativeImaging.Decoder);
        var nativePath = Path.Combine(directory, "song-card.ppgproj");
        NativePackageStore.Save(nativePath, session.Document, "p4-probe");
        WriteRestartExports(nativePath, session.Document);
        if (!png.SequenceEqual(PngExporter.Render(NativePackageStore.Read(nativePath),
                decoder: NativeImaging.Decoder)))
            throw new InvalidDataException("Song card changed after save/reopen.");
        File.WriteAllBytes(Path.Combine(directory, "song-card.png"), png);
        Console.WriteLine($"SONG CREATION PASS: {nativePath} (group, Original label, undo/redo, save/reopen)");
    }

    private static void RunAutomaticBackgroundProbe(string directory)
    {
        var document = new EditorDocument { CanvasWidth = 960, CanvasHeight = 540,
            BackgroundBlend = new(true, 10, 0.5) };
        IBrush[] colors = [Brushes.Crimson, Brushes.DodgerBlue, Brushes.Goldenrod];
        for (var index = 0; index < colors.Length; index++)
        {
            using var art = new RenderTargetBitmap(new PixelSize(220, 160), new Vector(96, 96));
            using (var context = art.CreateDrawingContext())
            {
                context.FillRectangle(colors[index], new Rect(0, 0, 220, 160));
                context.DrawEllipse(Brushes.White, null, new Point(150, 55), 35, 35);
                context.FillRectangle(Brushes.DarkSlateGray, new Rect(20, 110, 180, 30));
            }
            using var stream = new MemoryStream();
            art.Save(stream, new PngBitmapEncoderOptions());
            var id = Guid.NewGuid();
            document.AddAsset(id, stream.ToArray());
            document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = id,
                Name = $"Artwork {index + 1}", Slanted = true,
                Bounds = new RectD(50 + index * 300, 100, 220, 160) });
            document.Layers.Add(new Layer { Kind = LayerKind.Text, Text = $"Track {index + 1}",
                Bounds = new RectD(50 + index * 300, 275, 220, 60), FontSize = 36 });
            document.Layers.Add(EditorDocument.NewBadge(new PointD(50 + index * 300, 350), 90));
        }
        var session = new EditSession(document);
        var artworkIds = document.Layers.Where(layer => layer.Kind == LayerKind.Image)
            .Select(layer => layer.Id).ToArray();
        session.UpdateDocument(current =>
        {
            current.BackgroundBlend = current.BackgroundBlend with
            {
                ContributionMode = BackgroundContributionMode.Selected,
                SelectedArtworkLayerIds = [artworkIds[0], artworkIds[^1]],
                Range = 0.75,
                Transition = 0.8
            };
        });
        var expected = PngExporter.Render(session.Document);
        var nativePath = Path.Combine(directory, "automatic-background.ppgproj");
        NativePackageStore.Save(nativePath, session.Document, "p4-probe");
        WriteRestartExports(nativePath, session.Document);
        if (!expected.SequenceEqual(PngExporter.Render(NativePackageStore.Read(nativePath))))
            throw new InvalidDataException("Automatic background changed after save/reopen.");
        session.UpdateLayer(document.Layers[3].Id, layer => layer.Visible = false);
        if (expected.SequenceEqual(PngExporter.Render(session.Document)))
            throw new InvalidDataException("Hidden artwork did not change the automatic composition.");
        session.Undo();
        if (!expected.SequenceEqual(PngExporter.Render(session.Document)) || session.Document.Assets.Count != 3)
            throw new InvalidDataException("Automatic background undo changed document resources or pixels.");
        File.WriteAllBytes(Path.Combine(directory, "automatic-background.png"), expected);
        Console.WriteLine($"AUTOMATIC BACKGROUND PASS: {nativePath} (save/reopen, hide, undo, no derived assets)");
    }

    private static void WriteRestartExports(string projectPath, EditorDocument document)
    {
        // Capture the live document before reopening; a fresh process must reproduce both exports.
        File.WriteAllBytes(projectPath + ".opaque.png",
            PngExporter.Render(document, decoder: NativeImaging.Decoder));
        File.WriteAllBytes(projectPath + ".transparent.png",
            PngExporter.Render(document, true, NativeImaging.Decoder));
        File.WriteAllBytes(projectPath + ".width1920.opaque.png",
            PngExporter.Render(document, false, NativeImaging.Decoder, 1920));
        File.WriteAllBytes(projectPath + ".width1920.transparent.png",
            PngExporter.Render(document, true, NativeImaging.Decoder, 1920));
    }

    private static EditorDocument ImageDocument(byte[] raster, bool tiff)
    {
        var document = new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 };
        var assetId = Guid.NewGuid();
        document.AddAsset(assetId, tiff
            ? WicTiffEncoder.EncodeBgra(64, 32, raster)
            : EncodePng(64, 32, raster));
        document.Layers.Add(new Layer { Kind = LayerKind.Image, Name = tiff ? "TIFF" : "PNG",
            AssetId = assetId, Bounds = new RectD(20, 20, 64, 32) }); // 1:1, so the comparison is exact.
        return document;
    }

    private static byte[] SyntheticBgra(int width, int height)
        => ProbeImages.SyntheticBgra(width, height);

    private static byte[] EncodePng(int width, int height, byte[] bgra)
        => ProbeImages.EncodePng(width, height, bgra);

    public static int RunGates(string directory)
    {
        Directory.CreateDirectory(directory);
        var pixel = new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 };
        pixel.Layers.Add(new Layer { Kind = LayerKind.Shape, Name = "clipped red",
            Bounds = new RectD(-20, 10, 80, 80), ColorArgb = 0xFFFF0000 });
        pixel.Layers.Add(new Layer { Kind = LayerKind.Shape, Name = "hidden blue", Visible = false,
            Bounds = new RectD(0, 0, 100, 100), ColorArgb = 0xFF0000FF });
        pixel.Layers.Add(new Layer { Kind = LayerKind.Shape, Name = "green over red",
            Bounds = new RectD(20, 20, 40, 40), ColorArgb = 0xFF00FF00 });
        File.WriteAllBytes(Path.Combine(directory, "alpha-clip-order.png"),
            PngExporter.Render(pixel, transparentBackground: true));

        var text = new EditorDocument { CanvasWidth = 1280, CanvasHeight = 1000,
            BackgroundArgb = 0xFF202830 };
        var samples = new[]
        {
            "中文：输入法组合与提交", "English: long line with wrapping to the next line and another word",
            "中 English 混排 123", "第一行\n\n第三行（中间空行）",
            "Bold Italic 字体", "左对齐 Left", "Center 中心", "e\u0301 + 音乐 ♫ + 😀"
        };
        var rowY = new[] { 15, 115, 230, 320, 520, 620, 720, 820 };
        for (var i = 0; i < samples.Length; i++)
            text.Layers.Add(new Layer
            {
                Kind = LayerKind.Text, Name = $"sample {i}", Text = samples[i],
                Bounds = new RectD(25, rowY[i], 900, i == 3 ? 180 : 100), FontSize = 32,
                FontFamily = i == 7 ? "P1 Missing Font 123" : "Microsoft YaHei UI",
                Bold = i == 4, Italic = i == 4,
                Alignment = i == 6 ? Editor.Core.HorizontalAlignment.Center : Editor.Core.HorizontalAlignment.Left,
                LineSpacing = i == 3 ? 1.4 : 1
            });
        File.WriteAllBytes(Path.Combine(directory, "text-matrix.png"), PngExporter.Render(text));

        using var source = new RenderTargetBitmap(new PixelSize(900, 300), new Vector(96, 96));
        using (var context = source.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.Red, new Rect(0, 0, 300, 300));
            context.FillRectangle(Brushes.Blue, new Rect(300, 0, 300, 300));
            context.FillRectangle(Brushes.Yellow, new Rect(600, 0, 300, 300));
        }
        using var composite = new RenderTargetBitmap(new PixelSize(900, 300), new Vector(96, 96));
        using (var context = composite.CreateDrawingContext())
        using (context.PushEffect(new BlurEffect { Radius = 35 }, new Rect(0, 0, 900, 300)))
            context.DrawImage(source, new Rect(0, 0, 900, 300), new Rect(0, 0, 900, 300));
        composite.Save(Path.Combine(directory, "three-color-unified-blur.png"), new PngBitmapEncoderOptions());

        using var hiddenSource = new RenderTargetBitmap(new PixelSize(900, 300), new Vector(96, 96));
        using (var context = hiddenSource.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.Black, new Rect(0, 0, 900, 300));
            context.FillRectangle(Brushes.Red, new Rect(0, 0, 300, 300));
            // The middle blue source is hidden and deliberately contributes no pixels.
            context.FillRectangle(Brushes.Yellow, new Rect(600, 0, 300, 300));
        }
        using var hiddenComposite = new RenderTargetBitmap(new PixelSize(900, 300), new Vector(96, 96));
        using (var context = hiddenComposite.CreateDrawingContext())
        using (context.PushEffect(new BlurEffect { Radius = 35 }, new Rect(0, 0, 900, 300)))
            context.DrawImage(hiddenSource, new Rect(0, 0, 900, 300), new Rect(0, 0, 900, 300));
        hiddenComposite.Save(Path.Combine(directory, "hidden-middle-unified-blur.png"),
            new PngBitmapEncoderOptions());

        var fixedDoc = new EditorDocument { CanvasWidth = 900, CanvasHeight = 300 };
        var fixedAsset = Guid.NewGuid();
        using (var bitmap = new RenderTargetBitmap(new PixelSize(4, 4), new Vector(96, 96)))
        {
            using (var context = bitmap.CreateDrawingContext())
                context.FillRectangle(Brushes.Purple, new Rect(0, 0, 4, 4));
            using var stream = new MemoryStream();
            bitmap.Save(stream, new PngBitmapEncoderOptions());
            fixedDoc.AddAsset(fixedAsset, stream.ToArray());
        }
        fixedDoc.FixedBackgroundAssetId = fixedAsset;
        File.WriteAllBytes(Path.Combine(directory, "fixed-background.png"), PngExporter.Render(fixedDoc));

        Console.WriteLine($"GATE PROBES WRITTEN: {directory}");
        return 0;
    }
}
