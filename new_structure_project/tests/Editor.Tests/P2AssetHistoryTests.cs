using Editor.Application;
using Editor.Core;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P2AssetHistoryTests
{
    [Test]
    public void AssetOwnsInputAndSnapshotsShareImmutablePayload()
    {
        var input = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)i).ToArray();
        var document = new EditorDocument();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var blob = document.AddAsset(first, input);
        input[0] ^= 0xff;
        Assert.That(blob.ToArray()[0], Is.EqualTo((byte)0));
        Assert.That(blob.OpenRead().CanWrite, Is.False);
        var returned = blob.ToArray();
        returned[0] = 99;
        Assert.That(blob.ToArray()[0], Is.EqualTo((byte)0));

        document.AddAsset(second, blob.ToArray());
        var snapshot = document.Copy();
        Assert.Multiple(() =>
        {
            Assert.That(ReferenceEquals(document.Assets[first], document.Assets[second]), Is.True);
            Assert.That(ReferenceEquals(snapshot.Assets[first], blob), Is.True);
            Assert.That(ReferenceEquals(snapshot.Assets, document.Assets), Is.False);
        });
        document.Assets.Remove(first);
        Assert.That(snapshot.Assets[first], Is.SameAs(blob));
    }

    [Test]
    public void ImageHistorySharesOnePayloadAcrossGesturesPropertiesAndBranching()
    {
        var edit = new EditSession(new EditorDocument());
        var assetId = Guid.NewGuid();
        var image = new Layer { Kind = LayerKind.Image, AssetId = assetId,
            Bounds = new RectD(1, 2, 100, 60) };
        edit.AddImageLayer(image, Enumerable.Repeat((byte)42, 2 * 1024 * 1024).ToArray());
        var blob = edit.Document.Assets[assetId];

        for (var i = 0; i < 100; i++)
        {
            edit.BeginGesture(image.Id);
            edit.MoveBy(i + 1, i + 2);
            edit.CommitGesture();
            Assert.That(edit.Document.Assets[assetId], Is.SameAs(blob));
        }
        edit.BeginGesture(image.Id);
        edit.ResizeBy(30, 20);
        edit.CommitGesture();
        edit.UpdateLayer(image.Id, layer => { layer.Name = "renamed"; layer.Visible = false; });
        Assert.That(edit.UndoCount, Is.EqualTo(103));
        Assert.That(edit.Document.Find(image.Id).Visible, Is.False);

        Assert.That(edit.Undo(), Is.True); // property
        Assert.That(edit.Document.Find(image.Id).Visible, Is.True);
        Assert.That(edit.Undo(), Is.True); // resize
        Assert.That(edit.Document.Find(image.Id).Bounds.Width, Is.EqualTo(100));
        for (var i = 0; i < 100; i++)
        {
            Assert.That(edit.Document.Assets[assetId], Is.SameAs(blob));
            Assert.That(edit.Undo(), Is.True);
        }
        Assert.That(edit.Document.Find(image.Id).Bounds.X, Is.EqualTo(1));
        Assert.That(edit.Undo(), Is.True); // import
        Assert.That(edit.Document.Assets, Is.Empty);
        for (var i = 0; i < 103; i++)
        {
            Assert.That(edit.Redo(), Is.True);
            Assert.That(edit.Document.Assets[assetId], Is.SameAs(blob));
        }
        Assert.That(edit.Document.Find(image.Id).Name, Is.EqualTo("renamed"));
        Assert.That(edit.Document.Find(image.Id).Bounds.Width, Is.EqualTo(130));

        edit.DeleteLayer(image.Id);
        Assert.That(edit.Document.Assets, Is.Empty);
        Assert.That(edit.SelectedLayerId, Is.Null);
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Assets[assetId], Is.SameAs(blob));
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Assets, Is.Empty);
        Assert.That(edit.Undo(), Is.True);
        edit.UpdateLayer(image.Id, layer => layer.Visible = true);
        Assert.That(edit.RedoCount, Is.Zero);
        Assert.That(edit.Document.Assets[assetId], Is.SameAs(blob));
    }

    [Test]
    public void ReimportAfterDeleteReusesPayloadRetainedByUndoHistory()
    {
        var edit = new EditSession(new EditorDocument());
        var firstId = Guid.NewGuid();
        var first = new Layer { Kind = LayerKind.Image, AssetId = firstId };
        edit.AddImageLayer(first, [1, 2, 3]);
        var retained = edit.Document.Assets[firstId];
        edit.DeleteLayer(first.Id);
        Assert.That(edit.Document.Assets, Is.Empty);
        var secondId = Guid.NewGuid();
        edit.AddImageLayer(new Layer { Kind = LayerKind.Image, AssetId = secondId }, [1, 2, 3]);
        Assert.That(edit.Document.Assets[secondId], Is.SameAs(retained));
        Assert.That(edit.RedoCount, Is.Zero);
        edit.Undo();
        edit.Undo();
        Assert.That(edit.Document.Assets[firstId], Is.SameAs(retained));
    }

    [Test]
    public void LockedLayerRejectsTransformButAllowsPropertyEdit()
    {
        var layer = new Layer { Kind = LayerKind.Text, Locked = true };
        var doc = new EditorDocument();
        doc.Layers.Add(layer);
        var edit = new EditSession(doc);
        Assert.Throws<InvalidOperationException>(() => edit.BeginGesture(layer.Id));
        edit.UpdateLayer(layer.Id, value => value.Text = "editable property");
        Assert.That(edit.Document.Find(layer.Id).Text, Is.EqualTo("editable property"));
    }
}
