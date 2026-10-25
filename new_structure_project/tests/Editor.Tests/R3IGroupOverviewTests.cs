using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3IGroupOverviewTests
{
    [Test]
    public void GroupOverviewSelectsWholeGroupsAndShowsMemberSummary()
    {
        var edit = Session(out var first, out var second, out _);
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var list = ShowGroupOverview(window);
            Assert.That(list.Children.OfType<Expander>().Count(), Is.EqualTo(2));
            var songHeader = GroupHeader(list, "Song A");
            Assert.That(songHeader.Content?.ToString(), Does.Contain("4 层")
                .And.Contain("隐藏 1").And.Contain("锁定 1"));
            var card = songHeader.GetLogicalAncestors().OfType<Expander>().First();
            card.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.That(card.GetLogicalDescendants().OfType<Button>()
                .Count(button => button.Name == "GroupOverviewMember"), Is.EqualTo(4));
            UiTestActions.Click(songHeader);
            Dispatcher.UIThread.RunJobs();
            UiTestActions.ClickNamed(window, "SelectGroupOverview");
            Assert.That(edit.SelectedLayerIds, Is.EquivalentTo(first));
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
        }
    }

    [Test]
    public void GroupVisibilityAndLockActionsAreSingleUndoableEdits()
    {
        var edit = Session(out _, out var members, out var unrelated);
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var list = ShowGroupOverview(window);
            UiTestActions.Click(GroupHeader(list, "编组 B"));
            Dispatcher.UIThread.RunJobs();
            UiTestActions.ClickNamed(window, "ToggleGroupOverviewVisibility");
            Assert.Multiple(() =>
            {
                Assert.That(members.All(id => !edit.Document.Find(id).Visible), Is.True);
                Assert.That(edit.Document.Find(unrelated.Id).Visible, Is.True);
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            Assert.That(edit.Undo(), Is.True);
            UiTestActions.ClickNamed(window, "ToggleGroupOverviewLock");
            Assert.Multiple(() =>
            {
                Assert.That(members.All(id => edit.Document.Find(id).Locked), Is.True);
                Assert.That(edit.Document.Find(unrelated.Id).Locked, Is.False);
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            Assert.That(edit.Undo(), Is.True);
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
        }
    }

    [Test]
    public void UngroupFromOverviewIsUndoableAndRemovesTheGroupRow()
    {
        var edit = Session(out _, out var second, out _);
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var list = ShowGroupOverview(window);
            UiTestActions.Click(GroupHeader(list, "编组 B"));
            Dispatcher.UIThread.RunJobs();
            UiTestActions.ClickNamed(window, "UngroupGroupOverview");
            Assert.Multiple(() =>
            {
                Assert.That(second.All(id => edit.Document.Find(id).GroupId is null), Is.True);
                Assert.That(list.Children.OfType<Expander>().Count(), Is.EqualTo(1));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(second[0]).GroupId, Is.Not.Null);
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
        }
    }

    [Test]
    public void ExpandedGroupMemberInlineVisibilityAndLockAreSingleUndoEdits()
    {
        var edit = Session(out var first, out _, out _);
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var cards = ShowGroupOverview(window);
            var card = cards.Children.OfType<Expander>().First(item =>
                item.GetLogicalDescendants().OfType<Button>().Any(button =>
                    button.Name == "GroupOverviewHeader" &&
                    button.Content?.ToString()!.Contains("Song A", StringComparison.Ordinal) == true));
            card.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            var member = card.GetLogicalDescendants().OfType<Button>().First(button =>
                button.Name == "GroupOverviewMember" &&
                button.Content?.ToString()!.Contains("Charter A", StringComparison.Ordinal) == true);
            var row = member.GetLogicalAncestors().OfType<Grid>().First();
            var targetId = first[3];
            UiTestActions.Click(row.Children.OfType<Button>()
                .Single(button => button.Name == "GroupMemberVisibility"));
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(targetId).Visible, Is.False);
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            Assert.That(edit.Undo(), Is.True);
            var rowAfterUndo = card.GetLogicalDescendants().OfType<Button>().First(button =>
                button.Name == "GroupOverviewMember" &&
                button.Content?.ToString()!.Contains("Charter A", StringComparison.Ordinal) == true)
                .GetLogicalAncestors().OfType<Grid>().First();
            UiTestActions.Click(rowAfterUndo.Children.OfType<Button>()
                .Single(button => button.Name == "GroupMemberLock"));
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(targetId).Locked, Is.False);
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(targetId).Locked, Is.True);
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
        }
    }

    private static Button GroupHeader(StackPanel cards, string text) =>
        cards.GetLogicalDescendants().OfType<Button>().First(button =>
            button.Name == "GroupOverviewHeader" &&
            button.Content?.ToString()!.Contains(text, StringComparison.Ordinal) == true);

    private static StackPanel ShowGroupOverview(MainWindow window)
    {
        var expander = window.GetLogicalDescendants().OfType<Expander>()
            .Single(item => item.Name == "GroupOverview");
        expander.IsExpanded = true;
        window.UpdateLayout();
        return window.GetLogicalDescendants().OfType<StackPanel>()
            .Single(item => item.Name == "GroupOverviewCards");
    }

    private static EditSession Session(out Guid[] first, out Guid[] second,
        out Layer unrelated)
    {
        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();
        var layers = new List<Layer>
        {
            Text("Title A", "Song A", groupA, SongRole.Title),
            Image("Artwork A", groupA, SongRole.Artwork),
            Image("Hidden A", groupA, SongRole.None, visible: false),
            Text("Charter A", "charter", groupA, SongRole.Charter, locked: true),
            Shape("编组 B 形状一", groupB),
            Shape("编组 B 形状二", groupB)
        };
        first = layers.Take(4).Select(layer => layer.Id).ToArray();
        second = layers.Skip(4).Select(layer => layer.Id).ToArray();
        unrelated = Shape("独立图层", null);
        layers.Add(unrelated);
        var document = new EditorDocument
        {
            CanvasWidth = 1000, CanvasHeight = 600, Layers = layers
        };
        foreach (var assetId in layers.Where(layer => layer.AssetId is not null)
            .Select(layer => layer.AssetId!.Value))
            document.AddAsset(assetId, TestImages.EncodePng(8, 6,
                TestImages.SyntheticBgra(8, 6)));
        return new EditSession(document);
    }

    private static Layer Text(string name, string text, Guid? group, SongRole role,
        bool locked = false) => new()
    {
        Kind = LayerKind.Text, Name = name, Text = text, GroupId = group, SongRole = role,
        Locked = locked, Bounds = new RectD(100, 100, 300, 80), FontSize = 30
    };

    private static Layer Image(string name, Guid? group, SongRole role,
        bool visible = true) => new()
    {
        Kind = LayerKind.Image, Name = name, GroupId = group, SongRole = role,
        Visible = visible, Bounds = new RectD(100, 200, 200, 160),
        AssetId = Guid.NewGuid()
    };

    private static Layer Shape(string name, Guid? group) => new()
    {
        Kind = LayerKind.Shape, Name = name, GroupId = group,
        Bounds = new RectD(400, 100, 120, 80)
    };
}
