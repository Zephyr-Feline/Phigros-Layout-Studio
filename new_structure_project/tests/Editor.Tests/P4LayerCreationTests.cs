using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4LayerCreationTests
{
    [Test]
    public void ShapePresetIsSlantedAndUndoable()
    {
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1440, CanvasHeight = 1080 });
        var shape = LayerPresets.Shape(edit.Document);
        edit.AddLayer(shape);
        Assert.Multiple(() =>
        {
            Assert.That(shape.Kind, Is.EqualTo(LayerKind.Shape));
            Assert.That(shape.Slanted, Is.True);
            Assert.That(shape.Bounds, Is.EqualTo(new RectD(520, 505, 400, 300)));
            Assert.That(shape.ColorArgb, Is.EqualTo(0x96142837u));
            Assert.That(edit.SelectedLayerId, Is.EqualTo(shape.Id));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Layers, Is.Empty);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(shape.Id).Slanted, Is.True);
    }

    [Test]
    public void OriginalLabelIsOneUndoableWhitePlateAndEditableCaption()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1440, CanvasHeight = 1080 });
        edit.AddOriginalTag();
        var plate = edit.Document.Layers[0];
        var caption = edit.Document.Layers[1];
        Assert.Multiple(() =>
        {
            Assert.That(plate.Kind, Is.EqualTo(LayerKind.Shape));
            Assert.That(plate.Slanted, Is.True);
            Assert.That(plate.KeepAspect, Is.True);
            Assert.That(plate.ColorArgb, Is.EqualTo(0xFFFFFFFFu));
            Assert.That(plate.Bounds, Is.EqualTo(new RectD(600, 522, 240, 36)));
            Assert.That(caption.Kind, Is.EqualTo(LayerKind.Text));
            Assert.That(caption.Text, Is.EqualTo("Phigros Original"));
            Assert.That(caption.ColorArgb, Is.EqualTo(0xFF000000u));
            Assert.That(caption.Alignment, Is.EqualTo(HorizontalAlignment.Center));
            Assert.That(caption.VerticalAlignment, Is.EqualTo(VerticalAlignment.Center));
            Assert.That(caption.KeepAspect, Is.True);
            Assert.That(caption.Bounds.X, Is.EqualTo(613.648).Within(1e-8));
            Assert.That(caption.Bounds.Width, Is.EqualTo(212.704).Within(1e-8));
            Assert.That(caption.Bounds.Y, Is.EqualTo(522));
            Assert.That(caption.Bounds.Height, Is.EqualTo(36));
            Assert.That(plate.GroupId, Is.Not.Null.And.EqualTo(caption.GroupId));
            Assert.That(edit.SelectedLayerId, Is.EqualTo(caption.Id));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Layers, Is.Empty);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Find(caption.Id).Text, Is.EqualTo("Phigros Original"));
        var rendered = TestImages.RenderPng(edit.Document, true);
        var (_, _, pixels) = TestImages.DecodePng(rendered);
        var at = (525 * 1440 + 625) * 4;
        Assert.That(pixels[at..(at + 4)], Is.EqualTo(new byte[] { 255, 255, 255, 255 }));
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p4-original-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "p4-test");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.StateEquals(edit.Document), Is.True);
            Assert.That(TestImages.RenderPng(reopened, true), Is.EqualTo(rendered));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void SongCardUsesSelectedArtworkAndCreatesOneGroupInOneHistoryEntry()
    {
        AvaloniaTestHost.EnsureStarted();
        var id = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = id, Name = "My artwork",
            Bounds = new RectD(100, 100, 520, 250) };
        var document = new EditorDocument { CanvasWidth = 1440, CanvasHeight = 1080,
            Layers = [image] };
        document.AddAsset(id, P4AutomaticBackgroundTests.Solid(0xFFFF0000));
        var edit = new EditSession(document);
        edit.SelectedLayerId = image.Id;
        edit.CreateSongCard(image.Id);
        var layers = edit.Document.Layers;
        var group = edit.Document.Find(image.Id).GroupId;
        Assert.Multiple(() =>
        {
            Assert.That(layers.Count, Is.EqualTo(6));
            Assert.That(layers.Select(layer => layer.Kind), Is.EqualTo(new[]
                { LayerKind.Shape, LayerKind.Image, LayerKind.Text, LayerKind.Text, LayerKind.Text, LayerKind.Badge }));
            Assert.That(layers.All(layer => layer.GroupId == group), Is.True);
            Assert.That(group, Is.Not.Null);
            Assert.That(layers[0].Bounds, Is.EqualTo(new RectD(85, 340, 520, 125)));
            Assert.That(layers[0].Slanted, Is.True);
            Assert.That(layers[1].Id, Is.EqualTo(image.Id));
            Assert.That(layers[1].Slanted, Is.True);
            Assert.That(layers[1].KeepAspect, Is.True);
            Assert.That(layers[1].Name, Is.EqualTo("My artwork"));
            Assert.That(layers.Where(layer => layer.Kind == LayerKind.Text)
                .All(layer => layer.VerticalAlignment == VerticalAlignment.Center && layer.KeepAspect),
                Is.True);
            Assert.That(layers[^1].Bounds, Is.EqualTo(new RectD(519, 284, 104, 87.75)));
            Assert.That(layers[^1].Difficulty, Is.EqualTo("IN"));
            Assert.That(edit.SelectedLayerId, Is.EqualTo(image.Id));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        });
        var (_, _, pixels) = TestImages.DecodePng(TestImages.RenderPng(edit.Document, true));
        byte[] Pixel(int x, int y) => pixels[((y * 1440 + x) * 4)..][..4];
        Assert.Multiple(() =>
        {
            Assert.That(Pixel(105, 105)[3], Is.Zero, "the artwork uses its slanted corner");
            Assert.That(Pixel(300, 200)[2], Is.EqualTo(255), "the original embedded art stays visible");
            Assert.That(Pixel(100, 440)[3], Is.GreaterThan(0), "the slanted info bar extends below the artwork");
        });
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p4-song-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "p4-test");
            Assert.That(NativePackageStore.Read(path).StateEquals(edit.Document), Is.True);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        Assert.That(edit.Undo(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(1));
            Assert.That(edit.Document.Find(image.Id).Slanted, Is.False);
            Assert.That(edit.Document.Find(image.Id).KeepAspect, Is.False);
            Assert.That(edit.Document.Find(image.Id).GroupId, Is.Null);
        });
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Layers.Count, Is.EqualTo(6));
    }

    [Test]
    public void LabelJoinsSongGroupAndLayerLimitFailureLeavesHistoryAndSelectionIntact()
    {
        var id = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = id,
            Bounds = new RectD(100, 100, 520, 250) };
        var document = new EditorDocument { Layers = [image] };
        document.AddAsset(id, P4AutomaticBackgroundTests.Solid(0xFF0088AA));
        var edit = new EditSession(document);
        edit.CreateSongCard(image.Id);
        var group = edit.Document.Find(image.Id).GroupId;
        edit.AddOriginalTag(image.Id);
        var plate = edit.Document.Layers[^2];
        var caption = edit.Document.Layers[^1];
        Assert.Multiple(() =>
        {
            Assert.That(plate.GroupId, Is.EqualTo(group));
            Assert.That(caption.GroupId, Is.EqualTo(group));
            Assert.That(plate.Bounds, Is.EqualTo(new RectD(380, 465, 240, 36)));
            Assert.That(caption.Text, Is.EqualTo("Phigros Original"));
            Assert.That(edit.UndoCount, Is.EqualTo(2));
        });

        var full = new EditorDocument { Layers = Enumerable.Range(0, 195)
            .Select(index => new Layer { Kind = LayerKind.Shape, Name = $"Fill {index}" }).ToList() };
        full.Layers.Add(image.Copy());
        full.AddAsset(id, P4AutomaticBackgroundTests.Solid(0xFF0088AA));
        var limited = new EditSession(full);
        limited.SelectedLayerId = image.Id;
        var before = limited.Document.Copy();
        Assert.Throws<InvalidDataException>(() => limited.CreateSongCard(image.Id));
        Assert.Multiple(() =>
        {
            Assert.That(limited.Document.StateEquals(before), Is.True);
            Assert.That(limited.SelectedLayerId, Is.EqualTo(image.Id));
            Assert.That(limited.UndoCount, Is.Zero);
        });
        var almostFull = new EditSession(new EditorDocument { Layers = Enumerable.Range(0, 199)
            .Select(index => new Layer { Kind = LayerKind.Shape }).ToList() });
        var last = almostFull.Document.Layers[0].Id;
        almostFull.SelectedLayerId = last;
        var original = almostFull.Document.Copy();
        Assert.Throws<InvalidDataException>(() => almostFull.AddOriginalTag());
        Assert.That(almostFull.Document.StateEquals(original), Is.True);
        Assert.That(almostFull.SelectedLayerId, Is.EqualTo(last));
        Assert.That(almostFull.UndoCount, Is.Zero);
    }

    [Test]
    public void DesktopCreationButtonsUseTheSameSingleStepTransactions()
    {
        AvaloniaTestHost.EnsureStarted();
        var id = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = id,
            Bounds = new RectD(100, 100, 520, 250) };
        var document = new EditorDocument { Layers = [image] };
        document.AddAsset(id, P4AutomaticBackgroundTests.Solid(0xFF0088AA));
        var edit = new EditSession(document);
        edit.SelectedLayerId = image.Id;
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            Control Named(string name) => UiTestActions.FindNamed(window, name);
            UiTestActions.Click(Named("CreateSongCard"));
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(6));
            UiTestActions.Click(Named("AddOriginalTag"));
            Assert.That(edit.Document.Layers.Count, Is.EqualTo(8));
            UiTestActions.Click(Named("AddShape"));
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Layers.Count, Is.EqualTo(9));
                Assert.That(edit.UndoCount, Is.EqualTo(3));
                Assert.That(edit.Document.Layers[^1].Kind, Is.EqualTo(LayerKind.Shape));
            });
        }
        finally { window.Close(); }
    }

    [Test]
    public void TagSelectionAndSongGroupVisibilityUseOneHistoryEntryPerAction()
    {
        AvaloniaTestHost.EnsureStarted();
        var id = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = id,
            Bounds = new RectD(100, 100, 520, 250) };
        var document = new EditorDocument { Layers = [image] };
        document.AddAsset(id, P4AutomaticBackgroundTests.Solid(0xFF0088AA));
        var edit = new EditSession(document);
        edit.CreateSongCard(image.Id);
        edit.AddOriginalTag(image.Id);
        var plate = edit.Document.Layers[^2];
        var caption = edit.Document.Layers[^1];
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            Control Named(string name) => UiTestActions.FindNamed(window, name);
            var before = edit.UndoCount;
            UiTestActions.Click(Named("ToggleSelectedVisibility"));
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(plate.Id).Visible, Is.False);
                Assert.That(edit.Document.Find(caption.Id).Visible, Is.False);
                Assert.That(edit.Document.Find(image.Id).Visible, Is.True);
                Assert.That(edit.UndoCount, Is.EqualTo(before + 1));
            });
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(plate.Id).Visible, Is.True);
            Assert.That(edit.Document.Find(caption.Id).Visible, Is.True);

            before = edit.UndoCount;
            UiTestActions.Click(Named("ToggleSelectedLock"));
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(plate.Id).Locked, Is.True);
                Assert.That(edit.Document.Find(caption.Id).Locked, Is.True);
                Assert.That(edit.UndoCount, Is.EqualTo(before + 1));
            });
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Find(plate.Id).Locked, Is.False);
            Assert.That(edit.Document.Find(caption.Id).Locked, Is.False);

            edit.SelectedLayerId = caption.Id;
            before = edit.UndoCount;
            UiTestActions.Click(Named("HideGroup"));
            Assert.That(edit.Document.Layers.All(layer => !layer.Visible), Is.True);
            Assert.That(edit.UndoCount, Is.EqualTo(before + 1));
            UiTestActions.Click(Named("ShowGroup"));
            Assert.That(edit.Document.Layers.All(layer => layer.Visible), Is.True);
            Assert.That(edit.UndoCount, Is.EqualTo(before + 2));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Layers.All(layer => !layer.Visible), Is.True);
            Assert.That(edit.Redo(), Is.True);
            Assert.That(edit.Document.Layers.All(layer => layer.Visible), Is.True);
        }
        finally { window.Close(); }
    }

    [Test]
    public void BuildingFromHiddenArtworkKeepsTheWholeNewCompositionHidden()
    {
        AvaloniaTestHost.EnsureStarted();
        var id = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = id, Visible = false,
            Bounds = new RectD(100, 100, 520, 250) };
        var document = new EditorDocument { CanvasWidth = 960, CanvasHeight = 540,
            Layers = [image] };
        document.AddAsset(id, P4AutomaticBackgroundTests.Solid(0xFF0088AA));
        var edit = new EditSession(document);
        edit.CreateSongCard(image.Id);
        edit.AddOriginalTag(image.Id);
        Assert.That(edit.Document.Layers.Count, Is.EqualTo(8));
        Assert.That(edit.Document.Layers.All(layer => !layer.Visible), Is.True);
        var (_, _, transparent) = TestImages.DecodePng(TestImages.RenderPng(edit.Document, true));
        Assert.That(transparent.Where((_, index) => index % 4 == 3).All(alpha => alpha == 0), Is.True);
    }
}
