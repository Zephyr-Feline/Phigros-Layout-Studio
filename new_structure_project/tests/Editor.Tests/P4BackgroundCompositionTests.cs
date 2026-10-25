using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4BackgroundCompositionTests
{
    [TestCase(3, 800, 400)]
    [TestCase(5, 800, 400)]
    [TestCase(3, 1280, 720)]
    [TestCase(5, 800, 600)]
    public void VisibleArtworkMakesOneSmoothOpaqueBackground(int count, int canvasWidth, int canvasHeight)
    {
        AvaloniaTestHost.EnsureStarted();
        var colors = count == 3
            ? new uint[] { 0xFFFF0000, 0xFF00FF00, 0xFF0000FF }
            : [0xFFFFFFFF, 0xFF000000, 0xFFEE0000, 0xFF0000EE, 0xFFFFFF00];
        var document = MakeDocument(colors, canvasWidth, canvasHeight);
        var snapshot = BackgroundComposer.Generate(document, TestImages.Decoder);
        var (width, height, bgra) = TestImages.DecodePng(snapshot);
        Assert.That((width, height), Is.EqualTo((256,
            (int)Math.Round(canvasHeight * 256.0 / canvasWidth))));
        foreach (var layer in document.Layers)
        {
            var without = document.Copy();
            without.Find(layer.Id).Visible = false;
            Assert.That(BackgroundComposer.Generate(without, TestImages.Decoder),
                Is.Not.EqualTo(snapshot), $"visible contributor {layer.Name}");
        }
        var y = height * 3 / 4;
        var largestStep = 0;
        for (var x = 1; x < width; x++)
        for (var channel = 0; channel < 3; channel++)
            largestStep = Math.Max(largestStep,
                Math.Abs(bgra[(y * width + x) * 4 + channel] -
                         bgra[(y * width + x - 1) * 4 + channel]));
        var leftSample = (y * width + width / 4) * 4;
        var rightSample = (y * width + 3 * width / 4) * 4;
        Assert.Multiple(() =>
        {
            Assert.That(largestStep, Is.LessThanOrEqualTo(8), "no hard artwork boundary");
            Assert.That(Enumerable.Range(0, width * height)
                .All(i => bgra[i * 4 + 3] == 255), Is.True, "background is opaque");
            Assert.That(bgra[leftSample..(leftSample + 3)],
                Is.Not.EqualTo(bgra[rightSample..(rightSample + 3)]),
                "both sides reflect different artwork");
        });
        if (count == 3)
        {
            var left = (y * width + width / 8) * 4;
            var center = (y * width + width / 2) * 4;
            var right = (y * width + width * 7 / 8) * 4;
            Assert.Multiple(() =>
            {
                Assert.That(bgra[left + 2], Is.GreaterThan(bgra[left]), "red on left");
                Assert.That(bgra[center + 1], Is.GreaterThan(bgra[center + 2]), "green in center");
                Assert.That(bgra[right], Is.GreaterThan(bgra[right + 2]), "blue on right");
            });
        }
        else
        {
            var bright = (y * width + width / 10) * 4;
            var dark = (y * width + width * 3 / 10) * 4;
            Assert.That(bgra[bright] + bgra[bright + 1] + bgra[bright + 2],
                Is.GreaterThan(bgra[dark] + bgra[dark + 1] + bgra[dark + 2]),
                "bright and dark art retain their contrast");
        }
    }

    [Test]
    public void HiddenArtworkDoesNotContributeAndSnapshotChangesOnlyWhenRegenerated()
    {
        AvaloniaTestHost.EnsureStarted();
        var document = MakeDocument([0xFFFF0000, 0xFF0000FF]);
        var edit = new EditSession(document);
        var first = BackgroundComposer.Generate(edit.Document, TestImages.Decoder);
        edit.SetFixedBackground(first);
        var firstId = edit.Document.FixedBackgroundAssetId!.Value;
        var hiddenId = edit.Document.Layers[1].Id;
        edit.UpdateLayer(hiddenId, layer => layer.Visible = false);
        Assert.That(edit.Document.Assets[firstId].ToArray(), Is.EqualTo(first),
            "the saved snapshot is stable until explicitly regenerated");
        var second = BackgroundComposer.Generate(edit.Document, TestImages.Decoder);
        Assert.That(second, Is.Not.EqualTo(first));
        edit.SetFixedBackground(second);
        var secondId = edit.Document.FixedBackgroundAssetId!.Value;
        Assert.That(edit.Document.Assets.Keys, Is.EquivalentTo(new[]
            { edit.Document.Layers[0].AssetId!.Value, edit.Document.Layers[1].AssetId!.Value, secondId }));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.FixedBackgroundAssetId, Is.EqualTo(firstId));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Find(hiddenId).Visible, Is.True);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.FixedBackgroundAssetId, Is.EqualTo(secondId));
        Assert.That(edit.Document.Assets[secondId].ToArray(), Is.EqualTo(second));
    }

    [Test]
    public void GeneratedBackgroundSurvivesNativeRoundTripAndTransparentExportSkipsIt()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(MakeDocument([0xFFFF0000, 0xFF00FF00, 0xFF0000FF]));
        var background = BackgroundComposer.Generate(edit.Document, TestImages.Decoder);
        edit.SetFixedBackground(background);
        var opaqueBefore = TestImages.RenderPng(edit.Document);
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "p4-blended-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "p4-test");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.Assets[reopened.FixedBackgroundAssetId!.Value].ToArray(),
                Is.EqualTo(background));
            Assert.That(TestImages.RenderPng(reopened), Is.EqualTo(opaqueBefore),
                "preview/export painter receives the same persisted bitmap");
            var (width, _, transparent) = TestImages.DecodePng(TestImages.RenderPng(reopened, true));
            Assert.That(transparent[(reopened.CanvasHeight - 1) * width * 4 + 3], Is.Zero);
            var (opaqueWidth, _, opaquePixels) = TestImages.DecodePng(opaqueBefore);
            var centerOfFirstImage = (100 * opaqueWidth + 100) * 4;
            Assert.That(opaquePixels[centerOfFirstImage..(centerOfFirstImage + 4)],
                Is.EqualTo(new byte[] { 0, 0, 255, 255 }), "foreground artwork remains sharp");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void DesktopButtonCreatesOneUndoableSnapshotAndEmptyProjectFailsWithoutMutation()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(MakeDocument([0xFFFF0000, 0xFF0000FF]));
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var button = UiTestActions.FindNamed(window, "GenerateBlendedBackground");
            UiTestActions.Click(button);
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.FixedBackgroundAssetId, Is.Not.Null);
                Assert.That(edit.UndoCount, Is.EqualTo(1));
            });
            UiTestActions.Click(button);
            Assert.That(edit.UndoCount, Is.EqualTo(1),
                "regenerating identical pixels is a no-op");
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.FixedBackgroundAssetId, Is.Null);
        }
        finally { window.Close(); }

        var empty = new EditSession(new EditorDocument());
        var before = empty.Document.Copy();
        Assert.Throws<InvalidOperationException>(() =>
            BackgroundComposer.Generate(empty.Document, TestImages.Decoder));
        Assert.That(empty.Document.StateEquals(before), Is.True);
        Assert.That(empty.UndoCount, Is.Zero);
    }

    [Test]
    public void ExtremeSupportedCanvasKeepsBackgroundAspectInsteadOfCroppingIt()
    {
        AvaloniaTestHost.EnsureStarted();
        var document = MakeDocument([0xFFFF0000], 7680, 180);
        var (width, height, _) = TestImages.DecodePng(
            BackgroundComposer.Generate(document, TestImages.Decoder));
        Assert.That((width, height), Is.EqualTo((256, 6)));
    }

    [Test]
    public void SemitransparentArtworkContributesWithOneAlphaFactor()
    {
        AvaloniaTestHost.EnsureStarted();
        var opaque = MakeDocument([0xFFFF0000]);
        var translucent = MakeDocument([0x80FF0000]);
        opaque.BackgroundArgb = 0xFF000000;
        translucent.BackgroundArgb = 0xFF000000;
        var (width, _, full) = TestImages.DecodePng(
            BackgroundComposer.Generate(opaque, TestImages.Decoder));
        var (_, _, half) = TestImages.DecodePng(
            BackgroundComposer.Generate(translucent, TestImages.Decoder));
        var at = (35 * width + 32) * 4 + 2;
        Assert.That(half[at], Is.GreaterThan(full[at] * 0.7),
            "50% opacity should reduce the artwork weight once, not square it");
        Assert.That(half[at], Is.LessThan(full[at]));
    }

    private static EditorDocument MakeDocument(uint[] colors, int canvasWidth = 800,
        int canvasHeight = 400)
    {
        var document = new EditorDocument
        {
            CanvasWidth = canvasWidth, CanvasHeight = canvasHeight,
            BackgroundArgb = 0xFF202020,
            BackgroundBlend = new(IncludeFlatImages: true)
        };
        for (var index = 0; index < colors.Length; index++)
        {
            var id = Guid.NewGuid();
            document.AddAsset(id, SolidPng(colors[index]));
            document.Layers.Add(new Layer
            {
                Kind = LayerKind.Image, AssetId = id, Name = $"Artwork {index + 1}",
                Bounds = new RectD(60 + index * (canvasWidth - 200.0) /
                    Math.Max(1, colors.Length - 1), 60, 80, 100)
            });
        }
        document.Validate();
        return document;
    }

    private static byte[] SolidPng(uint argb)
    {
        const int width = 32, height = 32;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)argb;
            pixels[i + 1] = (byte)(argb >> 8);
            pixels[i + 2] = (byte)(argb >> 16);
            pixels[i + 3] = (byte)(argb >> 24);
        }
        return TestImages.EncodePng(width, height, pixels);
    }
}
