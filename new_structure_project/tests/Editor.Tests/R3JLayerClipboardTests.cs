using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3JLayerClipboardTests
{
    [Test]
    public void LayerListKeyboardCopyAndPasteUseTheSameClipboard()
    {
        AvaloniaTestHost.EnsureStarted();
        var sourceLayer = new Layer
        {
            Kind = LayerKind.Shape, Name = "keyboard source",
            Bounds = new RectD(20, 20, 80, 60)
        };
        var source = new EditSession(new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [sourceLayer]
        });
        source.SelectedLayerId = sourceLayer.Id;
        var clipboard = new LayerClipboard();
        var sourceWindow = new MainWindow(source, layerClipboard: clipboard);
        var target = new EditSession(new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 });
        var targetWindow = new MainWindow(target, layerClipboard: clipboard);
        try
        {
            sourceWindow.Show();
            targetWindow.Show();
            var sourceList = sourceWindow.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "LayerList");
            sourceList.Focus();
            sourceWindow.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(clipboard.LayerCount, Is.EqualTo(1));
            var targetList = targetWindow.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "LayerList");
            targetList.Focus();
            targetWindow.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(target.Document.Layers, Has.Count.EqualTo(1));
                Assert.That(target.UndoCount, Is.EqualTo(1));
                Assert.That(target.SelectedLayerIds, Has.Count.EqualTo(1));
            });
        }
        finally
        {
            source.MarkSaved();
            target.MarkSaved();
            sourceWindow.Close();
            targetWindow.Close();
        }
    }

    [Test]
    public void ClipboardSnapshotIsDetachedAndPasteRemapsIdsGroupsAndAssets()
    {
        var group = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var source = new EditorDocument { CanvasWidth = 900, CanvasHeight = 600 };
        source.AddAsset(asset, TestImages.EncodePng(8, 6,
            TestImages.SyntheticBgra(8, 6)));
        var image = new Layer
        {
            Kind = LayerKind.Image, Name = "artwork", GroupId = group, AssetId = asset,
            SongRole = SongRole.Artwork, Bounds = new RectD(100, 120, 240, 180)
        };
        var title = new Layer
        {
            Kind = LayerKind.Text, Name = "title", Text = "Song", GroupId = group,
            SongRole = SongRole.Title, Bounds = new RectD(100, 40, 240, 60), FontSize = 30
        };
        source.Layers.AddRange([image, title]);
        source.Validate();
        var clipboard = new LayerClipboard();
        Assert.That(clipboard.Copy(source, [image.Id, title.Id], includeGroups: true),
            Is.EqualTo(2));
        source.Layers.Clear(); // the clipboard must not depend on the source session

        var target = new EditSession(new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 });
        var pasted = clipboard.Paste(target);
        Assert.Multiple(() =>
        {
            Assert.That(pasted, Has.Count.EqualTo(2));
            Assert.That(pasted, Is.Unique);
            Assert.That(pasted, Has.None.EqualTo(image.Id));
            Assert.That(target.Document.Layers, Has.Count.EqualTo(2));
            Assert.That(target.SelectedLayerIds, Is.EquivalentTo(pasted));
            Assert.That(target.UndoCount, Is.EqualTo(1));
            var pastedImage = target.Document.Layers.Single(layer => layer.Kind == LayerKind.Image);
            var pastedTitle = target.Document.Layers.Single(layer => layer.Kind == LayerKind.Text);
            Assert.That(pastedImage.SongRole, Is.EqualTo(SongRole.Artwork));
            Assert.That(pastedTitle.SongRole, Is.EqualTo(SongRole.Title));
            Assert.That(pastedImage.Bounds, Is.EqualTo(image.Bounds));
            Assert.That(pastedImage.GroupId, Is.Not.Null.And.Not.EqualTo(group));
            Assert.That(pastedImage.GroupId, Is.EqualTo(pastedTitle.GroupId));
            Assert.That(target.Document.Assets[pastedImage.AssetId!.Value].Sha256,
                Is.EqualTo(new AssetBlob(sourceImageBytes()).Sha256));
        });
        Assert.That(target.Undo(), Is.True);
        Assert.That(target.Document.Layers, Is.Empty);
        Assert.That(target.Redo(), Is.True);
        Assert.That(target.Document.Layers, Has.Count.EqualTo(2));

        static byte[] sourceImageBytes() => TestImages.EncodePng(8, 6,
            TestImages.SyntheticBgra(8, 6));
    }

    [Test]
    public void PasteRespectsTheTwoHundredLayerLimitWithoutPartialChanges()
    {
        var source = new EditorDocument
        {
            CanvasWidth = 900, CanvasHeight = 600,
            Layers =
            [
                new Layer { Kind = LayerKind.Shape, Name = "a", Bounds = new RectD(10, 10, 50, 50) },
                new Layer { Kind = LayerKind.Shape, Name = "b", Bounds = new RectD(80, 10, 50, 50) }
            ]
        };
        var clipboard = new LayerClipboard();
        clipboard.Copy(source, source.Layers.Select(layer => layer.Id), includeGroups: false);
        var targetLayers = Enumerable.Range(0, 199).Select(index => new Layer
        {
            Kind = LayerKind.Shape, Name = $"block {index}",
            Bounds = new RectD(index % 20 * 20, index / 20 * 20, 10, 10)
        }).ToList();
        var target = new EditSession(new EditorDocument
        {
            CanvasWidth = 900, CanvasHeight = 600, Layers = targetLayers
        });
        var before = target.Document.Copy();
        Assert.That(() => clipboard.Paste(target), Throws.InstanceOf<InvalidDataException>());
        Assert.Multiple(() =>
        {
            Assert.That(target.Document.StateEquals(before), Is.True);
            Assert.That(target.UndoCount, Is.Zero);
            Assert.That(target.SelectedLayerIds, Is.Empty);
        });
    }

    [Test]
    public void DesktopClipboardCopiesAndPastesAcrossProjectSessions()
    {
        AvaloniaTestHost.EnsureStarted();
        var group = Guid.NewGuid();
        var sourceEdit = new EditSession(new EditorDocument
        {
            CanvasWidth = 900, CanvasHeight = 600,
            Layers =
            [
                new Layer { Kind = LayerKind.Shape, Name = "one", GroupId = group,
                    Bounds = new RectD(100, 100, 100, 100) },
                new Layer { Kind = LayerKind.Text, Name = "two", GroupId = group,
                    Bounds = new RectD(220, 100, 200, 80), FontSize = 30 }
            ]
        });
        sourceEdit.SetSelection(sourceEdit.Document.Layers.Select(layer => layer.Id));
        var clipboard = new LayerClipboard();
        var sourceWindow = new MainWindow(sourceEdit, layerClipboard: clipboard);
        var targetEdit = new EditSession(new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 });
        var targetWindow = new MainWindow(targetEdit, layerClipboard: clipboard);
        try
        {
            sourceWindow.Show();
            targetWindow.Show();
            UiTestActions.ClickNamed(sourceWindow, "CopySelectedLayers");
            Assert.That(clipboard.LayerCount, Is.EqualTo(2));
            Assert.That(sourceEdit.UndoCount, Is.Zero);
            UiTestActions.ClickNamed(targetWindow, "PasteLayers");
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(targetEdit.Document.Layers, Has.Count.EqualTo(2));
                Assert.That(targetEdit.SelectedLayerIds, Has.Count.EqualTo(2));
                Assert.That(targetEdit.UndoCount, Is.EqualTo(1));
                Assert.That(targetEdit.Document.Layers.Select(layer => layer.GroupId).Distinct().Count(),
                    Is.EqualTo(1));
                Assert.That(UiTestActions.Status(targetWindow), Does.Contain("粘贴"));
            });
            UiTestActions.ClickNamed(targetWindow, "PasteLayers");
            Dispatcher.UIThread.RunJobs();
            Assert.That(targetEdit.Document.Layers, Has.Count.EqualTo(4));
            Assert.That(targetEdit.UndoCount, Is.EqualTo(2));
        }
        finally
        {
            sourceEdit.MarkSaved();
            targetEdit.MarkSaved();
            sourceWindow.Close();
            targetWindow.Close();
        }
    }
}
