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

public sealed class P4CustomTemplateTests
{
    private string _directory = null!;
    [SetUp] public void SetUp() => _directory = Directory.CreateDirectory(Path.Combine(
        TestContext.CurrentContext.WorkDirectory, "templates 中文 " + Guid.NewGuid().ToString("N"))).FullName;
    [TearDown] public void TearDown() => Directory.Delete(_directory, true);
    private string PathName => Path.Combine(_directory, "layout.ppglayout");

    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    public void CapturedGroupRoundTripsWithoutContentAndAppliesAcrossCanvasSizes(int width, int height)
    {
        var group = Guid.NewGuid();
        var art = new Layer { Kind = LayerKind.Shape, GroupId = group,
            Bounds = new RectD(100, 100, 300, 200), Name = "Private song title" };
        var badge = EditorDocument.NewBadge(new PointD(300, 300));
        badge.GroupId = group; badge.Visible = false;
        var text = new Layer { Kind = LayerKind.Text, Bounds = new RectD(600, 500, 200, 100),
            Text = "Private lyrics", FontFamily = "Private font", FontSize = 45 };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 750,
            Layers = [art, badge, text] });
        var before = edit.Document.Copy();
        var template = edit.CaptureLayoutTemplate("自制布局", [art.Id, badge.Id, text.Id]);
        Assert.That(template.Slots.Count, Is.EqualTo(2));
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.IsDirty, Is.False);
        LayoutTemplateStore.Save(PathName, template);
        var bytes = File.ReadAllBytes(PathName);
        var timestamp = File.GetLastWriteTimeUtc(PathName);
        var loaded = LayoutTemplateStore.Read(PathName);
        Assert.That(loaded.Slots, Is.EqualTo(template.Slots));
        Assert.That(File.ReadAllBytes(PathName), Is.EqualTo(bytes));
        Assert.That(File.GetLastWriteTimeUtc(PathName), Is.EqualTo(timestamp));
        Assert.That(File.ReadAllText(PathName), Does.Not.Contain("Private").And.Not.Contain(art.Id.ToString()));
        edit.UpdateDocument(document => { document.CanvasWidth = width; document.CanvasHeight = height; });
        var beforeApply = edit.Document.Copy();
        var undoCount = edit.UndoCount;
        edit.ApplyLayoutTemplate(loaded, [art.Id, text.Id]);
        Assert.That(edit.UndoCount, Is.EqualTo(undoCount + 1));
        Assert.That(edit.Document.Find(badge.Id).Bounds.Width / edit.Document.Find(badge.Id).Bounds.Height,
            Is.EqualTo(128.0 / 108).Within(1e-10));
        Assert.That(edit.Document.Find(badge.Id).Visible, Is.False);
        var pixels = TestImages.RenderPng(edit.Document);
        var nativePath = Path.Combine(_directory, "applied.ppgproj");
        NativePackageStore.Save(nativePath, edit.Document, "template-test");
        Assert.That(TestImages.RenderPng(NativePackageStore.Read(nativePath)), Is.EqualTo(pixels));
        edit.Undo();
        Assert.That(edit.Document.StateEquals(beforeApply), Is.True);
        edit.Redo();
        Assert.That(TestImages.RenderPng(edit.Document), Is.EqualTo(pixels));
    }

    [TestCase("{\"formatVersion\":2,\"id\":\"a\",\"name\":\"a\",\"slots\":[]}")]
    [TestCase("{\"formatVersion\":1,\"id\":\"a\",\"name\":\"a\",\"slots\":[]}")]
    [TestCase("{\"formatVersion\":1,\"id\":\"a\",\"name\":\"a\",\"slots\":[],\"assets\":{}}")]
    [TestCase("{\"formatVersion\":1,\"id\":\"a\",\"id\":\"b\",\"name\":\"a\",\"slots\":[]}")]
    [TestCase("{\"formatVersion\":1,\"id\":\"a\",\"name\":null,\"slots\":[{\"x\":0,\"y\":0,\"width\":1,\"height\":1}]}")]
    [TestCase("{\"formatVersion\":1,\"id\":\"a\",\"name\":\"a\",\"slots\":[{\"x\":-1,\"y\":0,\"width\":1,\"height\":1}]}")]
    [TestCase("{\"formatVersion\":1,\"id\":\"a\",\"name\":\"a\",\"slots\":[{\"x\":0,\"y\":0,\"width\":1e999,\"height\":1}]}")]
    [TestCase("{\"formatVersion\":1,\"id\":\"a\",\"name\":\"a\",\"slots\":[{\"x\":0,\"y\":0,\"width\":1,\"height\":0}]}")]
    [TestCase("{\"formatVersion\":1,\"id\":\"a\",\"name\":\"a\",\"slots\":[{\"x\":0,\"y\":0,\"width\":1,\"height\":1,\"url\":\"file://bad\"}]}")]
    [TestCase("[]")]
    [TestCase("{broken")]
    public void MalformedOrFutureTemplatesAreRejectedWithoutWriting(string json)
    {
        File.WriteAllText(PathName, json);
        Assert.Throws<InvalidDataException>(() => LayoutTemplateStore.Read(PathName));
        Assert.That(File.ReadAllText(PathName), Is.EqualTo(json));
    }

    [Test]
    public void InvalidSavePreservesExistingFileAndOversizedReadIsRejected()
    {
        var template = LayoutTemplates.All[0];
        LayoutTemplateStore.Save(PathName, template);
        var original = File.ReadAllBytes(PathName);
        Assert.Throws<InvalidOperationException>(() => LayoutTemplateStore.Save(PathName,
            template with { Slots = [new RectD(0, 0, 2, 1)] }));
        Assert.That(File.ReadAllBytes(PathName), Is.EqualTo(original));
        Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
        File.WriteAllBytes(PathName, new byte[LayoutTemplateStore.MaxBytes + 1]);
        Assert.Throws<InvalidDataException>(() => LayoutTemplateStore.Read(PathName));
    }

    [Test]
    public void LateFitFailureRollsBackEarlierSlotsAndKeepsRedo()
    {
        var first = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(0, 0, 100, 100) };
        var badge = EditorDocument.NewBadge(new PointD(300, 100));
        var edit = new EditSession(new EditorDocument { Layers = [first, badge] });
        edit.UpdateLayer(first.Id, layer => layer.Name = "changed");
        edit.Undo();
        var original = edit.Document.Copy();
        var template = new LayoutTemplate("small", "Small", [new(.5, .5, .1, .1), new(0, 0, .00001, .00001)]);
        Assert.Throws<InvalidOperationException>(() => edit.ApplyLayoutTemplate(template, [first.Id, badge.Id]));
        Assert.That(edit.Document.StateEquals(original), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(edit.RedoCount, Is.EqualTo(1));
    }

    [Test]
    public void CaptureRejectsOffCanvasEmptyAndActiveGestureWithoutChanges()
    {
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(-1, 0, 100, 100) };
        var edit = new EditSession(new EditorDocument { Layers = [layer] });
        Assert.Throws<InvalidOperationException>(() => edit.CaptureLayoutTemplate("Outside", [layer.Id]));
        Assert.Throws<InvalidOperationException>(() => edit.CaptureLayoutTemplate("Empty", []));
        edit.BeginGesture(layer.Id);
        Assert.Throws<InvalidOperationException>(() => edit.CaptureLayoutTemplate("Gesture", [layer.Id]));
        edit.CancelGesture();
        Assert.That(edit.IsDirty, Is.False);
    }

    [Test]
    public void DesktopCaptureAndApplyPreserveExistingSelectionOrder()
    {
        var a = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(100, 100, 100, 100) };
        var b = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(400, 200, 100, 100) };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 750, Layers = [a, b] });
        edit.SetSelection([a.Id, b.Id]);
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            void Click(string name) => UiTestActions.Click(UiTestActions.FindNamed(window, name));
            Click("CaptureLayoutTemplate");
            var combo = window.GetLogicalDescendants().OfType<ComboBox>().Single(x => x.Name == "LayoutTemplate");
            var template = (LayoutTemplate)combo.SelectedItem!;
            Assert.That(template.Slots[0].X, Is.EqualTo(.1));
            Assert.That(edit.IsDirty, Is.False);
            edit.UpdateLayer(a.Id, layer => layer.Bounds = layer.Bounds with { X = 800 });
            Click("ApplyLayoutTemplate");
            Assert.That(edit.Document.Find(a.Id).Bounds.X, Is.EqualTo(100).Within(1e-9));
            Assert.That(edit.Document.Find(b.Id).Bounds.X, Is.EqualTo(400).Within(1e-9));
            Assert.That(edit.UndoCount, Is.EqualTo(2));
        }
        finally { edit.MarkSaved(); window.Close(); }
    }
}
