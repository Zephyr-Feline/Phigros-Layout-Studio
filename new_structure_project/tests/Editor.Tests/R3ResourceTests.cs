using Editor.Application;
using Editor.Core;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3ResourceTests
{
    [Test]
    public void InventoryReportsReferencesDimensionsAndUnreferencedAssets()
    {
        var document = Document(out var shared, out var unused);
        var resources = AssetResources.Inspect(document, ImageAssetValidator.ReadEncodedDimensions);
        Assert.That(resources, Has.Count.EqualTo(2));
        var sharedResource = resources.Single(resource => resource.AssetId == shared);
        Assert.That(sharedResource.LayerReferences, Has.Count.EqualTo(2));
        Assert.That(sharedResource.IsShared, Is.True);
        Assert.That(sharedResource.IsFixedBackground, Is.True);
        Assert.That(sharedResource.IsReferenced, Is.True);
        Assert.That(sharedResource.Width, Is.EqualTo(4));
        Assert.That(sharedResource.Height, Is.EqualTo(4));
        var unusedResource = resources.Single(resource => resource.AssetId == unused);
        Assert.That(unusedResource.IsUnreferenced, Is.True);
        Assert.That(unusedResource.HasWarning, Is.True);
    }

    [Test]
    public void SafeReplacementUpdatesAllReferencesAndKeepsUndoHistoryStable()
    {
        var document = Document(out var shared, out var unused);
        var edit = new EditSession(document);
        var before = edit.Document.Copy();
        var replacementBytes = TestImages.EncodePng(8, 6, TestImages.SyntheticBgra(8, 6));
        var result = edit.ReplaceAsset(shared, replacementBytes, TestImages.ValidateImages);
        Assert.That(result.ReplacementAssetId, Is.Not.EqualTo(shared));
        Assert.That(result.LayerIds, Has.Count.EqualTo(2));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.Document.Assets.ContainsKey(shared), Is.False);
        foreach (var layer in edit.Document.Layers.Where(layer => layer.AssetId == result.ReplacementAssetId))
            Assert.That(layer.AssetId, Is.EqualTo(result.ReplacementAssetId));
        Assert.That(edit.Document.FixedBackgroundAssetId, Is.EqualTo(result.ReplacementAssetId));
        Assert.That(edit.Document.Assets.ContainsKey(unused), Is.True);
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
        edit.Redo();
        Assert.That(edit.Document.FixedBackgroundAssetId, Is.EqualTo(result.ReplacementAssetId));
    }

    [Test]
    public void InvalidReplacementAndUnreferencedReplacementDoNotPartiallyChangeTheDocument()
    {
        var document = Document(out var shared, out var unused);
        var edit = new EditSession(document);
        var before = edit.Document.Copy();
        Assert.That(() => edit.ReplaceAsset(shared, [1, 2, 3], TestImages.ValidateImages),
            Throws.InstanceOf<InvalidDataException>());
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(() => edit.ReplaceAsset(unused,
            TestImages.EncodePng(8, 6, TestImages.SyntheticBgra(8, 6)), TestImages.ValidateImages),
            Throws.InstanceOf<InvalidOperationException>());
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void DimensionFailureIsReportedWithoutChangingDocumentBytes()
    {
        var document = Document(out _, out var unused);
        document.Assets[unused] = new AssetBlob([1, 2, 3]);
        var resources = AssetResources.Inspect(document, ImageAssetValidator.ReadEncodedDimensions);
        var resource = resources.Single(item => item.AssetId == unused);
        Assert.That(resource.DimensionDiagnostic, Is.Not.Null);
        Assert.That(resource.HasWarning, Is.True);
        Assert.That(resource.Width, Is.Null);
        Assert.That(document.Assets[unused].Length, Is.EqualTo(3));
    }

    private static EditorDocument Document(out Guid shared, out Guid unused)
    {
        shared = Guid.NewGuid();
        unused = Guid.NewGuid();
        var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 };
        document.AddAsset(shared, P4AutomaticBackgroundTests.Solid(0xFFFF0000));
        document.AddAsset(unused, P4AutomaticBackgroundTests.Solid(0xFF00FF00));
        document.Layers.Add(new Layer
        {
            Kind = LayerKind.Image, Name = "shared one", AssetId = shared,
            Bounds = new RectD(20, 20, 120, 80)
        });
        document.Layers.Add(new Layer
        {
            Kind = LayerKind.Image, Name = "shared two", AssetId = shared, Visible = false,
            Bounds = new RectD(160, 20, 120, 80)
        });
        document.FixedBackgroundAssetId = shared;
        document.Validate();
        return document;
    }
}
