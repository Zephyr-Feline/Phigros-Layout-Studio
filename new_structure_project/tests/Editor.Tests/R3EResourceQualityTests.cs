using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3EResourceQualityTests
{
    [Test]
    public void CleanupRemovesOnlyCurrentOrphansAndUndoRedoRestoreThem()
    {
        var document = CleanupDocument(out var shared, out var hidden, out var background,
            out var orphan);
        var edit = new EditSession(document);
        var sharedLayer = edit.Document.Layers[0];
        edit.UpdateLayer(sharedLayer.Id, layer => layer.Name = "edited");
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.RedoCount, Is.EqualTo(1));
        edit.SetSelection([sharedLayer.Id]);

        var plan = AssetResources.PlanCleanup(edit.Document);
        Assert.Multiple(() =>
        {
            Assert.That(plan.AssetIds, Is.EqualTo(new[] { orphan }));
            Assert.That(plan.Count, Is.EqualTo(1));
            Assert.That(plan.Bytes, Is.EqualTo(edit.Document.Assets[orphan].Length));
        });
        var result = edit.CleanUnreferencedAssets();
        Assert.Multiple(() =>
        {
            Assert.That(result.AssetIds, Is.EqualTo(new[] { orphan }));
            Assert.That(edit.Document.Assets.ContainsKey(orphan), Is.False);
            Assert.That(edit.Document.Assets.ContainsKey(shared), Is.True);
            Assert.That(edit.Document.Assets.ContainsKey(hidden), Is.True,
                "hidden layers still own their assets");
            Assert.That(edit.Document.Assets.ContainsKey(background), Is.True,
                "fixed backgrounds still own their assets");
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { sharedLayer.Id }));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.RedoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.True);
        });

        var noOp = edit.CleanUnreferencedAssets();
        Assert.Multiple(() =>
        {
            Assert.That(noOp.Changed, Is.False);
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.RedoCount, Is.Zero);
        });
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Assets.ContainsKey(orphan), Is.True);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Assets.ContainsKey(orphan), Is.False);
    }

    [Test]
    public void CleanupPersistsTheReducedAssetSet()
    {
        var document = CleanupDocument(out var shared, out _, out _, out var orphan);
        var edit = new EditSession(document);
        edit.CleanUnreferencedAssets();
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3e-cleanup-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "r3e-test");
            var reopened = NativePackageStore.Read(path);
            Assert.Multiple(() =>
            {
                Assert.That(reopened.Assets.ContainsKey(orphan), Is.False);
                Assert.That(reopened.Assets.ContainsKey(shared), Is.True);
                Assert.That(reopened.StateEquals(edit.Document), Is.True);
            });
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void NativeCanvasUpscaleWarningIsOutputWidthAwareAndSkipsHiddenLayers()
    {
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        var asset = Guid.NewGuid();
        document.AddAsset(asset, TestImages.EncodePng(400, 240,
            TestImages.SyntheticBgra(400, 240)));
        var visible = new Layer
        {
            Kind = LayerKind.Image, Name = "low-res artwork", AssetId = asset,
            Bounds = new RectD(100, 100, 800, 480)
        };
        var hidden = new Layer
        {
            Kind = LayerKind.Image, Name = "hidden artwork", AssetId = asset,
            Visible = false, Bounds = new RectD(100, 100, 900, 540)
        };
        document.Layers.AddRange([visible, hidden]);
        document.Validate();

        var native = ExportPreflight.Inspect(document, _ => false,
            readImageDimensions: ImageAssetValidator.ReadEncodedDimensions);
        Assert.Multiple(() =>
        {
            Assert.That(native.Select(warning => (warning.LayerId, warning.Kind)),
                Is.EqualTo(new[] { (visible.Id, ExportWarningKind.LowResolutionArtwork),
                    (hidden.Id, ExportWarningKind.HiddenLayer) }));
            Assert.That(native[0].Message, Does.Contain("source 400×240"));
            Assert.That(native[0].Message, Does.Contain("displayed 800×480"));
        });

        var quarter = ExportPreflight.Inspect(document, _ => false, 500,
            ImageAssetValidator.ReadEncodedDimensions);
        Assert.That(quarter.Select(warning => warning.Kind),
            Is.EqualTo(new[] { ExportWarningKind.HiddenLayer }));
        var noProbe = ExportPreflight.Inspect(document, _ => false);
        Assert.That(noProbe.Select(warning => warning.Kind),
            Is.EqualTo(new[] { ExportWarningKind.HiddenLayer }),
            "the old two-argument preflight remains dimension-free");
    }

    [Test]
    public void FixedBackgroundUpscaleUsesTheOutputCanvas()
    {
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        var background = Guid.NewGuid();
        document.AddAsset(background, TestImages.EncodePng(500, 300,
            TestImages.SyntheticBgra(500, 300)));
        document.FixedBackgroundAssetId = background;
        document.Validate();
        var native = ExportPreflight.Inspect(document, _ => false,
            readImageDimensions: ImageAssetValidator.ReadEncodedDimensions);
        Assert.That(native.Single().LayerId, Is.EqualTo(Guid.Empty));
        Assert.That(native.Single().LayerName, Is.EqualTo("Fixed background"));
        Assert.That(native.Single().Kind, Is.EqualTo(ExportWarningKind.LowResolutionArtwork));
        var smaller = ExportPreflight.Inspect(document, _ => false, 500,
            ImageAssetValidator.ReadEncodedDimensions);
        Assert.That(smaller, Is.Empty);
        var transparent = ExportPreflight.Inspect(document, _ => false,
            readImageDimensions: ImageAssetValidator.ReadEncodedDimensions,
            transparentBackground: true);
        Assert.That(transparent, Is.Empty,
            "transparent PNG output omits the fixed background");
    }

    [Test]
    public void RotationDoesNotCreateAFalseUpscaleWarning()
    {
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        var asset = Guid.NewGuid();
        document.AddAsset(asset, TestImages.EncodePng(400, 240,
            TestImages.SyntheticBgra(400, 240)));
        document.Layers.Add(new Layer
        {
            Kind = LayerKind.Image, AssetId = asset, RotationDegrees = 45,
            Bounds = new RectD(300, 180, 400, 240)
        });
        document.Validate();
        Assert.That(ExportPreflight.Inspect(document, _ => false,
            readImageDimensions: ImageAssetValidator.ReadEncodedDimensions), Is.Empty);
    }

    private static EditorDocument CleanupDocument(out Guid shared, out Guid hidden,
        out Guid background, out Guid orphan)
    {
        shared = Guid.NewGuid();
        hidden = Guid.NewGuid();
        background = Guid.NewGuid();
        orphan = Guid.NewGuid();
        var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 };
        foreach (var id in new[] { shared, hidden, background, orphan })
            document.AddAsset(id, TestImages.EncodePng(4, 4, TestImages.SyntheticBgra(4, 4)));
        document.Layers.Add(new Layer
        {
            Kind = LayerKind.Image, Name = "visible", AssetId = shared,
            Bounds = new RectD(20, 20, 120, 80)
        });
        document.Layers.Add(new Layer
        {
            Kind = LayerKind.Image, Name = "hidden", AssetId = hidden, Visible = false,
            Locked = true, Bounds = new RectD(160, 20, 120, 80)
        });
        document.FixedBackgroundAssetId = background;
        document.Validate();
        return document;
    }
}
