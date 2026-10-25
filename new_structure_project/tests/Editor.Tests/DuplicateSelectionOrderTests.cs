using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class DuplicateSelectionOrderTests
{
    [TestCase("4,1")]
    [TestCase("3,0,4,1")]
    [TestCase("4,1,4,1")]
    public void DuplicationMapsOrderedSelectionWhileKeepingPaintOrderAndIndependentGroups(string indices)
    {
        var groupA = Guid.NewGuid(); var groupB = Guid.NewGuid();
        var badge = EditorDocument.NewBadge(new(1100, 200)); badge.Name = "B badge"; badge.GroupId = groupB; badge.Level = 17;
        var originals = new[]
        {
            new Layer { Kind = LayerKind.Shape, Name = "A plate", GroupId = groupA, Slanted = true,
                Bounds = new(100.12345678912345, 100, 100, 80) },
            new Layer { Kind = LayerKind.Text, Name = "A title", GroupId = groupA, Text = "A", FontSize = 31,
                Bounds = new(250, 120, 180, 90) },
            new Layer { Kind = LayerKind.Shape, Name = "Independent", Bounds = new(500, 200, 100, 100) },
            new Layer { Kind = LayerKind.Text, Name = "B title", GroupId = groupB, Text = "B", FontSize = 52,
                Visible = false, Locked = true, RotationDegrees = 23, Bounds = new(800, 150, 200, 100) },
            badge
        };
        var edit = new EditSession(new EditorDocument { Layers = originals.ToList() });
        var ids = indices.Split(',').Select(value => originals[int.Parse(value)].Id).ToArray();
        edit.SetSelection(ids);
        var selection = edit.SelectedLayerIds.ToArray(); var before = edit.Document.Copy();

        edit.DuplicateLayers(ids);

        var appended = edit.Document.Layers.Skip(originals.Length).ToArray();
        var expectedPaintOrder = before.Layers.Where(layer => selection.Contains(layer.Id)).ToArray();
        Assert.That(appended.Select(layer => layer.Name), Is.EqualTo(expectedPaintOrder.Select(layer => layer.Name)));
        var copies = expectedPaintOrder.ToDictionary(layer => layer.Id,
            layer => appended.Single(copy => copy.Name == layer.Name));
        Assert.Multiple(() =>
        {
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection.Select(id => copies[id].Id)),
                "Copy selection follows the original ordered IDs, independently of paint order.");
            Assert.That(edit.SelectedLayerId, Is.EqualTo(copies[selection[0]].Id),
                "The primary layer must map to its own copy.");
            Assert.That(appended.Length, Is.EqualTo(selection.Length), "Repeated input IDs create one copy each.");
            Assert.That(edit.Document.Layers.Select(layer => layer.Id), Is.Unique);
            Assert.That(edit.Document.Layers.Take(originals.Length).Zip(before.Layers)
                .All(pair => pair.First.StateEquals(pair.Second)), Is.True);
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.RedoCount, Is.Zero);
        });
        var newGroups = new Dictionary<Guid, Guid>();
        foreach (var original in expectedPaintOrder)
        {
            var copy = copies[original.Id];
            var expected = original.Copy(); expected.Id = copy.Id;
            expected.Bounds = expected.Bounds with { X = expected.Bounds.X + 24, Y = expected.Bounds.Y + 24 };
            if (original.GroupId is Guid group)
            {
                Assert.That(copy.GroupId, Is.Not.Null.And.Not.EqualTo(group));
                if (newGroups.TryGetValue(group, out var replacement)) Assert.That(copy.GroupId, Is.EqualTo(replacement));
                else newGroups[group] = copy.GroupId!.Value;
            }
            expected.GroupId = copy.GroupId;
            Assert.That(copy.StateEquals(expected), Is.True, original.Name + " retains its content, flags and geometry.");
        }
        Assert.That(newGroups.Values, Is.Unique);
        var duplicated = edit.Document.Copy();
        Assert.That(edit.Undo(), Is.True); Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.Redo(), Is.True); Assert.That(edit.Document.StateEquals(duplicated), Is.True,
            "Redo restores the same copy identities, grouping and stacking order.");
    }

    [TestCase(19990d, 400d, 10d, 24d)]
    [TestCase(400d, 19990d, 24d, 10d)]
    [TestCase(20000d, 19990d, 0d, 10d)]
    public void DuplicationNearPositionLimitsUsesOneSharedOffsetWithoutTearingTheGroup(
        double x, double y, double expectedDx, double expectedDy)
    {
        var group = Guid.NewGuid();
        var first = new Layer { Kind = LayerKind.Shape, Name = "First", GroupId = group,
            RotationDegrees = 25, Bounds = new(x, y, 100, 80) };
        var second = new Layer { Kind = LayerKind.Text, Name = "Second", GroupId = group,
            Text = "Grouped text", FontSize = 37, Locked = true, Visible = false,
            RotationDegrees = -35, Bounds = new(x - 40, y - 30, 160, 100) };
        var edit = new EditSession(new EditorDocument { Layers = [first, second] });
        edit.SetSelection([first.Id, second.Id]); var before = edit.Document.Copy();

        edit.DuplicateLayers(edit.SelectedLayerIds);

        var copies = edit.Document.Layers.Skip(2).ToArray();
        foreach (var (original, copy) in new[] { first, second }.Zip(copies))
        {
            Assert.That(copy.Bounds, Is.EqualTo(original.Bounds with
                { X = original.Bounds.X + expectedDx, Y = original.Bounds.Y + expectedDy }), original.Name);
            Assert.That(copy.RotationDegrees, Is.EqualTo(original.RotationDegrees));
            Assert.That(copy.Locked, Is.EqualTo(original.Locked));
            Assert.That(copy.Visible, Is.EqualTo(original.Visible));
        }
        Assert.Multiple(() =>
        {
            Assert.That(copies[0].Bounds.X - copies[1].Bounds.X, Is.EqualTo(40));
            Assert.That(copies[0].Bounds.Y - copies[1].Bounds.Y, Is.EqualTo(30));
            Assert.That(copies[0].GroupId, Is.Not.Null.And.Not.EqualTo(group));
            Assert.That(copies[1].GroupId, Is.EqualTo(copies[0].GroupId));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.Document.Layers.Take(2).Zip(before.Layers).All(pair => pair.First.StateEquals(pair.Second)), Is.True);
        });
        var after = edit.Document.Copy();
        edit.Undo(); Assert.That(edit.Document.StateEquals(before), Is.True);
        edit.Redo(); Assert.That(edit.Document.StateEquals(after), Is.True);
    }

    [Test]
    public void ListDuplicateKeepsThePrimaryPropertyContextAndMixedVisibilityToggleTarget()
    {
        AvaloniaTestHost.EnsureStarted(); var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var back = new Layer { Kind = LayerKind.Text, Name = "Hidden A", Text = "A content", FontSize = 23,
            Visible = false, Bounds = new(100, 100, 300, 100) };
        var middle = new Layer { Kind = LayerKind.Shape, Name = "Unselected middle" };
        var front = new Layer { Kind = LayerKind.Text, Name = "Primary B", Text = "B content", FontSize = 57,
            Bounds = new(600, 100, 300, 100) };
        var edit = new EditSession(new EditorDocument { Layers = [back, middle, front] });
        var window = new MainWindow(edit);
        try
        {
            window.Show(); var list = window.GetVisualDescendants().OfType<ListBox>().Single();
            ClickRow("Primary B"); ClickRow("Hidden A", RawInputModifiers.Control);
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { front.Id, back.Id }));
            Assert.That(Field("LayerName").Text, Is.EqualTo(front.Name));

            window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();

            var copyA = edit.Document.Layers[3]; var copyB = edit.Document.Layers[4];
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { copyB.Id, copyA.Id }));
            Assert.That(edit.SelectedLayerId, Is.EqualTo(copyB.Id));
            Assert.That(Field("LayerName").Text, Is.EqualTo(front.Name));
            Assert.That(Field("TextContent").Text, Is.EqualTo(front.Text));
            Assert.That(Field("LayerFontSize").Text, Is.EqualTo("57"));
            Assert.That(list.IsKeyboardFocusWithin, Is.True);
            Assert.That(list.SelectedItems!.Cast<object>().Select(item => item.ToString()!.Split('：')[^1]),
                Is.EqualTo(new[] { front.Name, back.Name }));

            UiTestActions.Click(window, "Show / hide");
            Assert.That(edit.Document.Find(copyB.Id).Visible, Is.False,
                "The original primary was visible, so the mixed copy selection must become hidden.");
            Assert.That(edit.Document.Find(copyA.Id).Visible, Is.False);
            Assert.That(edit.Document.Find(front.Id).Visible, Is.True);
            Assert.That(edit.Document.Find(back.Id).Visible, Is.False);
            Assert.That(edit.UndoCount, Is.EqualTo(2));
            UiTestActions.Click(window, "Undo");
            Assert.That(edit.Document.Find(copyB.Id).Visible, Is.True);
            Assert.That(edit.Document.Find(copyA.Id).Visible, Is.False);
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { copyB.Id, copyA.Id }));
            Assert.That(Field("LayerName").Text, Is.EqualTo(front.Name));

            TextBox Field(string name) => window.GetLogicalDescendants().OfType<TextBox>().Single(field => field.Name == name);
            void ClickRow(string name, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                var row = list.GetVisualDescendants().OfType<ListBoxItem>().Single(item =>
                    item.Content?.ToString()?.EndsWith("：" + name, StringComparison.Ordinal) == true);
                var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left, modifiers); window.MouseUp(point, MouseButton.Left, modifiers);
            }
        }
        finally { edit.MarkSaved(); window.Close(); Avalonia.Application.Current!.Styles.Remove(theme); }
    }
}
