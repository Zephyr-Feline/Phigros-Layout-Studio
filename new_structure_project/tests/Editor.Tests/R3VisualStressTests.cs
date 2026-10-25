using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3VisualStressTests
{
    [TestCase(320, 180)]
    [TestCase(1366, 768)]
    [TestCase(2560, 1080)]
    [TestCase(1080, 1920)]
    public void StrongContrastTranslucentRotatedArtworkSurvivesV8RoundTrip(
        int width, int height)
    {
        var document = new EditorDocument
        {
            CanvasWidth = width,
            CanvasHeight = height,
            BackgroundArgb = 0xCC102030,
            BackgroundBlend = new(true, 7, 0.35, IncludeFlatImages: true,
                BackgroundContributionMode.Selected,
                null, 0.8, 0.35)
        };
        var ids = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var asset = Guid.NewGuid();
            var pixels = TestImages.SyntheticBgra(12 + index * 5, 8 + index * 3);
            for (var at = 3; at < pixels.Length; at += 4)
                pixels[at] = index == 1 ? (byte)96 : (byte)(180 + index * 20);
            document.AddAsset(asset, TestImages.EncodePng(12 + index * 5, 8 + index * 3, pixels));
            var layer = new Layer
            {
                Kind = LayerKind.Image, Name = $"stress artwork {index + 1}",
                AssetId = asset, Slanted = index != 1, Opacity = index == 1 ? 0.55 : 0.8,
                RotationDegrees = index == 2 ? 23 : 0,
                Bounds = new RectD(30 + index * width / 5.0, 30 + index * height / 8.0,
                    width / 5.0, height / 4.0)
            };
            document.Layers.Add(layer);
            ids.Add(layer.Id);
        }
        document.BackgroundBlend = document.BackgroundBlend with
            { SelectedArtworkLayerIds = [ids[0], ids[2]] };
        document.Layers.Add(new Layer
        {
            Kind = LayerKind.Text, Text = "强对比 / translucent / rotated",
            Bounds = new RectD(width * 0.08, height * 0.72, width * 0.7, height * 0.12),
            FontSize = Math.Max(12, height / 24.0), ColorArgb = 0xFFF2E4C8
        });
        document.Layers.Add(EditorDocument.NewBadge(
            new PointD(width * 0.76, height * 0.1), Math.Max(20, width / 20.0)));
        document.Validate();
        var png = TestImages.RenderPng(document);
        var path = Path.Combine(Path.GetTempPath(),
            "r3c-visual-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, document, "r3c-visual");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.StateEquals(document), Is.True);
            Assert.That(TestImages.RenderPng(reopened), Is.EqualTo(png));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
