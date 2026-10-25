using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R1TemplateLibraryTests
{
    private string _root = null!;
    private string _libraryPath = null!;
    private string _external = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "r1-template-" + Guid.NewGuid().ToString("N"));
        _libraryPath = Path.Combine(_root, "库 目录");
        _external = Path.Combine(_root, "用户 原模板");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_libraryPath);
        Directory.CreateDirectory(_external);
    }

    [TearDown]
    public void TearDown()
    {
        var expected = Path.GetFullPath(TestContext.CurrentContext.WorkDirectory) + Path.DirectorySeparatorChar;
        Assert.That(Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase), Is.True);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void MultipleTemplatesReloadFromDiskWithControlledFileNames()
    {
        var library = new TemplateLibrary(_libraryPath);
        var first = library.Add(Template("布局 甲", 1));
        var second = library.Add(Template("布局 乙", 2));
        Assert.That(first.Added && second.Added, Is.True);
        Assert.That(first.Entry.Id, Does.Match("^[0-9a-f]{32}$"));
        Assert.That(Path.GetFileName(first.Entry.Id), Is.EqualTo(first.Entry.Id));
        Assert.That(Directory.GetFiles(_libraryPath, "*.ppglayout"), Has.Length.EqualTo(2));

        var restarted = new TemplateLibrary(_libraryPath).Load();
        Assert.That(restarted.Diagnostics, Is.Empty);
        Assert.That(restarted.Entries.Select(entry => entry.Name),
            Is.EquivalentTo(new[] { "布局 甲", "布局 乙" }));
        Assert.That(restarted.Entries.Select(entry => entry.SlotCount), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void DuplicateContentIsIdempotentAndSameNameDifferentContentIsNotSilentlyReplaced()
    {
        var library = new TemplateLibrary(_libraryPath);
        var template = Template("同名布局", 2);
        var first = library.Add(template);
        var duplicate = library.Add(template);
        Assert.That(duplicate.Added, Is.False);
        Assert.That(duplicate.Entry.Id, Is.EqualTo(first.Entry.Id));
        var different = library.Add(template with
        {
            Slots = [new RectD(0, 0, 1, 1)]
        });
        Assert.That(different.Added, Is.True);
        Assert.That(different.Entry.Id, Is.Not.EqualTo(first.Entry.Id));
        Assert.That(library.Load().Entries.Count(entry => entry.Name == "同名布局"), Is.EqualTo(2));
        Assert.That(library.Read(first.Entry.Id).Slots, Is.EqualTo(template.Slots));
        Assert.That(library.Read(different.Entry.Id).Slots, Is.EqualTo(new[] { new RectD(0, 0, 1, 1) }));
    }

    [Test]
    public void ImportRenameExportAndRemoveOnlyTouchTheLibraryCopy()
    {
        var sourcePath = Path.Combine(_external, "原模板.ppglayout");
        var source = Template("用户原模板", 2) with { Id = "user-file-id" };
        LayoutTemplateStore.Save(sourcePath, source);
        var sourceBytes = File.ReadAllBytes(sourcePath);
        var sourceTime = File.GetLastWriteTimeUtc(sourcePath);

        var library = new TemplateLibrary(_libraryPath);
        var added = library.Import(sourcePath);
        Assert.That(added.Added, Is.True);
        Assert.That(added.Entry.Id, Is.Not.EqualTo(source.Id));
        Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(sourceBytes));
        Assert.That(File.GetLastWriteTimeUtc(sourcePath), Is.EqualTo(sourceTime));

        var renamed = library.Rename(added.Entry.Id, "改名后的库模板");
        Assert.That(renamed.Name, Is.EqualTo("改名后的库模板"));
        Assert.That(library.Load().Entries.Single().Name, Is.EqualTo("改名后的库模板"));
        Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(sourceBytes));

        var exportPath = Path.Combine(_external, "导出的副本.ppglayout");
        library.Export(added.Entry.Id, exportPath);
        Assert.That(LayoutTemplateStore.Read(exportPath).Slots, Is.EqualTo(source.Slots));
        Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(sourceBytes));

        library.Remove(added.Entry.Id);
        Assert.That(library.Load().Entries, Is.Empty);
        Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(sourceBytes));
        Assert.That(File.Exists(exportPath), Is.True);
    }

    [Test]
    public void ACorruptOrFutureEntryIsIsolatedAndNeverRewritten()
    {
        var library = new TemplateLibrary(_libraryPath);
        var valid = library.Add(Template("有效模板", 1));
        var corruptId = Guid.NewGuid().ToString("N");
        var futureId = Guid.NewGuid().ToString("N");
        var corruptPath = Path.Combine(_libraryPath, corruptId + ".ppglayout");
        var futurePath = Path.Combine(_libraryPath, futureId + ".ppglayout");
        File.WriteAllText(corruptPath, "{broken");
        File.WriteAllText(futurePath,
            "{\"formatVersion\":2,\"id\":\"x\",\"name\":\"future\",\"slots\":[{\"x\":0,\"y\":0,\"width\":1,\"height\":1}]}");
        var beforeCorrupt = File.ReadAllBytes(corruptPath);
        var beforeFuture = File.ReadAllBytes(futurePath);

        var snapshot = library.Load();
        Assert.That(snapshot.Entries.Select(entry => entry.Name), Is.EqualTo(new[] { "有效模板" }));
        Assert.That(snapshot.Diagnostics, Has.Count.EqualTo(2));
        Assert.That(File.ReadAllBytes(corruptPath), Is.EqualTo(beforeCorrupt));
        Assert.That(File.ReadAllBytes(futurePath), Is.EqualTo(beforeFuture));
        Assert.That(library.Read(valid.Entry.Id).Name, Is.EqualTo("有效模板"));
    }

    [Test]
    public void DisplayNamesCannotChooseAFilePath()
    {
        var library = new TemplateLibrary(_libraryPath);
        var added = library.Add(Template("../../越界名称", 1));
        Assert.That(added.Entry.Name, Is.EqualTo("../../越界名称"));
        Assert.That(added.Entry.Id, Does.Match("^[0-9a-f]{32}$"));
        Assert.That(Directory.GetFiles(_root, "*.ppglayout", SearchOption.AllDirectories),
            Has.Length.EqualTo(1));
        Assert.That(File.Exists(Path.Combine(_root, "越界名称.ppglayout")), Is.False);
    }

    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    [TestCase(1080, 1920)]
    public void LibraryTemplateAppliesToGroupsWithHiddenMembersAndOneUndo(int width, int height)
    {
        var group = Guid.NewGuid();
        var art = new Layer { Kind = LayerKind.Shape, GroupId = group, Name = "art",
            Bounds = new RectD(50, 50, 300, 200) };
        var hidden = EditorDocument.NewBadge(new PointD(220, 120));
        hidden.GroupId = group; hidden.Visible = false;
        var standalone = new Layer { Kind = LayerKind.Text, Name = "title",
            Bounds = new RectD(400, 300, 220, 80) };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 700,
            Layers = [art, hidden, standalone] });
        var library = new TemplateLibrary(_libraryPath);
        var entry = library.Add(Template("两槽位", 2));
        var template = library.Read(entry.Entry.Id);
        var before = edit.Document.Copy();
        edit.ApplyLayoutTemplate(template, [art.Id, standalone.Id]);
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.Document.Find(hidden.Id).Visible, Is.False);
        Assert.That(edit.Document.Find(hidden.Id).GroupId, Is.EqualTo(group));
        Assert.That(edit.Document.Find(art.Id).Bounds.X, Is.GreaterThanOrEqualTo(0));
        Assert.That(edit.Document.Find(standalone.Id).Bounds.X, Is.GreaterThanOrEqualTo(0));
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
        edit.Redo();
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public void LockedGroupIsRejectedWithoutChangingTheLibraryOrDocument()
    {
        var library = new TemplateLibrary(_libraryPath);
        var entry = library.Add(Template("锁定测试", 1));
        var locked = new Layer { Kind = LayerKind.Shape, Locked = true,
            Bounds = new RectD(10, 10, 100, 100) };
        var edit = new EditSession(new EditorDocument { Layers = [locked] });
        var before = edit.Document.Copy();
        Assert.That(() => edit.ApplyLayoutTemplate(library.Read(entry.Entry.Id), [locked.Id]),
            Throws.InstanceOf<InvalidOperationException>());
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(library.Read(entry.Entry.Id).Slots, Is.EqualTo(entry.Entry.Slots));
    }

    private static LayoutTemplate Template(string name, int slots)
    {
        var boxes = slots switch
        {
            1 => new[] { new RectD(.1, .1, .5, .5) },
            2 => new[] { new RectD(.05, .1, .4, .7), new RectD(.55, .1, .4, .7) },
            _ => new[] { new RectD(.1, .1, .2, .2) }
        };
        return new LayoutTemplate(Guid.NewGuid().ToString("N"), name, boxes);
    }
}
