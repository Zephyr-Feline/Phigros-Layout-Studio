using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4AutomaticBackgroundTests
{
    [TestCase(3, 800, 600)]
    [TestCase(5, 960, 540)]
    public void CachedPreviewAndFreshExportAgreeWithoutChangingDocumentOrAssets(int count, int width, int height)
    {
        var document = MakeDocument(count, width, height);
        var before = document.Copy();
        var cache = new BackgroundRenderCache();
        Assert.That(RenderPreview(document, cache), Is.EqualTo(TestImages.RenderPng(document)));
        Assert.That(RenderPreview(document, cache), Is.EqualTo(TestImages.RenderPng(document)));
        Assert.That(document.StateEquals(before), Is.True);
        Assert.That(document.Assets.Count, Is.EqualTo(count));
        var (_, _, transparent) = TestImages.DecodePng(TestImages.RenderPng(document, true));
        Assert.That(transparent[^1], Is.Zero);
    }

    [TestCase("position")]
    [TestCase("size")]
    [TestCase("visible")]
    [TestCase("asset")]
    [TestCase("canvas")]
    [TestCase("color")]
    [TestCase("blur")]
    [TestCase("darkness")]
    public void RelevantEditsInvalidateCacheAndMatchFreshComposition(string change)
    {
        var document = MakeDocument();
        var cache = new BackgroundRenderCache();
        var before = cache.Get(document, TestImages.Decoder)!;
        var layer = document.Layers[0];
        switch (change)
        {
            case "position": layer.Bounds = layer.Bounds with { X = 400 }; break;
            case "size": layer.Bounds = layer.Bounds with { Width = 700 }; break;
            case "visible": layer.Visible = false; break;
            case "asset": document.Assets[layer.AssetId!.Value] = new AssetBlob(Solid(0xFFFFFF00)); break;
            case "canvas": document.CanvasHeight = 800; break;
            case "color": document.BackgroundArgb = 0xFFFFFFFF; break;
            case "blur": document.BackgroundBlend = document.BackgroundBlend with { BlurRadius = 20 }; break;
            case "darkness": document.BackgroundBlend = document.BackgroundBlend with { Darkness = 0.8 }; break;
        }
        var after = cache.Get(document, TestImages.Decoder)!;
        Assert.That(after, Is.Not.SameAs(before));
        Assert.That(after.ToArray(), Is.Not.EqualTo(before.ToArray()));
        Assert.That(after.ToArray(), Is.EqualTo(BackgroundComposer.Generate(document, TestImages.Decoder)));
        Assert.That(RenderPreview(document, cache), Is.EqualTo(TestImages.RenderPng(document)));
    }

    [Test]
    public void ForegroundOnlyAndHiddenImageEditsReuseBackground()
    {
        var document = MakeDocument();
        document.Layers[1].Visible = false;
        var cache = new BackgroundRenderCache();
        var before = cache.Get(document, TestImages.Decoder);
        document.Layers[0].Locked = true;
        document.Layers[0].Name = "Renamed";
        document.Layers[1].Bounds = new RectD(20, 20, 100, 100);
        document.Layers.Add(new Layer { Kind = LayerKind.Text, Text = "Foreground" });
        Assert.That(cache.Get(document, TestImages.Decoder), Is.SameAs(before));
    }

    [Test]
    public void FixedBackgroundWinsAndClearingItResumesAutomaticRendering()
    {
        var edit = new EditSession(MakeDocument());
        var original = TestImages.RenderPng(edit.Document);
        edit.SetFixedBackground(Solid(0xFF224466));
        var cache = new BackgroundRenderCache();
        Assert.That(cache.Get(edit.Document, TestImages.Decoder), Is.Null);
        var (_, _, fixedPixels) = TestImages.DecodePng(TestImages.RenderPng(edit.Document));
        Assert.That(fixedPixels[^4..], Is.EqualTo(new byte[] { 0x66, 0x44, 0x22, 255 }));
        edit.ClearFixedBackground();
        Assert.That(TestImages.RenderPng(edit.Document), Is.EqualTo(original));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(TestImages.DecodePng(TestImages.RenderPng(edit.Document)).Bgra, Is.EqualTo(fixedPixels));
    }

    [Test]
    public void HiddenLastArtworkFallsBackToBaseColorAndUndoRestoresBackground()
    {
        var edit = new EditSession(MakeDocument(1));
        var cache = new BackgroundRenderCache();
        var before = RenderPreview(edit.Document, cache);
        edit.UpdateLayer(edit.Document.Layers[0].Id, layer => layer.Visible = false);
        Assert.That(cache.Get(edit.Document, TestImages.Decoder), Is.Null);
        var (_, _, pixels) = TestImages.DecodePng(RenderPreview(edit.Document, cache));
        Assert.That(pixels[..4], Is.EqualTo(new byte[] { 0x20, 0x20, 0x20, 255 }));
        Assert.That(edit.Undo(), Is.True);
        Assert.That(RenderPreview(edit.Document, cache), Is.EqualTo(before));
    }

    [Test]
    public void MoveCancelCommitUndoAndRedoNeverLeaveStaleBackgroundOrExtraHistory()
    {
        var edit = new EditSession(MakeDocument());
        var cache = new BackgroundRenderCache();
        var before = RenderPreview(edit.Document, cache);
        var id = edit.Document.Layers[0].Id;
        edit.BeginGesture(id);
        edit.MoveBy(280, 60);
        var during = RenderPreview(edit.Document, cache);
        Assert.That(during, Is.Not.EqualTo(before));
        Assert.That(edit.UndoCount, Is.Zero);
        edit.CancelGesture();
        Assert.That(RenderPreview(edit.Document, cache), Is.EqualTo(before));
        edit.BeginGesture(id);
        edit.MoveBy(280, 60);
        edit.CommitGesture();
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(RenderPreview(edit.Document, cache), Is.EqualTo(during));
        edit.Undo();
        Assert.That(RenderPreview(edit.Document, cache), Is.EqualTo(before));
        edit.Redo();
        Assert.That(RenderPreview(edit.Document, cache), Is.EqualTo(during));
        Assert.That(TestImages.RenderPng(edit.Document), Is.EqualTo(during));
    }

    [Test]
    public void SettingsAreUndoableAndActualDesktopControlsRejectInvalidValues()
    {
        var document = MakeDocument();
        document.BackgroundBlend = new();
        var edit = new EditSession(document);
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var automatic = window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(control => control.Name == "AutomaticBackground");
            automatic.IsChecked = true;
            Assert.That(edit.Document.BackgroundBlend.Enabled, Is.True);
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            TextBox Field(string name) => window.GetLogicalDescendants().OfType<TextBox>()
                .Single(control => control.Name == name);
            var apply = UiTestActions.FindNamed(window, "ApplyBlendSettings");
            Field("BackgroundBlur").Text = "12";
            Field("BackgroundDarkness").Text = "65";
            UiTestActions.Click(apply);
            Assert.That(edit.Document.BackgroundBlend, Is.EqualTo(new BackgroundBlendSettings(true, 12, 0.65)));
            Assert.That(edit.UndoCount, Is.EqualTo(2));
            Field("BackgroundDarkness").Text = "101";
            UiTestActions.Click(apply);
            Assert.That(edit.Document.BackgroundBlend.Darkness, Is.EqualTo(0.65));
            Assert.That(edit.UndoCount, Is.EqualTo(2));
            edit.Undo();
            edit.Undo();
            Assert.That(edit.Document.StateEquals(document), Is.True);
        }
        finally { window.Close(); }
    }

    internal static EditorDocument MakeDocument(int count = 3, int width = 800, int height = 600)
    {
        AvaloniaTestHost.EnsureStarted();
        var document = new EditorDocument { CanvasWidth = width, CanvasHeight = height,
            BackgroundArgb = 0xFF202020,
            BackgroundBlend = new(true, IncludeFlatImages: true) };
        uint[] colors = [0xFFFF0000, 0xFF0000FF, 0xFF00FF00, 0xFFFFFFFF, 0xFF000000];
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            document.AddAsset(id, Solid(colors[i]));
            document.Layers.Add(new Layer { Kind = LayerKind.Image, AssetId = id,
                Bounds = new RectD(20 + i * 130, 20, 80, 100) });
        }
        return document;
    }

    internal static byte[] Solid(uint color)
    {
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)color; pixels[i + 1] = (byte)(color >> 8);
            pixels[i + 2] = (byte)(color >> 16); pixels[i + 3] = (byte)(color >> 24);
        }
        return TestImages.EncodePng(4, 4, pixels);
    }

    private static byte[] RenderPreview(EditorDocument document, BackgroundRenderCache cache)
    {
        using var target = new RenderTargetBitmap(new PixelSize(document.CanvasWidth, document.CanvasHeight), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
            ScenePainter.Paint(context, document, TestImages.Decoder, backgroundCache: cache);
        using var stream = new MemoryStream();
        target.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }
}
