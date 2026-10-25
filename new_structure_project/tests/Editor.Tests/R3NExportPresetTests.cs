using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3NExportPresetTests
{
    private string _folder = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(),
            "r3n-presets-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Test]
    public void PresetLibraryRoundTripsUpdatesDeletesAndRejectsDuplicateNames()
    {
        var path = Path.Combine(_folder, "Presets.json");
        var library = new ExportPresetLibrary(path);
        Assert.That(library.Load().Presets, Is.Empty);
        var saved = library.Save(null, "HD", [
            new PngExportTask(1920, false), new PngExportTask(1920, true)
        ], ExportQualityPolicy.BlockLowResolution);
        var reloaded = new ExportPresetLibrary(path);
        var loaded = reloaded.Load();
        Assert.Multiple(() =>
        {
            Assert.That(loaded.Presets, Has.Count.EqualTo(1));
            Assert.That(loaded.Presets[0].Name, Is.EqualTo("HD"));
            Assert.That(loaded.Presets[0].Tasks, Has.Count.EqualTo(2));
            Assert.That(loaded.Presets[0].QualityPolicy,
                Is.EqualTo(ExportQualityPolicy.BlockLowResolution));
        });
        var updated = reloaded.Save(saved.Id, "HD 2K", [
            new PngExportTask(2560, false)
        ], ExportQualityPolicy.Notify);
        Assert.That(updated.Id, Is.EqualTo(saved.Id));
        Assert.That(() => reloaded.Save(null, "HD 2K",
            [new PngExportTask(1280, false)]), Throws.InstanceOf<InvalidOperationException>());
        Assert.That(reloaded.Delete(saved.Id), Is.True);
        Assert.That(new ExportPresetLibrary(path).Load().Presets, Is.Empty);
        Assert.That(Directory.GetFiles(_folder, "*.tmp"), Is.Empty);
    }

    [Test]
    public void VersionOnePresetLoadsWithNotifyAndRejectsV2Fields()
    {
        var path = Path.Combine(_folder, "Presets.json");
        File.WriteAllText(path, """
            {"formatVersion":1,"presets":[
              {"id":"0123456789abcdef0123456789abcdef","name":"旧预设",
               "tasks":[{"width":1280,"transparent":false}],"updatedUtc":"2026-10-17T00:00:00Z"}
            ]}
            """);
        var loaded = new ExportPresetLibrary(path).Load();
        Assert.That(loaded.Presets.Single().QualityPolicy, Is.EqualTo(ExportQualityPolicy.Notify));
        File.WriteAllText(path, """
            {"formatVersion":1,"presets":[
              {"id":"0123456789abcdef0123456789abcdef","name":"旧预设",
               "tasks":[{"width":1280,"transparent":false}],
               "updatedUtc":"2026-10-17T00:00:00Z",
               "qualityPolicy":"Notify"}
            ]}
            """);
        var rejected = new ExportPresetLibrary(path).Load();
        Assert.That(rejected.Presets, Is.Empty);
        Assert.That(rejected.Warning, Is.Not.Null);
    }

    [Test]
    public void InvalidOrFuturePresetFilesFallBackWithoutOverwriting()
    {
        var path = Path.Combine(_folder, "Presets.json");
        File.WriteAllText(path, "{\"formatVersion\":99,\"presets\":[]}");
        var result = new ExportPresetLibrary(path).Load();
        Assert.Multiple(() =>
        {
            Assert.That(result.Presets, Is.Empty);
            Assert.That(result.Warning, Is.Not.Null);
            Assert.That(File.ReadAllText(path),
                Is.EqualTo("{\"formatVersion\":99,\"presets\":[]}"));
        });
        File.WriteAllText(path, "not json");
        Assert.That(new ExportPresetLibrary(path).Load().Warning, Is.Not.Null);
        Assert.That(File.ReadAllText(path), Is.EqualTo("not json"));
    }

    [Test]
    public void QueueDialogAppliesSavesAndDeletesAPreset()
    {
        AvaloniaTestHost.EnsureStarted();
        var path = Path.Combine(_folder, "Presets.json");
        var library = new ExportPresetLibrary(path);
        var saved = library.Save(null, "已有预设", [
            new PngExportTask(1920, false), new PngExportTask(1920, true)
        ], ExportQualityPolicy.BlockLowResolution);
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        var dialog = new PngExportQueueDialog(document, Array.Empty<ExportWarning>(),
            () => Task.FromResult<string?>(null), ExportQualityPolicy.Notify, library);
        try
        {
            dialog.Show();
            var selector = dialog.GetLogicalDescendants().OfType<ComboBox>()
                .Single(combo => combo.Name == "PngPresetSelector");
            selector.SelectedItem = selector.ItemsSource!.Cast<object>()
                .Single(item => item.ToString()!.Contains("已有预设", StringComparison.Ordinal));
            UiTestActions.ClickNamed(dialog, "ApplyPngPreset");
            Dispatcher.UIThread.RunJobs();
            var tasks = dialog.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "PngQueueTaskList");
            Assert.That(tasks.Items.Count, Is.EqualTo(2));
            Assert.That(dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(check => check.Name == "PngQueueBlockLowResolution").IsChecked, Is.True);

            var width = dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "PngQueueWidth");
            width.Text = "2560";
            UiTestActions.ClickNamed(dialog, "AddPngQueueTask");
            UiTestActions.ClickNamed(dialog, "SavePngPreset");
            PumpUntil(() => dialog.OwnedWindows.OfType<NamePromptDialog>().Any());
            var prompt = dialog.OwnedWindows.OfType<NamePromptDialog>().Single();
            prompt.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "NamePromptValue").Text = "导出预设 UI";
            UiTestActions.ClickNamed(prompt, "ConfirmNamePrompt");
            PumpUntil(() => library.Load().Presets.Any(preset =>
                preset.Name == "导出预设 UI" && preset.Tasks.Count == 3 &&
                preset.QualityPolicy == ExportQualityPolicy.BlockLowResolution));
            Assert.That(library.Load().Presets.Single().Id, Is.EqualTo(saved.Id));

            UiTestActions.ClickNamed(dialog, "DeletePngPreset");
            PumpUntil(() => library.Load().Presets.Count == 0);
            Assert.That(library.Load().Presets, Is.Empty);
        }
        finally { dialog.Close(); }
    }

    [Test]
    public void CrossProjectDialogAppliesPresetTaskAndPolicy()
    {
        AvaloniaTestHost.EnsureStarted();
        var path = Path.Combine(_folder, "Presets.json");
        var library = new ExportPresetLibrary(path);
        library.Save(null, "跨工程预设", [
            new PngExportTask(1280, false), new PngExportTask(2560, true)
        ], ExportQualityPolicy.BlockLowResolution);
        var dialog = new ProjectBatchRenderDialog(
            () => Task.FromResult<IReadOnlyList<string>>([]),
            () => Task.FromResult<string?>(null),
            ExportQualityPolicy.Notify, library);
        try
        {
            dialog.Show();
            var selector = dialog.GetLogicalDescendants().OfType<ComboBox>()
                .Single(combo => combo.Name == "ProjectBatchPresetSelector");
            selector.SelectedItem = selector.ItemsSource!.Cast<object>()
                .Single(item => item.ToString()!.Contains("跨工程预设", StringComparison.Ordinal));
            UiTestActions.ClickNamed(dialog, "ApplyProjectBatchPreset");
            Dispatcher.UIThread.RunJobs();
            var tasks = dialog.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "ProjectBatchTaskList");
            Assert.Multiple(() =>
            {
                Assert.That(tasks.Items.Count, Is.EqualTo(2));
                Assert.That(dialog.GetLogicalDescendants().OfType<CheckBox>()
                    .Single(check => check.Name == "ProjectBatchBlockLowResolution").IsChecked,
                    Is.True);
            });
        }
        finally { dialog.Close(); }
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
