using Editor.Application;
using Editor.Core;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>
/// P2-scope render/export invariants that previously had probe-only or no automated evidence:
/// transparent export semantics, editor overlays never reaching the export, hidden layers, and the
/// badge level/ratio model rules.
/// </summary>
public sealed class P2RenderInvariantTests
{
    [Test]
    public void TransparentExportKeepsTheEmptyBackgroundFullyTransparent()
    {
        // The documented canvas range is 320x180 upwards, so the fixtures stay inside it.
        var document = new EditorDocument { CanvasWidth = 320, CanvasHeight = 180 };
        document.Layers.Add(new Layer { Kind = LayerKind.Shape, Bounds = new RectD(10, 8, 20, 12),
            ColorArgb = 0xFFFF0000 });
        var (width, height, pixels) = TestImages.DecodePng(
            TestImages.RenderPng(document, transparentBackground: true));
        Assert.Multiple(() =>
        {
            Assert.That(width, Is.EqualTo(320));
            Assert.That(height, Is.EqualTo(180));
            Assert.That(IsEmptyBackground(pixels, 0), Is.True, "top-left outside the shape");
            Assert.That(IsEmptyBackground(pixels, ((height - 1) * width + (width - 1)) * 4), Is.True,
                "bottom-right outside the shape");
        });

        var shape = (8 * width + 10) * 4;
        Assert.That(IsEmptyBackground(pixels, shape), Is.False, "shape pixel must not be transparent");
        Assert.That(pixels[shape..(shape + 4)], Has.Some.EqualTo((byte)255), "opaque channel");
    }

    [Test]
    public void ExportIgnoresEditorSelection()
    {
        var document = new EditorDocument { CanvasWidth = 320, CanvasHeight = 180,
            BackgroundArgb = 0xFF000000 };
        document.Layers.Add(new Layer { Kind = LayerKind.Shape, Bounds = new RectD(4, 4, 20, 12),
            ColorArgb = 0xFFFFFFFF });
        var before = TestImages.RenderPng(document);

        var session = new EditSession(document);
        session.SelectedLayerId = document.Layers[0].Id;
        Assert.Multiple(() =>
        {
            Assert.That(TestImages.RenderPng(session.Document), Is.EqualTo(before));
            Assert.That(TestImages.RenderPng(document), Is.EqualTo(before));
        });
    }

    [Test]
    public void HiddenLayersContributeNoInkButStayInTheModel()
    {
        var document = new EditorDocument { CanvasWidth = 320, CanvasHeight = 180,
            BackgroundArgb = 0xFF000000 };
        var layer = new Layer { Kind = LayerKind.Shape, Name = "kept", Bounds = new RectD(10, 8, 20, 12),
            ColorArgb = 0xFFFFFFFF };
        document.Layers.Add(layer);
        var visibleInk = InkPixels(TestImages.RenderPng(document));
        document.Layers[0].Visible = false;
        var hiddenInk = InkPixels(TestImages.RenderPng(document));
        Assert.Multiple(() =>
        {
            Assert.That(visibleInk, Is.GreaterThanOrEqualTo(20 * 12));
            Assert.That(visibleInk, Is.LessThanOrEqualTo(21 * 13));
            Assert.That(hiddenInk, Is.Zero);
            Assert.That(document.Find(layer.Id).Visible, Is.False, "hidden state stays in the model");
        });
    }

    [Test]
    public void BadgeLevelMustStayAPositiveInteger()
    {
        var badge = EditorDocument.NewBadge(new PointD(10, 10));
        var document = new EditorDocument();
        document.Layers.Add(badge);
        Assert.That(badge.Level, Is.GreaterThan(0));
        Assert.DoesNotThrow(document.Validate);

        foreach (var invalid in new[] { 0, -3 })
        {
            badge.Level = invalid;
            Assert.Throws<InvalidDataException>(document.Validate);
        }
        badge.Level = 1;
        Assert.DoesNotThrow(document.Validate);

        // The edit path refuses an invalid level instead of writing it.
        var session = new EditSession(document);
        Assert.Throws<InvalidDataException>(() => session.UpdateLayer(badge.Id, layer => layer.Level = 0));
        Assert.That(session.Document.Find(badge.Id).Level, Is.EqualTo(1));
    }

    private static bool IsEmptyBackground(byte[] pixels, int offset) =>
        pixels[offset] == 0 && pixels[offset + 1] == 0 &&
        pixels[offset + 2] == 0 && pixels[offset + 3] == 0;

    private static int InkPixels(byte[] png)
    {
        var (_, _, pixels) = TestImages.DecodePng(png);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) count++;
        return count;
    }
}
