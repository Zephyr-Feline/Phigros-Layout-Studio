using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4LayoutTemplateTests
{
    [TestCase("three-columns", 3, 1440, 1080)]
    [TestCase("five-point", 5, 2740, 1080)]
    [TestCase("five-point", 5, 1440, 1080)]
    public void LayoutFitsExistingGroupsWithoutChangingTheirIdentityOrInternalGeometry(
        string templateId, int count, int width, int height)
    {
        var layers = new List<Layer>();
        var selected = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            var group = Guid.NewGuid();
            var art = new Layer { Kind = LayerKind.Shape, GroupId = group,
                Name = $"Artwork {index}", Bounds = new RectD(100, 100, 600, 250) };
            var badge = EditorDocument.NewBadge(new PointD(460, 210));
            badge.GroupId = group;
            badge.Name = $"Badge {index}";
            badge.Level = index + 1;
            badge.Visible = index != 1;
            layers.AddRange([art, badge]);
            selected.Add(art.Id);
        }
        var edit = new EditSession(new EditorDocument { CanvasWidth = width, CanvasHeight = height,
            Layers = layers });
        edit.SetSelection(selected);
        var before = edit.Document.Copy();
        var beforePng = TestImages.RenderPng(edit.Document);
        edit.ApplyLayoutTemplate(templateId, selected);
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(TestImages.RenderPng(edit.Document), Is.Not.EqualTo(beforePng));
        var arranged = edit.Document.Copy();
        for (var index = 0; index < count; index++)
        {
            var art = edit.Document.Find(layers[index * 2].Id);
            var badge = edit.Document.Find(layers[index * 2 + 1].Id);
            var scale = art.Bounds.Width / 600;
            Assert.Multiple(() =>
            {
                Assert.That(art.GroupId, Is.EqualTo(layers[index * 2].GroupId));
                Assert.That(badge.GroupId, Is.EqualTo(art.GroupId));
                Assert.That(badge.Level, Is.EqualTo(index + 1));
                Assert.That(badge.Visible, Is.EqualTo(index != 1));
                Assert.That(scale, Is.GreaterThan(0).And.LessThanOrEqualTo(1));
                Assert.That(badge.Bounds.X - art.Bounds.X, Is.EqualTo(360 * scale).Within(1e-8));
                Assert.That(badge.Bounds.Y - art.Bounds.Y, Is.EqualTo(110 * scale).Within(1e-8));
                Assert.That(badge.Bounds.Width, Is.EqualTo(128 * scale).Within(1e-8));
                Assert.That(art.Bounds.X, Is.GreaterThanOrEqualTo(0));
                Assert.That(art.Bounds.Y, Is.GreaterThanOrEqualTo(0));
                Assert.That(art.Bounds.X + art.Bounds.Width, Is.LessThanOrEqualTo(width));
                Assert.That(art.Bounds.Y + art.Bounds.Height, Is.LessThanOrEqualTo(height));
            });
        }
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(TestImages.RenderPng(edit.Document), Is.EqualTo(beforePng));
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.StateEquals(arranged), Is.True);
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p4-template-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "p4-test");
            Assert.That(NativePackageStore.Read(path).StateEquals(arranged), Is.True);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void TemplateRejectsWrongCountOrLockedGroupWithoutPartialEdits()
    {
        var group = Guid.NewGuid();
        var first = new Layer { Kind = LayerKind.Shape, GroupId = group,
            Bounds = new RectD(10, 10, 100, 100) };
        var lockedMember = new Layer { Kind = LayerKind.Shape, GroupId = group, Locked = true,
            Bounds = new RectD(110, 10, 100, 100) };
        var second = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(250, 10, 100, 100) };
        var third = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(490, 10, 100, 100) };
        var edit = new EditSession(new EditorDocument { Layers = [first, lockedMember, second, third] });
        var original = edit.Document.Copy();
        Assert.Throws<InvalidOperationException>(() =>
            edit.ApplyLayoutTemplate("three-columns", [first.Id, second.Id]));
        Assert.Throws<InvalidOperationException>(() =>
            edit.ApplyLayoutTemplate("three-columns", [first.Id, second.Id, third.Id]));
        Assert.That(edit.Document.StateEquals(original), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);

        edit.UpdateLayer(lockedMember.Id, layer => layer.Locked = false);
        edit.ApplyLayoutTemplate("three-columns", [first.Id, lockedMember.Id, second.Id, third.Id]);
        Assert.That(edit.UndoCount, Is.EqualTo(2), "two selected members of one group occupy one slot");
    }
}
