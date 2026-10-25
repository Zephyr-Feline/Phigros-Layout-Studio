using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3KPreferencesTests
{
    private string _folder = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(),
            "r3k-preferences-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Test]
    public void PreferencesFileRoundTripsAndMissingFileUsesDefaults()
    {
        var path = Path.Combine(_folder, "nested", "Preferences.json");
        var store = new EditorPreferencesStore(path);
        var missing = store.Load();
        Assert.That(missing.Preferences, Is.EqualTo(EditorPreferences.Default));
        var preferences = new EditorPreferences
        {
            ShowRulers = false,
            MoveWholeGroup = false,
            ShowSafeArea = true,
            SafeAreaMarginPercent = 15,
            DefaultExportQualityPolicy = ExportQualityPolicy.BlockLowResolution
        };
        store.Save(preferences);
        Assert.That(store.Load().Preferences, Is.EqualTo(preferences));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"), Is.Empty);
    }

    [Test]
    public void CorruptOrFuturePreferencesFallBackWithoutOverwritingTheFile()
    {
        var path = Path.Combine(_folder, "Preferences.json");
        File.WriteAllText(path, "{\"formatVersion\":99}");
        var store = new EditorPreferencesStore(path);
        var result = store.Load();
        Assert.Multiple(() =>
        {
            Assert.That(result.Preferences, Is.EqualTo(EditorPreferences.Default));
            Assert.That(result.Warning, Is.Not.Null);
            Assert.That(File.ReadAllText(path), Is.EqualTo("{\"formatVersion\":99}"));
        });
        File.WriteAllText(path, "{ broken");
        var broken = store.Load();
        Assert.That(broken.Warning, Is.Not.Null);
        Assert.That(File.ReadAllText(path), Is.EqualTo("{ broken"));
    }

    [Test]
    public void VersionOnePreferencesLoadSafeAreaDefaultsAndRejectV2Fields()
    {
        var path = Path.Combine(_folder, "Preferences.json");
        File.WriteAllText(path, """
            {"formatVersion":1,"showRulers":false,"moveWholeGroup":false,
             "defaultExportQualityPolicy":"Notify"}
            """);
        var loaded = new EditorPreferencesStore(path).Load();
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Preferences.ShowRulers, Is.False);
            Assert.That(loaded.Preferences.ShowSafeArea, Is.False);
            Assert.That(loaded.Preferences.SafeAreaMarginPercent, Is.EqualTo(10));
        });
        File.WriteAllText(path, """
            {"formatVersion":1,"showRulers":false,"moveWholeGroup":false,
             "defaultExportQualityPolicy":"Notify","showSafeArea":true,
             "safeAreaMarginPercent":10}
            """);
        var rejected = new EditorPreferencesStore(path).Load();
        Assert.That(rejected.Warning, Is.Not.Null);
        Assert.That(rejected.Preferences, Is.EqualTo(EditorPreferences.Default));
    }

    [Test]
    public void MainWindowAppliesPersistedPreferencesAndExportDefaults()
    {
        AvaloniaTestHost.EnsureStarted();
        var path = Path.Combine(_folder, "Preferences.json");
        var store = new EditorPreferencesStore(path);
        store.Save(new EditorPreferences
        {
            ShowRulers = false,
            MoveWholeGroup = false,
            ShowSafeArea = true,
            SafeAreaMarginPercent = 15,
            DefaultExportQualityPolicy = ExportQualityPolicy.BlockLowResolution
        });
        var edit = new EditSession(new EditorDocument());
        var window = new MainWindow(edit, preferencesStore: store);
        try
        {
            window.Show();
            var showRulers = window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(check => check.Name == "ShowRulers");
            var moveGroup = window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(check => check.Name == "EditWholeGroup");
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            Assert.Multiple(() =>
            {
                Assert.That(showRulers.IsChecked, Is.False);
                Assert.That(moveGroup.IsChecked, Is.False);
                Assert.That(canvas.MoveWholeGroup, Is.False);
                Assert.That(canvas.ShowSafeArea, Is.True);
                Assert.That(canvas.SafeAreaMarginPercent, Is.EqualTo(15));
                Assert.That(window.GetVisualDescendants().OfType<CanvasRuler>()
                    .All(ruler => !ruler.IsVisible), Is.True);
            });
            var export = new PngExportDialog(edit.Document,
                defaultPolicy: store.Load().Preferences.DefaultExportQualityPolicy);
            export.Show();
            try
            {
                Assert.That(export.GetLogicalDescendants().OfType<CheckBox>()
                    .Single(check => check.Name == "PngBlockLowResolution").IsChecked, Is.True);
            }
            finally { export.Close(); }
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
        }
    }

    [Test]
    public void PreferencesDialogSavesAndAppliesTheNewValues()
    {
        AvaloniaTestHost.EnsureStarted();
        var path = Path.Combine(_folder, "Preferences.json");
        var store = new EditorPreferencesStore(path);
        var edit = new EditSession(new EditorDocument());
        var window = new MainWindow(edit, preferencesStore: store);
        try
        {
            window.Show();
            UiTestActions.ClickNamed(window, "OpenPreferences");
            PumpUntil(() => window.OwnedWindows.OfType<PreferencesDialog>().Any());
            var dialog = window.OwnedWindows.OfType<PreferencesDialog>().Single();
            dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(check => check.Name == "PreferenceShowRulers").IsChecked = false;
            dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(check => check.Name == "PreferenceMoveWholeGroup").IsChecked = false;
            dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(check => check.Name == "PreferenceShowSafeArea").IsChecked = true;
            dialog.GetLogicalDescendants().OfType<ComboBox>()
                .Single(combo => combo.Name == "PreferenceSafeAreaMargin").SelectedItem = "20%";
            dialog.GetLogicalDescendants().OfType<ComboBox>()
                .Single(combo => combo.Name == "PreferenceQualityPolicy").SelectedItem =
                ExportQualityPolicy.BlockLowResolution;
            UiTestActions.ClickNamed(dialog, "SavePreferences");
            PumpUntil(() => !window.OwnedWindows.OfType<PreferencesDialog>().Any());
            PumpUntil(() => store.Load().Preferences.DefaultExportQualityPolicy ==
                ExportQualityPolicy.BlockLowResolution);
            var loaded = store.Load().Preferences;
            Assert.Multiple(() =>
            {
                Assert.That(loaded.ShowRulers, Is.False);
                Assert.That(loaded.MoveWholeGroup, Is.False);
                Assert.That(loaded.ShowSafeArea, Is.True);
                Assert.That(loaded.SafeAreaMarginPercent, Is.EqualTo(20));
                Assert.That(loaded.DefaultExportQualityPolicy,
                    Is.EqualTo(ExportQualityPolicy.BlockLowResolution));
                Assert.That(window.GetVisualDescendants().OfType<SceneCanvas>().Single()
                    .MoveWholeGroup, Is.False);
                Assert.That(window.GetVisualDescendants().OfType<SceneCanvas>().Single()
                    .ShowSafeArea, Is.True);
            });
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
        }
    }

    [Test]
    public async Task CrossProjectQualityPolicyBlocksOnlyTheUpscaledProject()
    {
        var source = Path.Combine(_folder, "upscaled.ppgproj");
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        var asset = Guid.NewGuid();
        document.AddAsset(asset, TestImages.EncodePng(400, 240,
            TestImages.SyntheticBgra(400, 240)));
        document.Layers.Add(new Layer
        {
            Kind = LayerKind.Image, Name = "upscaled", AssetId = asset,
            Bounds = new RectD(100, 100, 800, 480)
        });
        NativePackageStore.Save(source, document, "r3k-test");
        var outputParent = Path.Combine(_folder, "output");
        Directory.CreateDirectory(outputParent);
        var rendered = false;
        var result = await ProjectRenderBatch.ExecuteAsync(
            new ProjectRenderPlan(outputParent, [source], [new PngExportTask(1000, false)]),
            _ => { },
            (_, _, _) => { rendered = true; return [1]; },
            (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
            new ProtectedSourcePaths(), () => null, _ => false,
            readImageDimensions: ImageAssetValidator.ReadEncodedDimensions,
            qualityPolicy: ExportQualityPolicy.BlockLowResolution).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(result.Projects.Single().Status, Is.EqualTo(ProjectRenderStatus.Failed));
            Assert.That(result.Projects.Single().Error, Does.Contain("quality policy"));
            Assert.That(result.Projects.Single().Tasks.Single().Status,
                Is.EqualTo(PngExportTaskStatus.Failed));
            Assert.That(rendered, Is.False);
            Assert.That(Directory.GetFiles(result.Directory, "*.png"), Is.Empty);
        });
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        Assert.That(manifest.RootElement.GetProperty("qualityPolicy").GetString(),
            Is.EqualTo("BlockLowResolution"));
    }

    [Test]
    public void SafeAreaOverlayNeverChangesPngOutput()
    {
        AvaloniaTestHost.EnsureStarted();
        var shape = new Layer
        {
            Kind = LayerKind.Shape, Bounds = new RectD(40, 40, 120, 80),
            ColorArgb = 0xFF22AA44
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [shape]
        });
        var canvas = new SceneCanvas(edit) { ShowSafeArea = true, SafeAreaMarginPercent = 20 };
        var png = PngExporter.Render(edit.Document);
        canvas.ShowSafeArea = false;
        Assert.That(PngExporter.Render(edit.Document), Is.EqualTo(png));
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(condition(), Is.True, "The UI operation did not complete.");
    }
}
