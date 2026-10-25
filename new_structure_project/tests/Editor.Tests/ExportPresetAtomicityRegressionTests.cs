using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

[NonParallelizable]
public sealed class ExportPresetAtomicityRegressionTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void ApplyingAPresetWithAnInvalidOutputKeepsTheCompleteExistingQueue(
        bool crop, bool exceedsPixelLimit)
    {
        var document = crop
            ? new EditorDocument { CanvasWidth = 2740, CanvasHeight = 1080 }
            : exceedsPixelLimit
                ? new EditorDocument { CanvasWidth = 640, CanvasHeight = 640 }
                : new EditorDocument { CanvasWidth = 320, CanvasHeight = 4320 };
        var region = crop
            ? exceedsPixelLimit
                ? new PngExportRegion(0, 0, 1000, 1000)
                : new PngExportRegion(0, 0, 100, 1000)
            : (PngExportRegion?)null;
        var invalidWidth = exceedsPixelLimit ? 10000 : 2000;
        // Both widths are valid preset data. The second task fails only when projected onto
        // this canvas/region: either a 10000-square bitmap or an output over 16384px high.
        using var host = new Host(document, region,
            [new PngExportTask(400, false), new PngExportTask(invalidWidth, true)]);
        if (crop)
        {
            // The constructor's 2740px default is too tall for the narrow crop. Replace it
            // through the public queue actions before enabling that crop.
            if (!exceedsPixelLimit)
            {
                host.List.SelectedIndex = 0;
                host.Action("RemovePngQueueTask");
                host.AddTask(640, false);
            }
            host.Control<CheckBox>("PngQueueCropToSelection").IsChecked = true;
        }
        host.AddTask(800, true);
        host.ChooseDirectory();
        host.List.SelectedIndex = 0;
        var rows = host.List.Items.Cast<object>().ToArray();
        var selected = host.List.SelectedItem;
        var count = host.Control<TextBlock>("PngQueueTaskCount").Text;
        var directory = host.Control<TextBlock>("PngQueueDirectory").Text;
        var before = document.Copy();
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Length.EqualTo(2), "the starting queue is independently valid");
            Assert.That(host.Start.IsEnabled, Is.True);
            Assert.That(host.Policy.IsChecked, Is.False);
            Assert.That(host.Control<TextBlock>("PngQueueError").Text, Is.Empty);
            if (crop)
                Assert.That(host.Control<CheckBox>("PngQueueCropToSelection").IsChecked, Is.True);
        });

        host.Action("ApplyPngPreset");

        var status = host.Control<TextBlock>("PngPresetStatus").Text ?? "";
        var error = host.Control<TextBlock>("PngQueueError").Text ?? "";
        Assert.Multiple(() =>
        {
            Assert.That(host.List.Items.Cast<object>().ToArray(), Is.EqualTo(rows),
                "one failed task must not partially replace the original queue");
            Assert.That(host.List.SelectedIndex, Is.Zero);
            Assert.That(host.List.SelectedItem, Is.SameAs(selected));
            Assert.That(host.Control<TextBlock>("PngQueueTaskCount").Text, Is.EqualTo(count));
            Assert.That(host.Policy.IsChecked, Is.False,
                "a rejected preset must not apply its different quality policy");
            Assert.That(host.Control<TextBlock>("PngQueueDirectory").Text, Is.EqualTo(directory));
            Assert.That(host.Start.IsEnabled, Is.True);
            Assert.That(status, Does.Not.Contain("已应用预设"));
            Assert.That(status + error, Is.Not.Empty, "show why the preset was rejected");
            if (crop)
                Assert.That(host.Control<CheckBox>("PngQueueCropToSelection").IsChecked, Is.True);
            Assert.That(document.StateEquals(before), Is.True);
            Assert.That(Directory.GetFileSystemEntries(host.OutputDirectory), Is.Empty,
                "applying a preset only edits the queue");
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AValidPresetReplacesEveryTaskAndPolicyWithoutChangingTheOutputParent(bool crop)
    {
        var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 };
        var region = crop ? new PngExportRegion(10, 20, 200, 100) : (PngExportRegion?)null;
        using var host = new Host(document, region,
            [new PngExportTask(1280, false), new PngExportTask(960, true)]);
        if (crop) host.Control<CheckBox>("PngQueueCropToSelection").IsChecked = true;
        host.AddTask(800, true);
        host.ChooseDirectory();
        var directory = host.Control<TextBlock>("PngQueueDirectory").Text;
        var before = document.Copy();

        host.Action("ApplyPngPreset");

        var rows = host.List.Items.Cast<object>().Select(item => item.ToString()).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.EqualTo(crop
                ? new[] { "1280 × 640 · 普通背景 · preview-1280.png",
                    "960 × 480 · 透明背景 · preview-960-transparent.png" }
                : new[] { "1280 × 720 · 普通背景 · preview-1280.png",
                    "960 × 540 · 透明背景 · preview-960-transparent.png" }));
            Assert.That(host.Control<TextBlock>("PngQueueTaskCount").Text, Is.EqualTo("任务数：2"));
            Assert.That(host.Policy.IsChecked, Is.True);
            Assert.That(host.Control<TextBlock>("PngQueueDirectory").Text, Is.EqualTo(directory));
            Assert.That(host.Start.IsEnabled, Is.True);
            Assert.That(host.Control<TextBlock>("PngPresetStatus").Text, Does.Contain("已应用预设"));
            Assert.That(host.Control<TextBlock>("PngQueueError").Text, Is.Empty);
            if (crop)
                Assert.That(host.Control<CheckBox>("PngQueueCropToSelection").IsChecked, Is.True);
            Assert.That(document.StateEquals(before), Is.True);
            Assert.That(Directory.GetFileSystemEntries(host.OutputDirectory), Is.Empty);
        });
    }

    private sealed class Host : IDisposable
    {
        private readonly string _folder;
        public string OutputDirectory { get; }
        public PngExportQueueDialog Dialog { get; }
        public ListBox List => Control<ListBox>("PngQueueTaskList");
        public CheckBox Policy => Control<CheckBox>("PngQueueBlockLowResolution");
        public Button Start => Control<Button>("StartPngQueueExport");

        public Host(EditorDocument document, PngExportRegion? region,
            IReadOnlyList<PngExportTask> presetTasks)
        {
            AvaloniaTestHost.EnsureStarted();
            _folder = Path.Combine(Path.GetTempPath(),
                "export-preset-atomicity-" + Guid.NewGuid().ToString("N"));
            OutputDirectory = Path.Combine(_folder, "output-parent");
            Directory.CreateDirectory(OutputDirectory);
            var library = new ExportPresetLibrary(Path.Combine(_folder, "presets.json"));
            var preset = library.Save(null, "原子应用核验预设", presetTasks,
                ExportQualityPolicy.BlockLowResolution);
            Dialog = new PngExportQueueDialog(document, [],
                () => Task.FromResult<string?>(OutputDirectory), ExportQualityPolicy.Notify,
                library, region);
            var selector = Control<ComboBox>("PngPresetSelector");
            selector.SelectedItem = selector.ItemsSource!.Cast<ExportPreset>()
                .Single(item => item.Id == preset.Id);
        }

        public T Control<T>(string name) where T : Control =>
            Dialog.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

        public void AddTask(int width, bool transparent)
        {
            Control<TextBox>("PngQueueWidth").Text = width.ToString();
            Control<ComboBox>("PngQueueMode").SelectedIndex = transparent ? 1 : 0;
            Action("AddPngQueueTask");
        }

        public void ChooseDirectory() => Action("ChoosePngQueueDirectory");

        public void Action(string name)
        {
            UiTestActions.ClickNamed(Dialog, name);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Dialog.Close();
            Directory.Delete(_folder, recursive: true);
        }
    }
}
