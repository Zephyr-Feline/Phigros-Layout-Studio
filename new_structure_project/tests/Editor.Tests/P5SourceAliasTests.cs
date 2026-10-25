using System.Diagnostics;
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

/// <summary>Real Windows directory junctions; no privileged symlinks or downloaded tools.</summary>
public sealed class P5SourceAliasTests
{
    private string _root = "";
    private string _real = "";
    private string _alias = "";
    private readonly List<string> _junctions = [];

    [SetUp]
    public void SetUp()
    {
        _root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "source-alias", Guid.NewGuid().ToString("N")));
        _real = Path.Combine(_root, "真实目录");
        _alias = Path.Combine(_root, "联接 别名");
        Directory.CreateDirectory(_real);
        Junction(_alias, _real);
    }

    [TearDown]
    public void TearDown()
    {
        // Remove only the link entries before any recursive cleanup of this isolated fixture.
        foreach (var link in _junctions.AsEnumerable().Reverse())
            if (Directory.Exists(link) || new DirectoryInfo(link).LinkTarget is not null)
                Directory.Delete(link, recursive: false);
        _junctions.Clear();
        var expected = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.WorkDirectory, "source-alias"))
            + Path.DirectorySeparatorChar;
        Assert.That(_root.StartsWith(expected, StringComparison.OrdinalIgnoreCase), Is.True);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ImportedSourceIsProtectedThroughEitherDirectoryName(bool importAlias)
    {
        var source = Path.Combine(_real, "原图.png"); File.WriteAllBytes(source, Png());
        var alias = Path.Combine(_alias, "原图.png");
        var before = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var paths = new ProtectedSourcePaths(); paths.Add(importAlias ? alias : source);
        Assert.That(paths.Contains(importAlias ? source : alias), Is.True);
        Assert.Throws<InvalidOperationException>(() => paths.EnsureNotProtected(importAlias ? source : alias));
        Assert.That(paths.Count, Is.EqualTo(1));
        AssertFile(source, before, time);
    }

    [Test]
    public async Task SaveAsCannotReplaceAnImportedImageThroughAJunction()
    {
        var source = Path.Combine(_real, "原图.png"); File.WriteAllBytes(source, Png());
        var alias = Path.Combine(_alias, "原图.png");
        var paths = new ProtectedSourcePaths(); paths.Add(source);
        var native = Native(); var before = native.Session.Document.Copy();
        var history = (native.Session.UndoCount, native.Session.RedoCount, native.IsDirty);
        var bytes = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        Assert.ThrowsAsync<InvalidOperationException>(() => native.SaveAsAsync(() => Task.FromResult<string?>(alias), paths));
        Assert.That(native.CurrentPath, Is.Null);
        AssertState(native.Session, before, history);
        AssertFile(source, bytes, time);
        Assert.That(await native.SaveAsAsync(() => Task.FromResult<string?>(Path.Combine(_alias, "新工程.ppgproj")), paths), Is.True);
        Assert.That(NativePackageStore.Read(native.CurrentPath!).StateEquals(before), Is.True);
        AssertFile(source, bytes, time);
    }

    [TestCase("png", false)]
    [TestCase("png", true)]
    [TestCase("temporary", false)]
    [TestCase("temporary", true)]
    [TestCase("selection", false)]
    [TestCase("selection", true)]
    public void OtherOutputsCannotReplaceTheActiveNativeProjectThroughAJunction(string output, bool dirty)
    {
        var native = Native(); var project = Path.Combine(_real, "当前工程.ppgproj"); native.SaveAs(project);
        if (dirty) native.Session.UpdateLayer(native.Session.SelectedLayerId!.Value, layer => layer.Text = "unsaved");
        else { native.Session.UpdateLayer(native.Session.SelectedLayerId!.Value, layer => layer.Text = "redo"); native.Session.Undo(); }
        var alias = Path.Combine(_alias, "当前工程.ppgproj");
        var before = native.Session.Document.Copy(); var selected = native.Session.SelectedLayerIds.ToArray();
        var history = (native.Session.UndoCount, native.Session.RedoCount, native.IsDirty);
        var bytes = File.ReadAllBytes(project); var time = File.GetLastWriteTimeUtc(project);
        var paths = new ProtectedSourcePaths(); var renders = 0; var writes = 0;
        var export = new PngExportWorkflow(native.Session, TestImages.ValidateImages,
            (_, _, _) => { renders++; return Png(); },
            (path, png) => { writes++; AtomicFile.Write(path, stream => stream.Write(png)); }, paths, () => native.CurrentPath);
        if (output == "png") Assert.Throws<InvalidOperationException>(() => export.Export(alias));
        else if (output == "temporary") Assert.ThrowsAsync<InvalidOperationException>(() =>
            native.SaveTemporaryAsync(() => Task.FromResult<string?>(alias), paths));
        else Assert.ThrowsAsync<InvalidOperationException>(() => new SelectionProjectWorkflow(native.Session,
            TestImages.ValidateImages, paths, () => native.CurrentPath, "source-alias-test")
            .SaveAsync(false, () => Task.FromResult<string?>(alias)));
        Assert.That((renders, writes), Is.EqualTo((0, 0)));
        Assert.That(native.CurrentPath, Is.EqualTo(project));
        Assert.That(native.Session.SelectedLayerIds, Is.EqualTo(selected));
        AssertState(native.Session, before, history);
        AssertFile(project, bytes, time);
        Assert.That(Directory.GetFiles(_real, "*.tmp"), Is.Empty);
    }

    [Test]
    public void CrossProjectImportCannotDuplicateTheCurrentProjectThroughAJunction()
    {
        var native = Native(); var project = Path.Combine(_real, "当前工程.ppgproj"); native.SaveAs(project);
        var before = native.Session.Document.Copy(); var history = (native.Session.UndoCount, native.Session.RedoCount, native.IsDirty);
        var paths = new ProtectedSourcePaths();
        Assert.ThrowsAsync<InvalidOperationException>(() => new ProjectLayerImportWorkflow(native.Session,
            TestImages.ValidateImages, paths, () => native.CurrentPath)
            .ImportAsync(() => Task.FromResult<string?>(Path.Combine(_alias, "当前工程.ppgproj"))));
        AssertState(native.Session, before, history);
        Assert.That(paths.Count, Is.Zero);
    }

    [TestCase(ImageImportAction.Add)]
    [TestCase(ImageImportAction.Replace)]
    [TestCase(ImageImportAction.Background)]
    public async Task ImageImportThroughAJunctionProtectsTheRealSource(ImageImportAction action)
    {
        var source = Path.Combine(_real, "导入图.png"); File.WriteAllBytes(source, Png());
        var session = Native().Session;
        var image = new Layer { Kind = LayerKind.Image, AssetId = Guid.NewGuid() };
        session.AddImageLayer(image, Png()); session.SetSelection([image.Id]);
        var paths = new ProtectedSourcePaths();
        var workflow = new ImageImportWorkflow(session, path => Task.FromResult(
            new LoadedImage(new AssetBlob(File.ReadAllBytes(path)), 4, 4)), paths, () => null);
        Assert.That(await workflow.ImportAsync(action, () => Task.FromResult<string?>(Path.Combine(_alias, "导入图.png"))), Is.True);
        Assert.That(paths.Contains(source), Is.True);
        var export = new PngExportWorkflow(session, TestImages.ValidateImages, (_, _, _) => Png(),
            NativeDocumentStore.WriteBytesAtomically, paths, () => null);
        Assert.Throws<InvalidOperationException>(() => export.Export(source));
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(Png()));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NestedJunctionsResolveEvenWhenTheDestinationFileIsNotCreatedYet(bool existing)
    {
        var folder = Path.Combine(_real, "内部"); Directory.CreateDirectory(folder);
        var nested = Path.Combine(_root, "第二层 别名"); Junction(nested, Path.Combine(_alias, "内部"));
        var target = Path.Combine(folder, "same.png");
        if (existing) File.WriteAllBytes(target, Png());
        var alias = Path.Combine(nested, "same.png");
        Assert.That(FilePathIdentity.AreSame(target, alias), Is.True);
        var sources = new ProtectedSourcePaths(); sources.Add(alias);
        Assert.That(sources.Contains(target), Is.True);
        Assert.That(FilePathIdentity.AreSame(target, Path.Combine(folder, "other.png")), Is.False);
    }

    [Test]
    public void DifferentFilesWithIdenticalBytesAreStillLegalOutputPaths()
    {
        var source = Path.Combine(_real, "source.png"); var other = Path.Combine(_real, "other.png");
        File.WriteAllBytes(source, Png()); File.WriteAllBytes(other, Png());
        var paths = new ProtectedSourcePaths(); paths.Add(source);
        Assert.That(FilePathIdentity.AreSame(source, other), Is.False);
        Assert.DoesNotThrow(() => paths.EnsureNotProtected(Path.Combine(_alias, "other.png")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ImportTimeTargetsStayProtectedAfterTheChosenJunctionIsRemovedOrRetargeted(bool retarget)
    {
        var name = "source.png"; var original = Path.Combine(_real, name); File.WriteAllBytes(original, Png());
        var paths = new ProtectedSourcePaths(); paths.Add(Path.Combine(_alias, name));
        RemoveJunction(_alias);
        var other = Path.Combine(_root, "另一目录"); Directory.CreateDirectory(other);
        if (retarget) Junction(_alias, other);
        Assert.That(paths.Contains(original), Is.True);
        Assert.Throws<InvalidOperationException>(() => paths.EnsureNotProtected(original));
        if (retarget)
        {
            paths.Add(Path.Combine(_alias, name));
            Assert.That(paths.Contains(Path.Combine(other, name)), Is.True);
            Assert.That(paths.Contains(original), Is.True, "reimporting the same chosen path must retain its earlier target");
        }
        Assert.That(paths.Count, Is.EqualTo(1));
        paths.Clear();
        Assert.That(paths.Contains(original), Is.False); Assert.That(paths.Count, Is.Zero);
    }

    [Test]
    public void WindowsExtendedAndOrdinaryPathsMatch()
    {
        var source = Path.Combine(_real, "source.png"); File.WriteAllBytes(source, Png());
        var paths = new ProtectedSourcePaths(); paths.Add(@"\\?\" + source);
        Assert.That(FilePathIdentity.AreSame(source, @"\\?\" + Path.Combine(_alias, "source.png")), Is.True);
        Assert.That(paths.Contains(Path.Combine(_alias, "source.png")), Is.True);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void BlankPathsAreRejectedWithoutRegisteringAnything(string path)
    {
        var paths = new ProtectedSourcePaths();
        Assert.Throws<ArgumentException>(() => paths.PrepareAdd(path));
        Assert.Throws<ArgumentException>(() => FilePathIdentity.AreSame(path, _real));
        Assert.That(paths.Count, Is.Zero);
    }

    [Test]
    public void CyclicJunctionIsRejectedWithoutChangingTheSourceRegistry()
    {
        var cycle = Path.Combine(_root, "循环"); Junction(cycle, cycle);
        var paths = new ProtectedSourcePaths(); var source = Path.Combine(_real, "source.png"); paths.Add(source);
        Assert.Throws<IOException>(() => paths.PrepareAdd(Path.Combine(cycle, "image.png")));
        Assert.Throws<IOException>(() => paths.EnsureNotProtected(Path.Combine(cycle, "image.png")));
        Assert.That(paths.Count, Is.EqualTo(1)); Assert.That(paths.Contains(source), Is.True);
    }

    [Test]
    public void PreparingSourceProtectionIsReadOnlyUntilTheImportCommits()
    {
        var source = Path.Combine(_real, "source.png"); File.WriteAllBytes(source, Png());
        var paths = new ProtectedSourcePaths(); var register = paths.PrepareAdd(Path.Combine(_alias, "source.png"));
        Assert.That(paths.Count, Is.Zero); Assert.That(paths.Contains(source), Is.False);
        register(); register();
        Assert.That(paths.Count, Is.EqualTo(1)); Assert.That(paths.Contains(source), Is.True);
    }

    [Test]
    public void OpeningTemporaryJsonThroughAJunctionProtectsTheOriginalAndDoesNotRewriteIt()
    {
        var native = Native(); var source = Path.Combine(_real, "temporary.json"); NativeDocumentStore.Save(source, native.Session.Document);
        var bytes = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var paths = new ProtectedSourcePaths();
        new ProjectOpenWorkflow(native, TestImages.ValidateImages, new(), paths)
            .Open(Path.Combine(_alias, "temporary.json"));
        Assert.That(native.CurrentPath, Is.Null); Assert.That(native.IsDirty, Is.True);
        Assert.That(paths.Contains(source), Is.True);
        Assert.ThrowsAsync<InvalidOperationException>(() => native.SaveAsAsync(() => Task.FromResult<string?>(source), paths));
        AssertFile(source, bytes, time);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SourcePreparationFailureAfterValidationKeepsThePreviousProjectAndSources(bool layerImport)
    {
        var native = Native(); var current = Path.Combine(_real, "current.ppgproj"); native.SaveAs(current);
        native.Session.UpdateLayer(native.Session.SelectedLayerId!.Value, layer => layer.Text = "still unsaved");
        var source = Path.Combine(_real, layerImport ? "donor.ppgproj" : "temporary.json");
        if (layerImport) NativePackageStore.Save(source, Native().Session.Document, "source-alias-test");
        else NativeDocumentStore.Save(source, Native().Session.Document);
        var bytes = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var before = native.Session.Document.Copy(); var history = (native.Session.UndoCount, native.Session.RedoCount, native.IsDirty);
        var paths = new ProtectedSourcePaths(); var oldSource = Path.Combine(_real, "earlier.png"); paths.Add(oldSource);
        void ValidateThenBreakLink(EditorDocument document)
        {
            TestImages.ValidateImages(document); RemoveJunction(_alias); Junction(_alias, _alias);
        }
        var chosen = Path.Combine(_alias, Path.GetFileName(source));
        if (layerImport)
            Assert.ThrowsAsync<IOException>(() => new ProjectLayerImportWorkflow(native.Session, ValidateThenBreakLink, paths, () => native.CurrentPath)
                .ImportAsync(() => Task.FromResult<string?>(chosen)));
        else
        {
            var workflow = new ProjectOpenWorkflow(native, ValidateThenBreakLink, new(), paths);
            Assert.Throws<IOException>(() => workflow.Open(chosen));
            Assert.That(workflow.SourceLabel, Is.Null); Assert.That(workflow.Details, Is.Empty);
            Assert.That(workflow.Summary, Is.EqualTo("Ready."));
        }
        Assert.That(native.CurrentPath, Is.EqualTo(current));
        AssertState(native.Session, before, history);
        Assert.That(paths.Count, Is.EqualTo(1)); Assert.That(paths.Contains(oldSource), Is.True);
        AssertFile(source, bytes, time);
    }

    [TestCase(ImageImportAction.Add)]
    [TestCase(ImageImportAction.Replace)]
    [TestCase(ImageImportAction.Background)]
    public void SourceResolutionFailureDoesNotCommitAnImageEdit(ImageImportAction action)
    {
        var cycle = Path.Combine(_root, "循环"); Junction(cycle, cycle);
        var session = Native().Session; var image = new Layer { Kind = LayerKind.Image, AssetId = Guid.NewGuid() };
        session.AddImageLayer(image, Png()); session.SetSelection([image.Id]);
        var before = session.Document.Copy(); var history = (session.UndoCount, session.RedoCount, session.IsDirty);
        var paths = new ProtectedSourcePaths();
        var workflow = new ImageImportWorkflow(session, _ => Task.FromResult(new LoadedImage(new(Png()), 4, 4)), paths, () => null);
        Assert.ThrowsAsync<IOException>(() => workflow.ImportAsync(action, () => Task.FromResult<string?>(Path.Combine(cycle, "image.png"))));
        AssertState(session, before, history); Assert.That(paths.Count, Is.Zero);
        Assert.That(session.SelectedLayerId, Is.EqualTo(image.Id));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BatchSourcePreparationCommitsTogetherOrLeavesAllSourcesAndHistoryUntouched(bool broken)
    {
        var names = new[] { "one.png", "two.png", "three.png" };
        foreach (var name in names) File.WriteAllBytes(Path.Combine(_real, name), Png());
        var chosen = names.Select(name => Path.Combine(_alias, name)).ToArray();
        if (broken)
        {
            var cycle = Path.Combine(_root, "循环"); Junction(cycle, cycle);
            chosen[2] = Path.Combine(cycle, "three.png");
        }
        var session = Native().Session; var before = session.Document.Copy();
        var history = (session.UndoCount, session.RedoCount, session.IsDirty); var paths = new ProtectedSourcePaths();
        var workflow = new SongBatchWorkflow(session, _ => Task.FromResult(new AssetBlob(Png())), TestImages.ValidateImages, paths);
        if (broken)
        {
            Assert.ThrowsAsync<IOException>(() => workflow.ImportAsync(() => Task.FromResult<IReadOnlyList<string>>(chosen)));
            AssertState(session, before, history); Assert.That(paths.Count, Is.Zero);
        }
        else
        {
            Assert.That(await workflow.ImportAsync(() => Task.FromResult<IReadOnlyList<string>>(chosen)), Is.EqualTo(3));
            Assert.That(paths.Count, Is.EqualTo(3));
            foreach (var name in names) Assert.That(paths.Contains(Path.Combine(_real, name)), Is.True);
            Assert.That(session.UndoCount, Is.EqualTo(history.UndoCount + 1));
            session.Undo(); Assert.That(session.Document.StateEquals(before), Is.True);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CliRejectsTheSameNativeInputUnderAnotherDirectoryNameBeforeInitialization(bool reverse)
    {
        var native = Native(); var input = Path.Combine(_real, "原生工程.png"); native.SaveAs(input);
        var alias = Path.Combine(_alias, "原生工程.png");
        var bytes = File.ReadAllBytes(input); var time = File.GetLastWriteTimeUtc(input);
        var initializations = 0; var output = new StringWriter(); var error = new StringWriter();
        Assert.That(RenderCommand.Run(["--render", reverse ? alias : input, reverse ? input : alias], output, error,
            () => initializations++), Is.EqualTo(1));
        Assert.That(initializations, Is.Zero); Assert.That(output.ToString(), Is.Empty);
        Assert.That(error.ToString(), Does.Contain("must not be overwritten"));
        AssertFile(input, bytes, time);
    }

    [Test]
    public void DesktopLayoutSaveCannotReplaceTheActiveNativeProjectThroughAJunction()
    {
        AvaloniaTestHost.EnsureStarted();
        var native = Native(); var project = Path.Combine(_real, "原生工程.ppglayout"); native.SaveAs(project);
        var bytes = File.ReadAllBytes(project); var time = File.GetLastWriteTimeUtc(project);
        var before = native.Session.Document.Copy(); var history = (native.Session.UndoCount, native.Session.RedoCount, native.IsDirty);
        var window = new MainWindow(nativeWorkflow: native, filePathPicker: action =>
        {
            Assert.That(action, Is.EqualTo(EditorFileAction.SaveLayout));
            return Task.FromResult<string?>(Path.Combine(_alias, "原生工程.ppglayout"));
        });
        try
        {
            window.Show(); Click(window, "Save layout template");
            Assert.That(Error(window), Does.Contain("不能覆盖当前工程"));
            AssertFile(project, bytes, time); AssertState(native.Session, before, history);
        }
        finally { native.Session.MarkSaved(); window.Close(); }
    }

    [Test]
    public void DesktopSaveRechecksImportedSourcesIfTheActiveProjectJunctionWasRetargeted()
    {
        AvaloniaTestHost.EnsureStarted();
        var name = "current.png"; var native = Native(); native.SaveAs(Path.Combine(_alias, name));
        var other = Path.Combine(_root, "曲绘目录"); Directory.CreateDirectory(other);
        var source = Path.Combine(other, name); File.WriteAllBytes(source, Png());
        var bytes = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var project = Path.Combine(_real, name); var projectBytes = File.ReadAllBytes(project); var projectTime = File.GetLastWriteTimeUtc(project);
        var window = new MainWindow(nativeWorkflow: native, filePathPicker: _ => Task.FromResult<string?>(source),
            imageLoader: path => Task.FromResult(new LoadedImage(new(File.ReadAllBytes(path)), 4, 4)));
        try
        {
            window.Show(); Click(window, "Import Image");
            var before = native.Session.Document.Copy(); var history = (native.Session.UndoCount, native.Session.RedoCount, native.IsDirty);
            RemoveJunction(_alias); Junction(_alias, other);
            Click(window, "Save Project");
            Assert.That(Error(window), Does.Contain("已导入的源文件"));
            AssertState(native.Session, before, history);
            AssertFile(source, bytes, time); AssertFile(project, projectBytes, projectTime);
        }
        finally { native.Session.MarkSaved(); window.Close(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OrdinarySaveCannotFollowARetargetedOrRemovedCurrentProjectJunction(bool removed)
    {
        var native = Native(); var name = "current.ppgproj"; native.SaveAs(Path.Combine(_alias, name));
        native.Session.UpdateLayer(native.Session.SelectedLayerId!.Value, layer => layer.Text = "unsaved edit");
        var oldFile = Path.Combine(_real, name); var bytes = File.ReadAllBytes(oldFile); var time = File.GetLastWriteTimeUtc(oldFile);
        var other = Path.Combine(_root, "其他工程"); Directory.CreateDirectory(other);
        var donor = Path.Combine(other, name); NativePackageStore.Save(donor, Native().Session.Document, "donor");
        var donorBytes = File.ReadAllBytes(donor); var donorTime = File.GetLastWriteTimeUtc(donor);
        var before = native.Session.Document.Copy(); var history = (native.Session.UndoCount, native.Session.RedoCount, native.IsDirty);
        RemoveJunction(_alias); if (!removed) Junction(_alias, other);
        Assert.Throws<IOException>(() => native.Save());
        AssertState(native.Session, before, history); AssertFile(oldFile, bytes, time); AssertFile(donor, donorBytes, donorTime);
        if (!removed) RemoveJunction(_alias);
        Junction(_alias, _real); native.Save();
        Assert.That(NativePackageStore.Read(oldFile).StateEquals(before), Is.True); Assert.That(native.IsDirty, Is.False);
        AssertFile(donor, donorBytes, donorTime);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NativeOpenOrSaveAsRejectsAPathRetargetedDuringValidation(bool save)
    {
        var name = "candidate.ppgproj"; var source = Path.Combine(_real, name);
        NativePackageStore.Save(source, Native().Session.Document, "original");
        var other = Path.Combine(_root, "其他工程"); Directory.CreateDirectory(other);
        var donor = Path.Combine(other, name); NativePackageStore.Save(donor, Native().Session.Document, "donor");
        var originalBytes = File.ReadAllBytes(source); var originalTime = File.GetLastWriteTimeUtc(source);
        var donorBytes = File.ReadAllBytes(donor); var donorTime = File.GetLastWriteTimeUtc(donor);
        var session = Native().Session; var before = session.Document.Copy(); var history = (session.UndoCount, session.RedoCount, session.IsDirty);
        var native = new NativeProjectWorkflow(session, document =>
        {
            TestImages.ValidateImages(document); RemoveJunction(_alias); Junction(_alias, other);
        }, "source-alias-test");
        var alias = Path.Combine(_alias, name);
        if (save) Assert.Throws<IOException>(() => native.SaveAs(alias));
        else Assert.Throws<IOException>(() => native.Open(alias));
        Assert.That(native.CurrentPath, Is.Null); AssertState(session, before, history);
        AssertFile(source, originalBytes, originalTime); AssertFile(donor, donorBytes, donorTime);
    }

    [TestCase(ImageImportAction.Add, false)]
    [TestCase(ImageImportAction.Replace, false)]
    [TestCase(ImageImportAction.Background, false)]
    [TestCase(ImageImportAction.Add, true)]
    [TestCase(ImageImportAction.Replace, true)]
    [TestCase(ImageImportAction.Background, true)]
    public async Task ImageSourceChangingDuringReadRejectsTheWholeEditAndAllowsRetry(ImageImportAction action, bool removed)
    {
        var source = Path.Combine(_real, "source.png"); File.WriteAllBytes(source, Png());
        var bytes = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var native = Native(); var session = native.Session;
        var image = new Layer { Kind = LayerKind.Image, AssetId = Guid.NewGuid() };
        session.AddImageLayer(image, TestImages.EncodePng(2, 2, TestImages.SyntheticBgra(2, 2)));
        session.SetSelection([image.Id]);
        session.UpdateLayer(image.Id, layer => layer.Name = "redo"); session.Undo();
        var before = session.Document.Copy(); var selected = session.SelectedLayerIds.ToArray();
        var history = (session.UndoCount, session.RedoCount, session.IsDirty);
        var paths = new ProtectedSourcePaths(); paths.Add(Path.Combine(_real, "earlier.png"));
        var change = true;
        var workflow = new ImageImportWorkflow(session, path =>
        {
            var loaded = new LoadedImage(new(File.ReadAllBytes(path)), 4, 4);
            if (change) ChangeJunction(removed);
            return Task.FromResult(loaded);
        }, paths, () => native.CurrentPath);
        Assert.ThrowsAsync<IOException>(() => workflow.ImportAsync(action,
            () => Task.FromResult<string?>(Path.Combine(_alias, "source.png"))));
        AssertState(session, before, history); Assert.That(session.SelectedLayerIds, Is.EqualTo(selected));
        Assert.That(paths.Count, Is.EqualTo(1)); AssertFile(source, bytes, time);
        RestoreJunction(); change = false;
        Assert.That(await workflow.ImportAsync(action, () => Task.FromResult<string?>(Path.Combine(_alias, "source.png"))), Is.True);
        Assert.That(paths.Contains(source), Is.True);
        Assert.That(session.UndoCount, Is.EqualTo(history.UndoCount + 1));
        Assert.That(session.RedoCount, Is.Zero);
        session.Undo(); Assert.That(session.Document.StateEquals(before), Is.True);
        AssertFile(source, bytes, time);
    }

    [TestCase(3, "read")]
    [TestCase(3, "validate")]
    [TestCase(5, "read")]
    [TestCase(5, "validate")]
    public async Task BatchSourceChangingMidOperationRejectsAllSongsAndAllowsRetry(int count, string stage)
    {
        var names = Enumerable.Range(1, count).Select(i => $"song{i}.png").ToArray();
        foreach (var name in names) File.WriteAllBytes(Path.Combine(_real, name), Png());
        var native = Native(); var session = native.Session;
        var before = session.Document.Copy(); var selected = session.SelectedLayerIds.ToArray();
        var history = (session.UndoCount, session.RedoCount, session.IsDirty);
        var paths = new ProtectedSourcePaths(); paths.Add(Path.Combine(_real, "earlier.png"));
        var chosen = names.Select(name => Path.Combine(_alias, name)).ToArray();
        var change = true; var reads = 0;
        var workflow = new SongBatchWorkflow(session, path =>
        {
            var image = new AssetBlob(File.ReadAllBytes(path));
            if (++reads == 1 && change && stage == "read") ChangeJunction(false);
            return Task.FromResult(image);
        }, document =>
        {
            TestImages.ValidateImages(document);
            if (change && stage == "validate") ChangeJunction(false);
        }, paths);
        Assert.ThrowsAsync<IOException>(() => workflow.ImportAsync(() => Task.FromResult<IReadOnlyList<string>>(chosen)));
        AssertState(session, before, history); Assert.That(session.SelectedLayerIds, Is.EqualTo(selected));
        Assert.That(paths.Count, Is.EqualTo(1));
        RestoreJunction(); change = false; reads = 0;
        Assert.That(await workflow.ImportAsync(() => Task.FromResult<IReadOnlyList<string>>(chosen)), Is.EqualTo(count));
        Assert.That(paths.Count, Is.EqualTo(count + 1));
        foreach (var name in names) Assert.That(paths.Contains(Path.Combine(_real, name)), Is.True);
        Assert.That(session.UndoCount, Is.EqualTo(history.UndoCount + 1));
        session.Undo(); Assert.That(session.Document.StateEquals(before), Is.True);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task ConversionOrLayerImportChangingSourceDuringValidationLeavesEditorIntact(bool layerImport, bool removed)
    {
        var native = Native(); var current = Path.Combine(_real, "current.ppgproj"); native.SaveAs(current);
        native.Session.UpdateLayer(native.Session.SelectedLayerId!.Value, layer => layer.Text = "unsaved");
        var source = Path.Combine(_real, layerImport ? "donor.ppgproj" : "temporary.json");
        if (layerImport) NativePackageStore.Save(source, Native().Session.Document, "donor");
        else NativeDocumentStore.Save(source, Native().Session.Document);
        var bytes = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var before = native.Session.Document.Copy(); var selected = native.Session.SelectedLayerIds.ToArray();
        var history = (native.Session.UndoCount, native.Session.RedoCount, native.IsDirty);
        var paths = new ProtectedSourcePaths(); paths.Add(Path.Combine(_real, "earlier.png"));
        var change = true;
        void Validate(EditorDocument document)
        {
            TestImages.ValidateImages(document); if (change) ChangeJunction(removed);
        }
        var chosen = Path.Combine(_alias, Path.GetFileName(source));
        var open = new ProjectOpenWorkflow(native, Validate, new(), paths);
        var import = new ProjectLayerImportWorkflow(native.Session, Validate, paths, () => native.CurrentPath);
        if (layerImport) Assert.ThrowsAsync<IOException>(() => import.ImportAsync(() => Task.FromResult<string?>(chosen)));
        else Assert.Throws<IOException>(() => open.Open(chosen));
        Assert.That(native.CurrentPath, Is.EqualTo(current)); AssertState(native.Session, before, history);
        Assert.That(native.Session.SelectedLayerIds, Is.EqualTo(selected));
        Assert.That(paths.Count, Is.EqualTo(1)); Assert.That(open.Summary, Is.EqualTo("Ready."));
        AssertFile(source, bytes, time);
        RestoreJunction(); change = false;
        if (layerImport) Assert.That(await import.ImportAsync(() => Task.FromResult<string?>(chosen)), Is.EqualTo(1));
        else { open.Open(chosen); Assert.That(native.CurrentPath, Is.Null); Assert.That(native.IsDirty, Is.True); }
        Assert.That(paths.Contains(source), Is.True); AssertFile(source, bytes, time);
    }

    [TestCase("validate", false)]
    [TestCase("validate", true)]
    [TestCase("render", false)]
    [TestCase("render", true)]
    public void PngDestinationChangingDuringValidationOrRenderingNeverWritesAnotherTarget(string stage, bool removed)
    {
        var native = Native(); var session = native.Session;
        var current = Path.Combine(_real, "current.ppgproj"); native.SaveAs(current);
        session.UpdateLayer(session.SelectedLayerId!.Value, layer => layer.Text = "unsaved");
        var other = Path.Combine(_root, "other"); Directory.CreateDirectory(other);
        var target = Path.Combine(_real, "export.png"); File.WriteAllBytes(target, [1, 2, 3]);
        var protectedFile = Path.Combine(other, "export.png"); File.WriteAllBytes(protectedFile, Png());
        var bytes = File.ReadAllBytes(protectedFile); var time = File.GetLastWriteTimeUtc(protectedFile);
        var originalBytes = File.ReadAllBytes(target); var originalTime = File.GetLastWriteTimeUtc(target);
        var before = session.Document.Copy(); var history = (session.UndoCount, session.RedoCount, session.IsDirty);
        var paths = new ProtectedSourcePaths(); paths.Add(protectedFile); var change = true; var writes = 0;
        void Change() { RemoveJunction(_alias); if (!removed) Junction(_alias, other); }
        var workflow = new PngExportWorkflow(session, document =>
        {
            TestImages.ValidateImages(document); if (change && stage == "validate") Change();
        }, (_, _, _) => { if (change && stage == "render") Change(); return Png(); },
            (path, data) => { writes++; AtomicFile.Write(path, stream => stream.Write(data)); }, paths, () => native.CurrentPath);
        Assert.Throws<IOException>(() => workflow.Export(Path.Combine(_alias, "export.png")));
        Assert.That(writes, Is.Zero); AssertFile(protectedFile, bytes, time); AssertFile(target, originalBytes, originalTime);
        AssertState(session, before, history); Assert.That(native.CurrentPath, Is.EqualTo(current));
        RestoreJunction(); change = false;
        workflow.Export(Path.Combine(_alias, "export.png"));
        Assert.That(writes, Is.EqualTo(1)); Assert.That(File.ReadAllBytes(target), Is.EqualTo(Png()));
        AssertFile(protectedFile, bytes, time); AssertState(session, before, history);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TemporaryDestinationChangingDuringValidationNeverWritesOrMarksSaved(bool removed)
    {
        var session = Native().Session; var change = false;
        var native = new NativeProjectWorkflow(session, document =>
        {
            TestImages.ValidateImages(document); if (change) ChangeJunction(removed);
        }, "lifecycle-test");
        var current = Path.Combine(_real, "current.ppgproj"); native.SaveAs(current);
        session.UpdateLayer(session.SelectedLayerId!.Value, layer => layer.Text = "unsaved");
        var target = Path.Combine(_real, "temporary.json"); File.WriteAllText(target, "old output");
        var bytes = File.ReadAllBytes(target); var time = File.GetLastWriteTimeUtc(target);
        var before = session.Document.Copy(); var selected = session.SelectedLayerIds.ToArray();
        var history = (session.UndoCount, session.RedoCount, session.IsDirty);
        change = true;
        Assert.ThrowsAsync<IOException>(() => native.SaveTemporaryAsync(
            () => Task.FromResult<string?>(Path.Combine(_alias, "temporary.json")), new()));
        Assert.That(native.CurrentPath, Is.EqualTo(current)); AssertState(session, before, history);
        Assert.That(session.SelectedLayerIds, Is.EqualTo(selected)); AssertFile(target, bytes, time);
        RestoreJunction(); change = false;
        Assert.That(await native.SaveTemporaryAsync(() => Task.FromResult<string?>(Path.Combine(_alias, "temporary.json")), new()), Is.True);
        Assert.That(NativeDocumentStore.Read(target).StateEquals(before), Is.True);
        Assert.That(native.CurrentPath, Is.Null); Assert.That(native.IsDirty, Is.False);
    }

    [TestCase("classic", false)]
    [TestCase("classic", true)]
    [TestCase("free", false)]
    [TestCase("free", true)]
    public void LegacyConversionChangingSourceDuringValidationPreservesThePriorConversion(string kind, bool removed)
    {
        var native = Native(); var earlier = Path.Combine(_real, "earlier.json");
        NativeDocumentStore.Save(earlier, native.Session.Document);
        var sources = new ProtectedSourcePaths(); var change = false;
        var open = new ProjectOpenWorkflow(native, document =>
        {
            TestImages.ValidateImages(document); if (change) ChangeJunction(removed);
        }, new() { ProbeImageSize = Editor.Rendering.ImageAssetValidator.ReadEncodedDimensions,
            ProbeImageOpacity = asset => Editor.Rendering.ImageAssetValidator.IsOpaque(asset, TestImages.Decoder) }, sources);
        open.Open(earlier);
        native.Session.UpdateLayer(native.Session.Document.Layers.Single().Id, layer => layer.Text = "unsaved");
        var source = Path.Combine(_real, kind == "classic" ? "旧工程.phipreview" : "旧工程.phifree");
        var png = Convert.ToBase64String(Png());
        File.WriteAllText(source, kind == "classic"
            ? $"<Project><Background>{png}</Background><Labels /></Project>"
            : $"<FreeProject><Width>1440</Width><Height>1080</Height><SoftBackground>false</SoftBackground><Layers><FreeLayer><Id>1</Id><Kind>Text</Kind><Name>song</Name><Width>200</Width><Height>100</Height><Text>中文</Text></FreeLayer></Layers></FreeProject>");
        var bytes = File.ReadAllBytes(source); var time = File.GetLastWriteTimeUtc(source);
        var before = native.Session.Document.Copy(); var history = (native.Session.UndoCount, native.Session.RedoCount, native.IsDirty);
        var selected = native.Session.SelectedLayerIds.ToArray();
        var conversion = (open.SourceLabel, open.Summary, Details: open.Details.ToArray());
        change = true;
        Assert.Throws<IOException>(() => open.Open(Path.Combine(_alias, Path.GetFileName(source))));
        AssertState(native.Session, before, history); Assert.That(native.Session.SelectedLayerIds, Is.EqualTo(selected));
        Assert.That(native.CurrentPath, Is.Null);
        Assert.That((open.SourceLabel, open.Summary), Is.EqualTo((conversion.SourceLabel, conversion.Summary)));
        Assert.That(open.Details, Is.EqualTo(conversion.Details));
        Assert.That(sources.Count, Is.EqualTo(1)); Assert.That(sources.Contains(earlier), Is.True);
        AssertFile(source, bytes, time);
        RestoreJunction(); change = false;
        open.Open(Path.Combine(_alias, Path.GetFileName(source)));
        Assert.That(sources.Contains(source), Is.True); Assert.That(native.IsDirty, Is.True);
        AssertFile(source, bytes, time);
    }

    [TestCase("source", false)]
    [TestCase("source", true)]
    [TestCase("destination", false)]
    [TestCase("destination", true)]
    public void CliPathChangingDuringInitializationRejectsWithoutRenderingOrWriting(string changed, bool removed)
    {
        AvaloniaTestHost.EnsureStarted();
        var source = Path.Combine(_real, "source.ppgproj"); Native().SaveAs(source);
        var target = Path.Combine(_real, "export.png"); File.WriteAllBytes(target, [4, 5, 6]);
        var sourceBytes = File.ReadAllBytes(source); var sourceTime = File.GetLastWriteTimeUtc(source);
        var targetBytes = File.ReadAllBytes(target); var targetTime = File.GetLastWriteTimeUtc(target);
        var input = changed == "source" ? Path.Combine(_alias, "source.ppgproj") : source;
        var output = changed == "destination" ? Path.Combine(_alias, "export.png") : target;
        var stdout = new StringWriter(); var stderr = new StringWriter();
        Assert.That(RenderCommand.Run(["--render", input, output], stdout, stderr,
            () => ChangeJunction(removed)), Is.EqualTo(1));
        Assert.That(stdout.ToString(), Is.Empty);
        Assert.That(stderr.ToString(), Does.Contain("now points to another location"));
        AssertFile(source, sourceBytes, sourceTime); AssertFile(target, targetBytes, targetTime);
        Assert.That(Directory.Exists(_alias), Is.EqualTo(!removed));
        Assert.That(Directory.GetFiles(_real, "*.tmp"), Is.Empty);
    }

    private void ChangeJunction(bool removed)
    {
        RemoveJunction(_alias);
        if (removed) return;
        var other = Path.Combine(_root, "changed"); Directory.CreateDirectory(other);
        foreach (var source in Directory.GetFiles(_real)) File.Copy(source, Path.Combine(other, Path.GetFileName(source)), overwrite: true);
        Junction(_alias, other);
    }

    private void RestoreJunction()
    {
        if (_junctions.Contains(_alias)) RemoveJunction(_alias);
        Junction(_alias, _real);
    }

    private static void Click(MainWindow window, string label)
    {
        UiTestActions.Click(UiTestActions.Find(window, label));
        Dispatcher.UIThread.RunJobs();
    }
    private static string? Error(MainWindow window) => UiTestActions.Error(window);
    private void RemoveJunction(string path)
    {
        Directory.Delete(path, recursive: false); _junctions.Remove(path);
    }

    private NativeProjectWorkflow Native()
    {
        var session = new EditSession(new EditorDocument());
        var layer = new Layer { Kind = LayerKind.Text, Text = "中文 source", Bounds = new(20, 30, 200, 80) };
        session.AddLayer(layer); session.SetSelection([layer.Id]);
        return new NativeProjectWorkflow(session, TestImages.ValidateImages, "source-alias-test");
    }

    private void Junction(string path, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            Arguments = $"/d /c mklink /J \"{path}\" \"{target}\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        Assert.That(process.WaitForExit(10000), Is.True, "junction fixture creation timed out");
        Assert.That(process.ExitCode, Is.Zero, output + error);
        _junctions.Add(path);
        Assert.That(new DirectoryInfo(path).LinkTarget, Is.Not.Null, "fixture must be an actual directory junction");
    }

    private static byte[] Png() => TestImages.EncodePng(4, 4, TestImages.SyntheticBgra(4, 4));
    private static void AssertFile(string path, byte[] bytes, DateTime time)
    {
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(time));
    }
    private static void AssertState(EditSession session, EditorDocument before, (int Undo, int Redo, bool Dirty) history)
    {
        Assert.That(session.Document.StateEquals(before), Is.True);
        Assert.That((session.UndoCount, session.RedoCount, session.IsDirty), Is.EqualTo(history));
    }
}
