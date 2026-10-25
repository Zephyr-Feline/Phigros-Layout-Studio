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

public sealed class P5ExportSizeTests
{
    [TestCase(1920, 1080, 3840, 2160)]
    [TestCase(1440, 1080, 1920, 1440)]
    [TestCase(2048, 1080, 1920, 1012)]
    [TestCase(2048, 1080, 64, 34)]
    [TestCase(1080, 1920, 540, 960)]
    [TestCase(2740, 1080, 1370, 540)]
    public void SizeUsesEvenRoundingAndUniformLogicalScale(int w, int h, int target, int expectedHeight)
    {
        var doc = new EditorDocument { CanvasWidth = w, CanvasHeight = h };
        var size = PngExportSize.Create(doc, target);
        Assert.That(size.Width, Is.EqualTo(target));
        Assert.That(size.Height, Is.EqualTo(expectedHeight));
        Assert.That(size.Scale, Is.EqualTo((double)target / w));
        Assert.That(Math.Abs(size.OffsetY), Is.LessThanOrEqualTo(.25));
        Assert.That(size.Scale * h + 2 * size.OffsetY, Is.EqualTo(expectedHeight).Within(1e-10));
        Assert.That(PngExportSize.Create(doc), Is.EqualTo(new PngExportSize(w, h, 1, 0)));
    }

    [TestCase(320, 180, 0)]
    [TestCase(320, 180, -1)]
    [TestCase(320, 180, 16385)]
    [TestCase(320, 180, int.MaxValue)]
    [TestCase(320, 4320, 2000)] // height limit
    [TestCase(320, 320, 10000)] // pixel budget
    [TestCase(7680, 180, 1)] // would round to zero height
    public void InvalidSizeStopsBeforeImageValidationRenderingOrWriting(int w, int h, int target)
    {
        var session = new EditSession(new EditorDocument { CanvasWidth = w, CanvasHeight = h });
        var workflow = new PngExportWorkflow(session, _ => Assert.Fail("Unexpected decode"),
            (_, _, _) => throw new AssertionException("Unexpected render"),
            (_, _) => Assert.Fail("Unexpected write"), new ProtectedSourcePaths(), () => null);
        Assert.Throws<InvalidDataException>(() => workflow.Export("unused.png", outputWidth: target));
        Assert.That(session.IsDirty, Is.False);
        Assert.Throws<InvalidDataException>(() => PngExporter.Render(session.Document, outputWidth: target));
    }

    [TestCase(640, 360, 1280, false)]
    [TestCase(640, 480, 320, true)]
    [TestCase(640, 640, 960, false)]
    [TestCase(640, 481, 800, true)]
    public void ActualPngKeepsGeometryAndAlphaWithoutMutatingDocument(int w, int h, int target, bool transparent)
    {
        AvaloniaTestHost.EnsureStarted();
        var badge = EditorDocument.NewBadge(new(100, 100));
        var doc = new EditorDocument { CanvasWidth = w, CanvasHeight = h, BackgroundArgb = 0xFF102030,
            Layers = [badge, new Layer { Kind = LayerKind.Shape, ColorArgb = 0xFF00FF00,
                Bounds = new(300, 100, 80, 40) }] };
        var before = doc.Copy();
        var png = TestImages.RenderPng(doc, transparent, target);
        var size = PngExportSize.Create(doc, target);
        var (width, height, pixels) = TestImages.DecodePng(png);
        Assert.That((width, height), Is.EqualTo((size.Width, size.Height)));
        var inside = ((int)(120 * size.Scale + size.OffsetY) * width + (int)(340 * size.Scale)) * 4;
        Assert.That(pixels[inside + 1], Is.EqualTo(255));
        Assert.That(pixels[inside + 3], Is.EqualTo(255));
        Assert.That(pixels[(10 * width + 10) * 4 + 3], Is.EqualTo(transparent ? 0 : 255));
        // Independent oracle: exact-ratio outputs match a correspondingly scaled scene,
        // preserving the badge's geometry as well as the solid shape.
        if (size.OffsetY == 0 && target >= 320 && size.Height >= 180)
        {
            var scaled = doc.Copy(); scaled.CanvasWidth = width; scaled.CanvasHeight = height;
            foreach (var layer in scaled.Layers)
            {
                var b = layer.Bounds;
                layer.Bounds = new(b.X * size.Scale, b.Y * size.Scale, b.Width * size.Scale, b.Height * size.Scale);
            }
            var expected = TestImages.DecodePng(TestImages.RenderPng(scaled, transparent));
            // Test the solid geometry away from text/antialiasing, whose raster phase can differ.
            Assert.That(pixels[inside..(inside + 4)], Is.EqualTo(expected.Bgra[inside..(inside + 4)]));
        }
        Assert.That(doc.StateEquals(before), Is.True);
        Assert.That(TestImages.RenderPng(doc, transparent), Is.EqualTo(TestImages.RenderPng(doc, transparent, w)));
    }

    [TestCase(480)]
    [TestCase(481)]
    public void RoundedOutputDoesNotStretchBadge(int height)
    {
        AvaloniaTestHost.EnsureStarted();
        var badge = EditorDocument.NewBadge(new(100, 100));
        var doc = new EditorDocument { CanvasWidth = 640, CanvasHeight = height, Layers = [badge] };
        var (w, h, pixels) = TestImages.DecodePng(TestImages.RenderPng(doc, true, 800));
        var xs = new List<int>(); var ys = new List<int>();
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            if (pixels[(y * w + x) * 4 + 3] > 127) { xs.Add(x); ys.Add(y); }
        Assert.That(xs.Max() - xs.Min() + 1, Is.EqualTo(badge.Bounds.Width * 1.25).Within(1));
        Assert.That(ys.Max() - ys.Min() + 1, Is.EqualTo(badge.Bounds.Height * 1.25).Within(1));
    }

    [TestCase("cancel")]
    [TestCase("change")]
    [TestCase("invalid")]
    public void SizePromptCancellationChangeAndInvalidValuesStopBeforePathPicker(string action)
    {
        var edit = new EditSession(new EditorDocument());
        var workflow = new PngExportWorkflow(edit, _ => { },
            (_, _, _) => throw new AssertionException("Unexpected render"),
            (_, _) => Assert.Fail("Unexpected write"), new ProtectedSourcePaths(), () => null);
        Task<bool> Run() => workflow.ExportWithReviewAsync(false, _ => false,
            _ => throw new AssertionException("Unexpected warning"),
            () => throw new AssertionException("Unexpected picker"), () =>
            {
                if (action == "change") edit.UpdateDocument(d => d.CanvasHeight = 1200);
                return Task.FromResult<int?>(action == "cancel" ? null : action == "invalid" ? 0 : 1920);
            });
        if (action == "cancel") Assert.That(Run().GetAwaiter().GetResult(), Is.False);
        else if (action == "change") Assert.ThrowsAsync<InvalidOperationException>(Run);
        else Assert.ThrowsAsync<InvalidDataException>(Run);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReviewedSizeReachesRendererWithoutChangingHistory(bool transparent)
    {
        var edit = new EditSession(new EditorDocument());
        edit.UpdateDocument(d => d.BackgroundArgb = 0xFF123456);
        var before = edit.Document.Copy();
        var written = false;
        var workflow = new PngExportWorkflow(edit, _ => { }, (doc, mode, width) =>
        {
            Assert.That(doc.StateEquals(before), Is.True);
            Assert.That(width, Is.EqualTo(1920)); Assert.That(mode, Is.EqualTo(transparent));
            return [1];
        }, (_, _) => written = true, new ProtectedSourcePaths(), () => null);
        Assert.That(await workflow.ExportWithReviewAsync(transparent, _ => false,
            _ => Task.FromResult(true), () => Task.FromResult<string?>("unused.png"),
            () => Task.FromResult<int?>(1920)), Is.True);
        Assert.That(written, Is.True); Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.UndoCount, Is.EqualTo(1)); Assert.That(edit.IsDirty, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SizeDialogValidatesResetsAndCancels(bool accept)
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new Avalonia.Themes.Fluent.FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var owner = new Window(); var doc = new EditorDocument { CanvasWidth = 640, CanvasHeight = 480 };
        var before = doc.Copy(); var dialog = new PngExportDialog(doc);
        try
        {
            owner.Show(); var pending = dialog.ShowDialog<int?>(owner);
            var width = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            var text = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PngOutputSize");
            Button FindButton(string name) => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
            width.Text = "-1"; Assert.That(FindButton("ConfirmPngSize").IsEnabled, Is.False);
            width.Text = "10000"; Assert.That(FindButton("ConfirmPngSize").IsEnabled, Is.True);
            width.Text = "16384"; Assert.That(FindButton("ConfirmPngSize").IsEnabled, Is.False);
            FindButton("ResetPngSize").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.That(width.Text, Is.EqualTo("640"));
            width.Text = "1920"; Assert.That(text.Text, Is.EqualTo("1920 × 1440 像素"));
            dialog.UpdateLayout();
            using (var frame = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new Avalonia.PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height)))
            {
                frame.Render(dialog);
                frame.Save(Path.Combine(TestContext.CurrentContext.WorkDirectory, "png-size-dialog.png"),
                    new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            FindButton(accept ? "ConfirmPngSize" : "CancelPngSize").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.That(pending.Result, Is.EqualTo(accept ? (int?)1920 : null));
            Assert.That(doc.StateEquals(before), Is.True);
        }
        finally { dialog.Close(); owner.Close(); Avalonia.Application.Current!.Styles.Remove(theme); }
    }
}
