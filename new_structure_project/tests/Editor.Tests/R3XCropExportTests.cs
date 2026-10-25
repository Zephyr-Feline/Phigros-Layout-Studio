using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3XCropExportTests
{
    [Test]
    public void RegionUsesRotatedVisualUnionRasterEdgesAndSelectedScope()
    {
        var first = new Layer
        {
            Kind = LayerKind.Shape,
            Bounds = new RectD(10.2, 20.4, 100.3, 80.2),
            RotationDegrees = 30
        };
        var hiddenLocked = new Layer
        {
            Kind = LayerKind.Shape,
            Bounds = new RectD(300.75, 200.2, 50.5, 20.5),
            RotationDegrees = 90,
            Visible = false,
            Locked = true
        };
        var document = new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [first, hiddenLocked]
        };
        var layers = document.Layers.Select(LayerGeometry.VisualBounds).ToArray();
        var union = MoveSnapping.BoundsOf(layers);
        var left = Math.Clamp(Math.Floor(union.X), 0, document.CanvasWidth);
        var top = Math.Clamp(Math.Floor(union.Y), 0, document.CanvasHeight);
        var right = Math.Clamp(Math.Ceiling(union.X + union.Width), 0, document.CanvasWidth);
        var bottom = Math.Clamp(Math.Ceiling(union.Y + union.Height), 0, document.CanvasHeight);
        var expected = new PngExportRegion(
            left, top, right - left, bottom - top);
        var region = PngExportRegion.FromLayers(document, document.Layers);

        Assert.Multiple(() =>
        {
            Assert.That(region, Is.EqualTo(expected));
            Assert.That(region.Right, Is.GreaterThanOrEqualTo(315.75),
                "the hidden, locked member contributes geometry");
            Assert.That(region.Width, Is.EqualTo(Math.Floor(region.Width)));
            Assert.That(region.Height, Is.EqualTo(Math.Floor(region.Height)));
        });

        var snapped = PngExportRegion.FromLayers(document, [new Layer
        {
            Kind = LayerKind.Shape,
            Bounds = new RectD(10.000000000000002, 20, 89.99999999999999, 60)
        }]);
        Assert.That(snapped, Is.EqualTo(new PngExportRegion(10, 20, 90, 60)));
    }

    [Test]
    public void RegionClampsToCanvasRejectsEmptyScopesAndTracksWholeGroups()
    {
        var clipped = new Layer
        {
            Kind = LayerKind.Shape, Bounds = new RectD(-20, -10, 50, 40)
        };
        var document = new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [clipped]
        };
        Assert.That(PngExportRegion.FromLayers(document, [clipped]),
            Is.EqualTo(new PngExportRegion(0, 0, 30, 30)));
        Assert.That(() => PngExportRegion.FromLayers(document, []),
            Throws.InstanceOf<InvalidOperationException>());
        var outside = new Layer
        {
            Kind = LayerKind.Shape, Bounds = new RectD(700, 400, 20, 20)
        };
        document.Layers.Add(outside);
        Assert.That(() => PngExportRegion.FromLayers(document, [outside]),
            Throws.InstanceOf<InvalidOperationException>());

        var group = Guid.NewGuid();
        var selected = new Layer
        {
            Kind = LayerKind.Shape, GroupId = group,
            Bounds = new RectD(40, 40, 120, 80)
        };
        var distantHidden = new Layer
        {
            Kind = LayerKind.Text, GroupId = group, Text = "hidden",
            Visible = false, Locked = true,
            Bounds = new RectD(420.2, 250.3, 140, 60)
        };
        var groupedDocument = new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360,
            Layers = [selected, distantHidden]
        };
        var snapshot = SelectionProject.Create(groupedDocument, [selected.Id], includeGroups: true);
        var region = PngExportRegion.FromLayers(snapshot, snapshot.Layers);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Layers, Has.Count.EqualTo(2));
            Assert.That(region.X, Is.EqualTo(40));
            Assert.That(region.Y, Is.EqualTo(40));
            Assert.That(region.Right, Is.EqualTo(561));
            Assert.That(region.Bottom, Is.EqualTo(311));
        });
    }

    [TestCase(640, 360, false)]
    [TestCase(640, 360, true)]
    [TestCase(800, 600, false)]
    [TestCase(800, 600, true)]
    [TestCase(1600, 450, false)]
    [TestCase(1600, 450, true)]
    public void CroppedRenderEqualsATranslatedCanvasAcrossAspectRatios(
        int width, int height, bool transparent)
    {
        var document = new EditorDocument
        {
            CanvasWidth = width, CanvasHeight = height,
            BackgroundArgb = 0xFF123456,
            Layers =
            [
                new Layer
                {
                    Kind = LayerKind.Shape, ColorArgb = 0xFFCF1313,
                    Bounds = new RectD(width * .1 + .3, 40.2, 220.4, 110.6),
                    RotationDegrees = 25
                },
                new Layer
                {
                    Kind = LayerKind.Shape, ColorArgb = 0xFF20C060,
                    Bounds = new RectD(width * .55 + .1, height * .4 + .2, 180.2, 90.4),
                    RotationDegrees = -15
                }
            ]
        };
        var region = PngExportRegion.FromLayers(document, document.Layers);
        var translated = document.Copy();
        translated.CanvasWidth = (int)region.Width;
        translated.CanvasHeight = (int)region.Height;
        foreach (var layer in translated.Layers)
            layer.Bounds = layer.Bounds with
            {
                X = layer.Bounds.X - region.X,
                Y = layer.Bounds.Y - region.Y
            };

        var expected = TestImages.RenderPng(translated, transparent, 400);
        var actual = TestImages.RenderPngRegion(document, transparent, 400, region);
        var repeated = TestImages.RenderPngRegion(document, transparent, 400, region);
        Assert.That(actual, Is.EqualTo(repeated), "the same region render is deterministic");
        var (actualWidth, actualHeight, _) = TestImages.DecodePng(actual);
        var size = PngExportSize.Create(document, 400, region);
        Assert.That((actualWidth, actualHeight), Is.EqualTo((size.Width, size.Height)));
        AssertInteriorPixelsEqual(expected, actual);
    }

    [Test]
    public void SelectionCropKeepsScopeButExcludesFixedAndAutomaticBackground()
    {
        var background = Guid.NewGuid();
        var image = Guid.NewGuid();
        var group = Guid.NewGuid();
        var artwork = new Layer
        {
            Kind = LayerKind.Image, AssetId = image, GroupId = group,
            Bounds = new RectD(50, 40, 220, 120)
        };
        var hidden = new Layer
        {
            Kind = LayerKind.Shape, GroupId = group, Visible = false, Locked = true,
            Bounds = new RectD(320, 210, 120, 60)
        };
        var source = new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360,
            FixedBackgroundAssetId = background,
            BackgroundBlend = new(true, 18, .8, true),
            Layers = [artwork, hidden]
        };
        source.AddAsset(image,
            TestImages.EncodePng(8, 4, TestImages.SyntheticBgra(8, 4)));
        source.AddAsset(background,
            TestImages.EncodePng(4, 2, TestImages.SyntheticBgra(4, 2)));

        var snapshot = SelectionProject.Create(source, [artwork.Id], includeGroups: true);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.FixedBackgroundAssetId, Is.Null);
            Assert.That(snapshot.BackgroundBlend.Enabled, Is.False);
            Assert.That(snapshot.Layers.Select(layer => layer.Id),
                Is.EqualTo(new[] { artwork.Id, hidden.Id }));
        });
        var region = PngExportRegion.FromLayers(snapshot, snapshot.Layers);
        Assert.That(region, Is.EqualTo(new PngExportRegion(50, 40, 390, 230)));
    }

    [Test]
    public async Task QueueRendersEveryCropTaskFromOneUnchangedSnapshot()
    {
        var document = new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360,
            Layers =
            [
                new Layer { Kind = LayerKind.Shape, Bounds = new RectD(90.2, 40.2, 220.4, 110.6) },
                new Layer { Kind = LayerKind.Shape, Bounds = new RectD(380.1, 200.2, 180.2, 90.4) }
            ]
        };
        var region = PngExportRegion.FromLayers(document, document.Layers);
        var before = document.Copy();
        var tasks = new[] { new PngExportTask(400, false), new PngExportTask(480, true) };
        var expected = tasks.ToDictionary(task => (task.Width, task.Transparent),
            task => TestImages.RenderPngRegion(document, task.Transparent, task.Width, region));
        var parent = ParentFolder();
        try
        {
            var directory = PngExportQueue.CreateRunDirectory(parent, DateTime.Now);
            var result = await PngExportQueue.ExecuteRegionAsync(document, tasks, directory,
                TestImages.ValidateImages,
                (snapshot, transparent, width, outputRegion) =>
                {
                    Assert.That(outputRegion, Is.EqualTo(region));
                    return TestImages.RenderPngRegion(snapshot, transparent, width!.Value, outputRegion!.Value);
                },
                (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
                new ProtectedSourcePaths(), () => null,
                outputRegion: region).ConfigureAwait(false);
            Assert.That(result.Items.Select(item => item.Status),
                Is.All.EqualTo(PngExportTaskStatus.Completed));
            foreach (var item in result.Items)
            {
                var path = Path.Combine(directory, item.FileName);
                Assert.That(File.ReadAllBytes(path),
                    Is.EqualTo(expected[(item.Task.Width, item.Task.Transparent)]));
                var size = PngExportSize.Create(document, item.Task.Width, region);
                Assert.That((item.Size.Width, item.Size.Height),
                    Is.EqualTo((size.Width, size.Height)));
            }
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
            Assert.That(manifest.RootElement.GetProperty("formatVersion").GetInt32(), Is.EqualTo(2));
            Assert.That(manifest.RootElement.GetProperty("tasks").EnumerateArray()
                .Select(item => item.GetProperty("height").GetInt32()),
                Is.EqualTo(result.Items.Select(item => item.Size.Height)));
            Assert.That(document.StateEquals(before), Is.True);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Test]
    public void QueueDialogDefaultsToFullCanvasAndReturnsTheSelectedCrop()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var document = new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360,
            Layers =
            [
                new Layer { Kind = LayerKind.Shape, Bounds = new RectD(90.2, 40.2, 220.4, 110.6) },
                new Layer { Kind = LayerKind.Shape, Bounds = new RectD(380.1, 200.2, 180.2, 90.4) }
            ]
        };
        var region = PngExportRegion.FromLayers(document, document.Layers);
        var parent = ParentFolder();
        var owner = new Window { Width = 400, Height = 300 };
        var dialog = new PngExportQueueDialog(document, [], () => Task.FromResult<string?>(parent),
            selectionCropRegion: region,
            selectionCropNote: "裁切范围使用旋转后可视并集。");
        try
        {
            owner.Show();
            var pending = dialog.ShowDialog<PngExportQueueRequest?>(owner);
            dialog.UpdateLayout();
            var list = dialog.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "PngQueueTaskList");
            Assert.That(list.Items.Cast<object>().Single().ToString(), Does.Contain("640 × 360"),
                "full canvas remains the default");
            var crop = dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "PngQueueCropToSelection");
            crop.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            var size = PngExportSize.Create(document, 640, region);
            Assert.That(list.Items.Cast<object>().Single().ToString(),
                Does.Contain($"{size.Width} × {size.Height}"));
            Assert.That(dialog.GetLogicalDescendants().OfType<TextBlock>()
                .Single(text => text.Name == "PngQueueCropNote").Text,
                Does.Contain("旋转后可视并集"));

            UiTestActions.ClickNamed(dialog, "ChoosePngQueueDirectory");
            PumpUntil(() => dialog.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "StartPngQueueExport").IsEnabled);
            UiTestActions.ClickNamed(dialog, "StartPngQueueExport");
            PumpUntil(() => pending.IsCompleted);
            var request = pending.GetAwaiter().GetResult();
            Assert.Multiple(() =>
            {
                Assert.That(request, Is.Not.Null);
                Assert.That(request!.CropRegion, Is.EqualTo(region));
                Assert.That(request.Tasks.Single().Width, Is.EqualTo(640));
            });
        }
        finally
        {
            foreach (var window in owner.OwnedWindows.ToArray()) window.Close();
            owner.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Test]
    public void DesktopSelectedExportUsesTheCropModeWithoutEditingTheProject()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var document = new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360,
            Layers =
            [
                new Layer { Kind = LayerKind.Shape, Bounds = new RectD(90.2, 40.2, 220.4, 110.6) },
                new Layer { Kind = LayerKind.Shape, Bounds = new RectD(380.1, 200.2, 180.2, 90.4) }
            ]
        };
        var region = PngExportRegion.FromLayers(document, document.Layers);
        var parent = ParentFolder();
        var edit = new EditSession(document);
        edit.SetSelection(document.Layers.Select(layer => layer.Id));
        var before = edit.Document.Copy();
        var window = new MainWindow(edit,
            exportDirectoryPicker: () => Task.FromResult<string?>(parent));
        try
        {
            window.Show();
            UiTestActions.ClickNamed(window, "BatchExportSelectionPng");
            PumpUntil(() => window.OwnedWindows.OfType<PngExportQueueDialog>().Any());
            var setup = window.OwnedWindows.OfType<PngExportQueueDialog>().Single();
            var crop = setup.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "PngQueueCropToSelection");
            crop.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            UiTestActions.ClickNamed(setup, "ChoosePngQueueDirectory");
            PumpUntil(() => setup.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "StartPngQueueExport").IsEnabled);
            UiTestActions.ClickNamed(setup, "StartPngQueueExport");
            PumpUntil(() => window.OwnedWindows.OfType<PngExportProgressDialog>().Any());
            var progress = window.OwnedWindows.OfType<PngExportProgressDialog>().Single();
            PumpUntil(() => progress.Result is not null);
            var result = progress.Result!;
            var expected = PngExportSize.Create(document, document.CanvasWidth, region);
            Assert.Multiple(() =>
            {
                Assert.That(result.Items.Single().Status, Is.EqualTo(PngExportTaskStatus.Completed));
                Assert.That((result.Items.Single().Size.Width, result.Items.Single().Size.Height),
                    Is.EqualTo((expected.Width, expected.Height)));
                Assert.That(edit.Document.StateEquals(before), Is.True);
                Assert.That(edit.IsDirty, Is.False);
                Assert.That(edit.UndoCount, Is.Zero);
            });
            var (width, height, _) = TestImages.DecodePng(
                File.ReadAllBytes(Path.Combine(result.Directory, result.Items.Single().FileName)));
            Assert.That((width, height), Is.EqualTo((expected.Width, expected.Height)));
            UiTestActions.ClickNamed(progress, "ClosePngQueueProgress");
            PumpUntil(() => window.OwnedWindows.Count == 0);
        }
        finally
        {
            edit.MarkSaved();
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Test]
    public void CliCropMatchesTheGuiRendererAndRejectsInvalidRegions()
    {
        AvaloniaTestHost.EnsureStarted();
        var document = new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360,
            BackgroundArgb = 0xFF212121,
            Layers =
            [
                new Layer
                {
                    Kind = LayerKind.Shape, ColorArgb = 0xFFCF1313,
                    Bounds = new RectD(90.2, 40.2, 220.4, 110.6), RotationDegrees = 23
                },
                new Layer
                {
                    Kind = LayerKind.Shape, ColorArgb = 0xFF20C060,
                    Bounds = new RectD(380.1, 200.2, 180.2, 90.4)
                }
            ]
        };
        var region = PngExportRegion.FromLayers(document, document.Layers);
        var parent = ParentFolder();
        var source = Path.Combine(parent, "source.ppgproj");
        var destination = Path.Combine(parent, "crop.png");
        try
        {
            NativePackageStore.Save(source, document, "r3x-test");
            var bytes = File.ReadAllBytes(source);
            var output = new StringWriter();
            var error = new StringWriter();
            var code = RenderCommand.Run(["--render", source, destination,
                "--width", "400", "--crop",
                $"{region.X},{region.Y},{region.Width},{region.Height}"],
                output, error, () => { });
            Assert.That(code, Is.Zero, error.ToString());
            Assert.That(File.ReadAllBytes(destination),
                Is.EqualTo(TestImages.RenderPngRegion(document, false, 400, region)));
            Assert.That(output.ToString(), Does.Contain("400 x"));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(bytes));

            var naturalDestination = Path.Combine(parent, "natural.png");
            var natural = RenderCommand.Run(["--render", source, naturalDestination,
                "--crop", $"{region.X},{region.Y},{region.Width},{region.Height}"],
                new StringWriter(), new StringWriter(), () => { });
            Assert.That(natural, Is.Zero);
            Assert.That(File.ReadAllBytes(naturalDestination),
                Is.EqualTo(TestImages.RenderPngRegion(document, false,
                    (int)region.Width, region)));

            var invalidDestination = Path.Combine(parent, "invalid.png");
            var invalidError = new StringWriter();
            var invalid = RenderCommand.Run(["--render", source, invalidDestination,
                "--crop", "600,300,100,100"], new StringWriter(), invalidError, () => { });
            Assert.That(invalid, Is.EqualTo(1));
            Assert.That(invalidError.ToString(), Does.Contain("RENDER FAILED"));
            Assert.That(File.Exists(invalidDestination), Is.False);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [TestCase("--crop")]
    [TestCase("--crop|1,2,3")]
    [TestCase("--crop|0,0,0,10")]
    [TestCase("--crop|-1,0,10,10")]
    [TestCase("--crop|NaN,0,10,10")]
    [TestCase("--crop|0,0,10,10|--crop|0,0,10,10")]
    public void MalformedCropArgumentsReturnUsageBeforeGraphicsInitialization(string suffix)
    {
        var error = new StringWriter();
        string[] args = ["--render", "input", "output.png", .. suffix.Split('|')];
        var code = RenderCommand.Run(args, new StringWriter(), error,
            () => throw new AssertionException("Unexpected graphics initialization"));
        Assert.That(code, Is.EqualTo(2));
        Assert.That(error.ToString(), Does.Contain("Usage:"));
    }

    private static string ParentFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(),
            "r3x-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void AssertInteriorPixelsEqual(byte[] expected, byte[] actual)
    {
        var (width, height, first) = TestImages.DecodePng(expected);
        var (otherWidth, otherHeight, second) = TestImages.DecodePng(actual);
        Assert.That((otherWidth, otherHeight), Is.EqualTo((width, height)));
        // A translated canvas ends its background exactly on the output edge; the region
        // renderer deliberately fills past that edge before bitmap clipping. Compare the
        // deterministic interior and require repeated region renders to be byte-identical.
        for (var y = 1; y < height - 1; y++)
        for (var x = 1; x < width - 1; x++)
        {
            var offset = (y * width + x) * 4;
            Assert.That(second.AsSpan(offset, 4).SequenceEqual(first.AsSpan(offset, 4)),
                Is.True, $"pixel ({x}, {y})");
        }
    }

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
