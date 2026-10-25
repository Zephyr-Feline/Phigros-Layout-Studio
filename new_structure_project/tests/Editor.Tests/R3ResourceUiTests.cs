using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3ResourceUiTests
{
    [Test]
    public void DesktopResourcePanelLocatesAndSafelyReplacesAnAsset()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var document = Document(out var assetId, out var layerId);
        var edit = new EditSession(document);
        var window = new MainWindow(edit, recovery: EmptyRecovery(edit),
            filePathPicker: _ => Task.FromResult<string?>("replacement.png"),
            imageLoader: _ => Task.FromResult(new LoadedImage(
                new AssetBlob(TestImages.EncodePng(8, 6,
                    TestImages.SyntheticBgra(8, 6))), 8, 6)));
        try
        {
            window.Show();
            UiTestActions.ClickNamed(window, "OpenResourcePanel");
            PumpUntil(() => window.OwnedWindows.OfType<ResourcePanelDialog>().Any());
            var dialog = window.OwnedWindows.OfType<ResourcePanelDialog>().Single();
            var list = dialog.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "ResourceList");
            Assert.That(list.Items, Has.Count.EqualTo(1));
            UiTestActions.ClickNamed(dialog, "LocateResourceReference");
            PumpUntil(() => edit.SelectedLayerIds.Contains(layerId));
            UiTestActions.ClickNamed(dialog, "ReplaceResource");
            PumpUntil(() => edit.Document.Assets.Keys.All(id => id != assetId));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(edit.Document.Find(layerId).AssetId, Is.Not.EqualTo(assetId));
            UiTestActions.ClickNamed(dialog, "CloseResourcePanel");
            PumpUntil(() => !window.OwnedWindows.OfType<ResourcePanelDialog>().Any());
            PumpUntil(() => UiTestActions.Status(window)?.Contains("资源", StringComparison.Ordinal) == true);
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static EditorDocument Document(out Guid assetId, out Guid layerId)
    {
        assetId = Guid.NewGuid();
        var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 };
        document.AddAsset(assetId, P4AutomaticBackgroundTests.Solid(0xFFFF0000));
        var layer = new Layer
        {
            Kind = LayerKind.Image, Name = "资源图层", AssetId = assetId,
            Bounds = new RectD(20, 20, 200, 120)
        };
        layerId = layer.Id;
        document.Layers.Add(layer);
        document.Validate();
        return document;
    }

    private static AutoRecoveryService EmptyRecovery(EditSession edit) =>
        new(edit, Path.Combine(Path.GetTempPath(),
            "r3c-resource-recovery-" + Guid.NewGuid().ToString("N")[..8]),
            "r3c-test", () => DateTimeOffset.Now, (_, _) => false);

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(condition(), Is.True, "The UI operation did not complete.");
    }
}
