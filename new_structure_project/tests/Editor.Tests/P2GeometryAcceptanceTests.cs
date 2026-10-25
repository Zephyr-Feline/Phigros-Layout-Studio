using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>
/// REQ-GEOM-007 (paired preview / input / export transforms) and the automatable part of
/// REQ-CANVAS-004/005 (letterbox fit and scale independence). Document space stays logical;
/// the export is the identity mapping from that same space.
/// </summary>
public sealed class P2GeometryAcceptanceTests
{
    private static readonly (int Width, int Height)[] Canvases =
        [(320, 180), (640, 640), (1920, 1080), (2740, 1080), (7680, 4320)];

    private static readonly double[] Zooms = [0.25, 0.5, 1, 2, 8];

    private static readonly PointD[] Pans =
        [new PointD(0, 0), new PointD(37, -21), new PointD(-15.5, 8.25)];

    [Test]
    public void ForwardAndInverseTransformsStayPairedAcrossTheFullMatrix()
    {
        foreach (var (width, height) in Canvases)
        {
            foreach (var zoom in Zooms)
            {
                foreach (var pan in Pans)
                {
                    var transform = new ViewportTransform(width, height, 913, 617, zoom, pan);
                    foreach (var logical in new[]
                    {
                        new PointD(0, 0), new PointD(width, height),
                        new PointD(width * 0.37, height * 0.83), new PointD(-width * 0.25, height * 1.5)
                    })
                    {
                        var roundTrip = transform.ToLogical(transform.ToView(logical));
                        Assert.Multiple(() =>
                        {
                            Assert.That(roundTrip.X, Is.EqualTo(logical.X).Within(1e-6));
                            Assert.That(roundTrip.Y, Is.EqualTo(logical.Y).Within(1e-6));
                        });
                    }

                    var rect = new RectD(width * 0.1, height * 0.2, width * 0.3, height * 0.4);
                    var viewRect = transform.ToView(rect);
                    var origin = transform.ToView(new PointD(rect.X, rect.Y));
                    Assert.Multiple(() =>
                    {
                        Assert.That(viewRect.X, Is.EqualTo(origin.X).Within(1e-6));
                        Assert.That(viewRect.Y, Is.EqualTo(origin.Y).Within(1e-6));
                        Assert.That(viewRect.Width, Is.EqualTo(rect.Width * transform.Scale).Within(1e-6));
                        Assert.That(viewRect.Height, Is.EqualTo(rect.Height * transform.Scale).Within(1e-6));
                        // Preview scale is isotropic: the same factor applies to both axes.
                        Assert.That(viewRect.Width / rect.Width,
                            Is.EqualTo(viewRect.Height / rect.Height).Within(1e-9));
                    });
                }
            }
        }
    }

    [TestCase(2740, 1080, 800, 500, 1.0)]
    [TestCase(2740, 1080, 800, 500, 2.0)]
    [TestCase(1920, 1080, 1280, 720, 0.5)]
    [TestCase(320, 180, 2000, 300, 1.0)]
    [TestCase(640, 640, 700, 600, 1.0)]
    public void LetterboxFitCentresTheDocumentAndKeepsItsAspect(int canvasWidth, int canvasHeight,
        double viewWidth, double viewHeight, double zoom)
    {
        var transform = new ViewportTransform(canvasWidth, canvasHeight, viewWidth, viewHeight, zoom);
        var scale = Math.Min(viewWidth / canvasWidth, viewHeight / canvasHeight) * zoom;
        var paintedWidth = canvasWidth * scale;
        var paintedHeight = canvasHeight * scale;
        Assert.Multiple(() =>
        {
            Assert.That(transform.Scale, Is.EqualTo(scale).Within(1e-9));
            Assert.That(transform.Origin.X, Is.EqualTo((viewWidth - paintedWidth) / 2).Within(1e-9));
            Assert.That(transform.Origin.Y, Is.EqualTo((viewHeight - paintedHeight) / 2).Within(1e-9));
            Assert.That(paintedWidth / paintedHeight, Is.EqualTo((double)canvasWidth / canvasHeight).Within(1e-9));
            // The document stays centred in the viewport, which is what letterboxing means.
            Assert.That(transform.ToView(new PointD(canvasWidth / 2.0, canvasHeight / 2.0)).X,
                Is.EqualTo(viewWidth / 2).Within(1e-9));
            Assert.That(transform.ToView(new PointD(canvasWidth / 2.0, canvasHeight / 2.0)).Y,
                Is.EqualTo(viewHeight / 2).Within(1e-9));
        });
    }

    [Test]
    public void InvalidViewportsAreRejectedInsteadOfProducingNaN()
    {
        var invalid = new[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -1.0
        };
        foreach (var value in invalid)
        {
            Assert.Multiple(() =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportTransform(value, 1080, 800, 500));
                Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportTransform(2740, value, 800, 500));
                Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportTransform(2740, 1080, value, 500));
                Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportTransform(2740, 1080, 800, value));
                Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportTransform(2740, 1080, 800, 500, value));
            });
        }

        var transform = new ViewportTransform(2740, 1080, 800, 500, 1.5, new PointD(12, -8));
        var logical = transform.ToLogical(new PointD(400, 250));
        Assert.Multiple(() =>
        {
            Assert.That(double.IsFinite(logical.X), Is.True);
            Assert.That(double.IsFinite(logical.Y), Is.True);
            Assert.That(double.IsFinite(transform.ScreenPixelsToLogical(8, 1.25)), Is.True);
        });
    }

    [TestCase(320, 180, 40, 30, 100, 60)]
    [TestCase(320, 180, 5, 7, 33, 21)]
    [TestCase(640, 360, 200, 140, 120, 90)]
    [TestCase(2740, 1080, 1000, 400, 640, 320)]
    public void ExportedPixelsMatchTheLogicalGeometry(int canvasWidth, int canvasHeight,
        int x, int y, int width, int height)
    {
        var bounds = new RectD(x, y, width, height);
        var document = new EditorDocument { CanvasWidth = canvasWidth, CanvasHeight = canvasHeight,
            BackgroundArgb = 0xFF000000 };
        document.Layers.Add(new Layer { Kind = LayerKind.Shape, Name = "ink", Bounds = bounds,
            ColorArgb = 0xFFFFFFFF });
        var (left, top, right, bottom) = TestImages.InkBounds(TestImages.RenderPng(document));
        Assert.Multiple(() =>
        {
            // Antialiased edges may tint one boundary pixel; the box itself must not move.
            Assert.That(left, Is.EqualTo(x).Within(1));
            Assert.That(top, Is.EqualTo(y).Within(1));
            Assert.That(right, Is.EqualTo(x + width - 1).Within(1));
            Assert.That(bottom, Is.EqualTo(y + height - 1).Within(1));
        });
    }

    [Test]
    public void PreviewGeometryAgreesWithExportGeometryUpToAUniformScale()
    {
        const int canvasWidth = 320;
        const int canvasHeight = 180;
        var first = new PointD(10, 20);
        var second = new PointD(210, 140);
        foreach (var zoom in Zooms)
        {
            var transform = new ViewportTransform(canvasWidth, canvasHeight, 900, 600, zoom, new PointD(11, -6));
            var viewFirst = transform.ToView(first);
            var viewSecond = transform.ToView(second);
            var deltaX = viewSecond.X - viewFirst.X;
            var deltaY = viewSecond.Y - viewFirst.Y;
            // The export uses the same logical space (identity), so relative geometry is preserved
            // and the preview scale is the only difference.
            Assert.Multiple(() =>
            {
                Assert.That(deltaX / (second.X - first.X), Is.EqualTo(transform.Scale).Within(1e-9));
                Assert.That(deltaY / (second.Y - first.Y), Is.EqualTo(transform.Scale).Within(1e-9));
            });
        }
    }

    [Test]
    public void HitTestingUsesLogicalBoundsThroughTheTransform()
    {
        var bounds = new RectD(40, 30, 100, 60);
        foreach (var zoom in Zooms)
        {
            var transform = new ViewportTransform(320, 180, 900, 600, zoom, new PointD(5, -3));
            foreach (var (logical, expected) in new[]
            {
                (new PointD(40, 30), true), (new PointD(140, 90), true),
                (new PointD(90, 60), true), (new PointD(39.5, 60), false),
                (new PointD(60, 90.5), false), (new PointD(200, 200), false)
            })
            {
                var hit = bounds.Contains(transform.ToLogical(transform.ToView(logical)));
                Assert.That(hit, Is.EqualTo(expected), $"zoom={zoom} point={logical.X},{logical.Y}");
            }
        }
    }

    [Test]
    public void GeometryRoundTripsThroughThePublicProjectFormat()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "p2-geometry",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var document = new EditorDocument { CanvasWidth = 2740, CanvasHeight = 1080 };
            var layer = new Layer { Kind = LayerKind.Shape,
                Bounds = new RectD(11.25, 19.5, 200.75, 91.125), ColorArgb = 0xFF00FF00 };
            document.Layers.Add(layer);
            var edit = new EditSession(document);
            var path = Path.Combine(directory, "geometry.ppgproj");
            NativePackageStore.Save(path, edit.Document, "p2-test");
            var reloaded = NativePackageStore.Read(path);
            Assert.That(reloaded.Find(layer.Id).Bounds, Is.EqualTo(layer.Bounds));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

}
