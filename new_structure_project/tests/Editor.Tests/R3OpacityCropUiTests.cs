using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3OpacityCropUiTests
{
    [Test]
    public void DesktopOpacityEditIsOneUndoAndCropDialogPersistsFitAndSampling()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var image = new Layer
        {
            Kind = LayerKind.Image, Name = "图片", AssetId = Guid.NewGuid(),
            Bounds = new RectD(40, 40, 240, 160)
        };
        var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360, Layers = [image] };
        document.AddAsset(image.AssetId!.Value,
            TestImages.EncodePng(20, 10, TestImages.SyntheticBgra(20, 10)));
        var edit = new EditSession(document) { SelectedLayerId = image.Id };
        var window = new MainWindow(edit, recovery: EmptyRecovery(edit));
        try
        {
            window.Show();
            var opacity = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerOpacity");
            opacity.Text = "50";
            UiTestActions.ClickNamed(window, "ApplyLayerProperties");
            Assert.That(edit.Document.Find(image.Id).Opacity, Is.EqualTo(.5));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            UiTestActions.ClickNamed(window, "Undo");
            Assert.That(edit.Document.Find(image.Id).Opacity, Is.EqualTo(1));

            UiTestActions.ClickNamed(window, "OpenImageCrop");
            PumpUntil(() => window.OwnedWindows.OfType<ImageCropDialog>().Any());
            var dialog = window.OwnedWindows.OfType<ImageCropDialog>().Single();
            dialog.GetLogicalDescendants().OfType<ComboBox>()
                .Single(box => box.Name == "ImageCropFit").SelectedItem = ImageFitMode.Contain;
            dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "ImageCropX").Text = "0.1";
            dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "ImageCropWidth").Text = "0.8";
            UiTestActions.ClickNamed(dialog, "ApplyImageCrop");
            PumpUntil(() => edit.Document.Find(image.Id).ImageFit == ImageFitMode.Contain);
            Assert.That(edit.Document.Find(image.Id).ImageCrop.X, Is.EqualTo(.1));
            Assert.That(edit.Document.Find(image.Id).ImageCrop.Width, Is.EqualTo(.8));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void CropDialogRejectsOutOfBoundsAndResetRestoresCompleteSource()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var owner = new Window();
        var layer = new Layer
        {
            Kind = LayerKind.Image, AssetId = Guid.NewGuid(),
            ImageCrop = new RectD(.2, .2, .5, .5), ImageFit = ImageFitMode.Cover
        };
        var dialog = new ImageCropDialog(layer);
        try
        {
            owner.Show();
            var pending = dialog.ShowDialog<ImageCropSettings?>(owner);
            dialog.UpdateLayout();
            var x = dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "ImageCropX");
            x.Text = "0.8";
            Dispatcher.UIThread.RunJobs();
            Assert.That(dialog.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "ApplyImageCrop").IsEnabled, Is.False);
            UiTestActions.ClickNamed(dialog, "ResetImageCrop");
            Assert.That(dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "ImageCropX").Text, Is.EqualTo("0"));
            Assert.That(dialog.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "ApplyImageCrop").IsEnabled, Is.True);
            UiTestActions.ClickNamed(dialog, "ApplyImageCrop");
            PumpUntil(() => pending.IsCompleted);
            var settings = pending.GetAwaiter().GetResult();
            Assert.That(settings, Is.Not.Null);
            Assert.That(settings!.Crop, Is.EqualTo(new RectD(0, 0, 1, 1)));
            Assert.That(settings.Fit, Is.EqualTo(ImageFitMode.Cover));
        }
        finally
        {
            foreach (var owned in owner.OwnedWindows.ToArray()) owned.Close();
            owner.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static AutoRecoveryService EmptyRecovery(EditSession edit) =>
        new(edit, Path.Combine(Path.GetTempPath(), "r3-opacity-recovery-" + Guid.NewGuid().ToString("N")[..8]),
            "r3-test", () => DateTimeOffset.Now, (_, _) => false);

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(condition(), Is.True, "UI operation did not complete.");
    }
}
