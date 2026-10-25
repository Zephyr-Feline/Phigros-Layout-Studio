using System.IO.Compression;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>
/// REQ-HIST-003 (bounded history and lifecycle) and REQ-STORAGE-008 (history must not copy image
/// payloads). The policy is asserted as a documented constant instead of a magic number.
/// </summary>
public sealed class HistoryPolicyAndAssetLifecycleTests
{
    private string _directory = "";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p2-history",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void HistoryDepthPolicyIsExplicitAndConfigurable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EditSession.DefaultMaxHistoryEntries, Is.EqualTo(128));
            Assert.That(new EditSession(new EditorDocument()).MaxHistoryEntries,
                Is.EqualTo(EditSession.DefaultMaxHistoryEntries));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EditSession(new EditorDocument(), 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EditSession(new EditorDocument(), -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EditSession(new EditorDocument(), 10001));
            Assert.That(new EditSession(new EditorDocument(), 1).MaxHistoryEntries, Is.EqualTo(1));
            Assert.That(new EditSession(new EditorDocument(), EditSession.MaxAllowedHistoryEntries)
                .MaxHistoryEntries, Is.EqualTo(EditSession.MaxAllowedHistoryEntries));
        });
    }

    [Test]
    public void EvictedSavepointNeverReportsCleanWhileTheContentDiffers()
    {
        var edit = new EditSession(new EditorDocument(), maxHistoryEntries: 4);
        var layer = new Layer { Kind = LayerKind.Text, Text = "0" };
        edit.AddLayer(layer);
        edit.MarkSaved();
        var saved = edit.Document.Copy();
        for (var i = 1; i <= 12; i++) edit.UpdateLayer(layer.Id, item => item.Text = i.ToString());
        Assert.That(edit.IsDirty, Is.True);

        var steps = 0;
        while (edit.Undo())
        {
            steps++;
            if (edit.IsDirty) continue;
            Assert.That(edit.Document.StateEquals(saved), Is.True,
                "a clean state must be the saved revision");
        }

        Assert.Multiple(() =>
        {
            Assert.That(steps, Is.EqualTo(4), "history depth bounds the reachable states");
            // The saved revision was evicted, so the oldest reachable state is still not saved.
            Assert.That(edit.IsDirty, Is.True);
            Assert.That(edit.Document.StateEquals(saved), Is.False);
        });

        while (edit.Redo()) { }
        Assert.That(edit.IsDirty, Is.True);
    }

    [Test]
    public void ImageHeavyHistoryKeepsOnePayloadAndOneStoredAsset()
    {
        var payload = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(payload);
        var edit = new EditSession(new EditorDocument(), EditSession.DefaultMaxHistoryEntries);
        var assetId = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = assetId, Bounds = new RectD(0, 0, 100, 100) };
        edit.AddImageLayer(image, payload);
        var blob = edit.Document.Assets[assetId];

        for (var i = 0; i < 200; i++)
        {
            edit.BeginGesture(image.Id);
            edit.MoveBy(i + 1, -i);
            edit.CommitGesture();
            Assert.That(edit.Document.Assets[assetId], Is.SameAs(blob));
        }

        Assert.That(edit.UndoCount, Is.EqualTo(EditSession.DefaultMaxHistoryEntries));
        var walked = 0;
        while (edit.Undo())
        {
            walked++;
            Assert.That(edit.Document.Assets[assetId], Is.SameAs(blob),
                "every retained snapshot must share the same immutable payload");
        }
        Assert.That(walked, Is.EqualTo(EditSession.DefaultMaxHistoryEntries));
        while (edit.Redo())
            Assert.That(edit.Document.Assets[assetId], Is.SameAs(blob));

        var path = Path.Combine(_directory, "image-heavy.ppgproj");
        NativePackageStore.Save(path, edit.Document, "p2-test");
        using (var zip = ZipFile.OpenRead(path))
            Assert.That(zip.Entries.Count(entry => entry.FullName.StartsWith("assets/", StringComparison.Ordinal)),
                Is.EqualTo(1));
        Assert.That(NativePackageStore.Read(path).Assets, Has.Count.EqualTo(1));
    }

    [Test]
    public void AssetLifecycleSurvivesHistoryEviction()
    {
        var edit = new EditSession(new EditorDocument(), maxHistoryEntries: 6);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        edit.AddImageLayer(new Layer { Kind = LayerKind.Image, AssetId = firstId, Name = "first" }, [1, 2, 3]);
        edit.AddImageLayer(new Layer { Kind = LayerKind.Image, AssetId = secondId, Name = "second" }, [4, 5, 6]);
        var firstLayer = edit.Document.Layers[0].Id;
        var secondLayer = edit.Document.Layers[1].Id;
        var firstBlob = edit.Document.Assets[firstId];
        var secondBlob = edit.Document.Assets[secondId];

        edit.DeleteLayer(firstLayer);
        Assert.That(edit.Document.Assets.ContainsKey(firstId), Is.False,
            "a removed layer must not keep an unreferenced resource in the document");
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Assets[firstId], Is.SameAs(firstBlob),
            "undo must restore the resource with the layer");

        for (var i = 0; i < 10; i++)
        {
            edit.BeginGesture(secondLayer);
            edit.MoveBy(1, 1);
            edit.CommitGesture();
        }

        Assert.That(edit.UndoCount, Is.EqualTo(6));
        for (var i = 0; i < 6; i++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Assets[firstId], Is.SameAs(firstBlob));
                Assert.That(edit.Document.Assets[secondId], Is.SameAs(secondBlob));
            });
            Assert.That(edit.Undo(), Is.True);
        }

        var path = Path.Combine(_directory, "lifecycle.ppgproj");
        NativePackageStore.Save(path, edit.Document, "p2-test");
        var reloaded = NativePackageStore.Read(path);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Assets.Keys, Is.EquivalentTo(new[] { firstId, secondId }));
            Assert.That(reloaded.Layers.Select(layer => layer.Id),
                Is.EquivalentTo(new[] { firstLayer, secondLayer }));
        });
    }
}
