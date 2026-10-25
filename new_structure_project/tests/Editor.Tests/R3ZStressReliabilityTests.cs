using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3ZStressReliabilityTests
{
    [Test]
    public void MaximumCanvasAndLayerCountSurviveViewsGuidesHistoryAndNativeSave()
    {
        var built = BuildLargeDocument(7680, 4320, includeLargePayload: false);
        var original = built.Document.Copy();
        var edit = new EditSession(built.Document, maxHistoryEntries: 64);
        var imageLayer = edit.Document.Find(built.Ids[0]);
        var assetId = imageLayer.AssetId!.Value;
        var sharedBlob = edit.Document.Assets[assetId];
        edit.SetSelection([imageLayer.Id, built.Ids[11]]);

        using var host = new CanvasHost(edit, 1000, 600);
        Assert.Multiple(() =>
        {
            Assert.That(host.Canvas.ZoomToActualSize(), Is.True);
            Assert.That(host.Canvas.ZoomIn(), Is.True);
            Assert.That(host.Canvas.ZoomToSelection(), Is.True);
        });
        host.Canvas.FitView();
        Assert.That(host.Canvas.ManualGuides.Add(true, 640, 7680, 4320),
            Is.EqualTo(ManualGuideAddResult.Added));
        Assert.That(host.Canvas.ManualGuides.Add(false, 1080, 7680, 4320),
            Is.EqualTo(ManualGuideAddResult.Added));
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(edit.IsDirty, Is.False);
        Assert.That(edit.Document.StateEquals(original), Is.True);

        var moving = edit.Document.Find(built.Ids[11]);
        for (var index = 0; index < 90; index++)
        {
            edit.BeginGesture(moving.Id, includeGroup: false);
            edit.MoveBy(index % 2 == 0 ? 1 : 0, index % 2 == 0 ? 0 : 1);
            Assert.That(edit.CommitGesture(), Is.True);
            Assert.That(edit.Document.Assets[assetId], Is.SameAs(sharedBlob));
        }
        Assert.That(edit.UndoCount, Is.EqualTo(64));
        for (var index = 0; index < 25; index++)
        {
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Assets[assetId], Is.SameAs(sharedBlob));
            Assert.That(edit.Redo(), Is.True);
            Assert.That(edit.Document.Assets[assetId], Is.SameAs(sharedBlob));
        }
        Assert.That(host.Canvas.ManualGuides.Count, Is.EqualTo(2),
            "history operations never alter session guides");

        var final = edit.Document.Copy();
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3z-max-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, final, "r3z-test");
            Assert.That(NativePackageStore.Read(path).StateEquals(final), Is.True);
            using var archive = ZipFile.OpenRead(path);
            var uniquePayloads = final.Assets.Values
                .Select(asset => asset.Sha256).Distinct().Count();
            Assert.That(archive.Entries.Count(entry =>
                    entry.FullName.StartsWith("assets/", StringComparison.Ordinal)),
                Is.EqualTo(uniquePayloads),
                "one embedded payload per content hash, not per asset ID, layer or history state");
            Assert.That(edit.Document.StateEquals(final), Is.True);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void PayloadHistoryCleanupAndSelectionExportShareOneSnapshotWithoutLeaks()
    {
        var built = PayloadHistoryDocument();
        var edit = new EditSession(built.Document, maxHistoryEntries: 64);
        var image = edit.Document.Find(built.ImageLayerId);
        var assetId = image.AssetId!.Value;
        var blob = edit.Document.Assets[assetId];
        for (var index = 0; index < 180; index++)
        {
            edit.BeginGesture(built.MovingLayerId, includeGroup: false);
            edit.MoveBy(1, index % 2);
            edit.CommitGesture();
        }

        var distinctPayloads = new HashSet<AssetBlob>(ReferenceEqualityComparer.Instance);
        var undoStates = 0;
        while (edit.Undo())
        {
            undoStates++;
            distinctPayloads.Add(edit.Document.Assets[assetId]);
        }
        Assert.That(undoStates, Is.EqualTo(64));
        Assert.That(distinctPayloads, Has.Count.EqualTo(1),
            "all retained states must share the immutable image payload");
        while (edit.Redo()) { }

        var unused = Guid.NewGuid();
        edit.Document.AddAsset(unused, blob);
        var cleanup = edit.CleanUnreferencedAssets();
        Assert.Multiple(() =>
        {
            Assert.That(cleanup.Count, Is.EqualTo(1));
            Assert.That(edit.Document.Assets.ContainsKey(unused), Is.False);
            Assert.That(edit.UndoLabel, Is.EqualTo("清理未引用资源"));
        });
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.Document.Assets[unused], Is.SameAs(blob),
            "undo restores the removed resource by shared payload identity");
        Assert.That(edit.Redo(), Is.True);
        Assert.That(edit.Document.Assets.ContainsKey(unused), Is.False);

        var beforeExport = edit.Document.Copy();
        var undo = edit.UndoCount;
        var redo = edit.RedoCount;
        var selected = edit.Document.Layers.Select(layer => layer.Id).ToArray();
        var snapshot = SelectionProject.Create(edit.Document, selected, includeGroups: false);
        var region = PngExportRegion.FromLayers(snapshot, snapshot.Layers);
        var png = TestImages.RenderPngRegion(snapshot, true, 640, region);
        var (width, height, _) = TestImages.DecodePng(png);
        Assert.Multiple(() =>
        {
            Assert.That(edit.Document.StateEquals(beforeExport), Is.True);
            Assert.That((edit.UndoCount, edit.RedoCount), Is.EqualTo((undo, redo)));
            Assert.That(width, Is.EqualTo(640));
            Assert.That(height, Is.GreaterThan(0));
        });
    }

    [TestCase(5760, 4320, false)]
    [TestCase(5760, 4320, true)]
    [TestCase(7680, 2160, false)]
    [TestCase(7680, 2160, true)]
    public void RotatedFourByThreeAndUltrawideTransparentRendersAreDeterministic(
        int width, int height, bool transparent)
    {
        var built = BuildLargeDocument(width, height, includeLargePayload: false);
        var before = built.Document.Copy();
        var region = PngExportRegion.FromLayers(built.Document, built.Document.Layers);
        var first = TestImages.RenderPngRegion(built.Document, transparent, 960, region);
        var second = TestImages.RenderPngRegion(built.Document, transparent, 960, region);
        Assert.That(second, Is.EqualTo(first));
        var size = PngExportSize.Create(built.Document, 960, region);
        var (actualWidth, actualHeight, _) = TestImages.DecodePng(second);
        Assert.Multiple(() =>
        {
            Assert.That((actualWidth, actualHeight),
                Is.EqualTo((size.Width, size.Height)));
            Assert.That(built.Document.StateEquals(before), Is.True);
            Assert.That(before.Layers.Count, Is.EqualTo(200));
        });
    }

    [Test]
    public void StressInspectionDoesNotMutateDocumentOrCreateHistory()
    {
        var built = BuildLargeDocument(7680, 4320, includeLargePayload: false);
        var edit = new EditSession(built.Document);
        var before = edit.Document.Copy();
        var resources = AssetResources.Inspect(edit.Document,
            value => (32, 18));
        var cleanup = AssetResources.PlanCleanup(edit.Document);
        var warnings = ExportPreflight.Inspect(edit.Document,
            _ => false, 1280, _ => (32, 18), transparentBackground: false);
        Assert.Multiple(() =>
        {
            Assert.That(resources, Has.Count.EqualTo(before.Assets.Count));
            Assert.That(cleanup.Changed, Is.False);
            Assert.That(warnings, Is.Not.Null);
            Assert.That(edit.Document.StateEquals(before), Is.True);
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.RedoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
        });
    }

    [Test]
    public async Task LargeQueueRendersMultipleTasksWithoutChangingHistoryOrDocument()
    {
        var built = BuildLargeDocument(7680, 4320, includeLargePayload: false);
        var edit = new EditSession(built.Document, maxHistoryEntries: 16);
        for (var index = 0; index < 6; index++)
        {
            edit.BeginGesture(built.Ids[11], includeGroup: false);
            edit.MoveBy(1, 0);
            edit.CommitGesture();
        }
        var before = edit.Document.Copy();
        var undo = edit.UndoCount;
        var redo = edit.RedoCount;
        var region = PngExportRegion.FromLayers(edit.Document, edit.Document.Layers);
        var tasks = new[]
        {
            new PngExportTask(640, false),
            new PngExportTask(960, true)
        };
        var parent = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3z-queue-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(parent);
        try
        {
            var directory = PngExportQueue.CreateRunDirectory(parent, DateTime.Now);
            var result = await PngExportQueue.ExecuteRegionAsync(edit.Document, tasks, directory,
                TestImages.ValidateImages,
                (snapshot, transparent, width, outputRegion) =>
                    TestImages.RenderPngRegion(snapshot, transparent, width!.Value,
                        outputRegion!.Value),
                (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
                new ProtectedSourcePaths(), () => null,
                outputRegion: region).ConfigureAwait(false);
            Assert.Multiple(() =>
            {
                Assert.That(result.Items.Select(item => item.Status),
                    Is.All.EqualTo(PngExportTaskStatus.Completed));
                Assert.That(result.Items.Select(item => File.Exists(
                    Path.Combine(directory, item.FileName))), Is.All.True);
                Assert.That(edit.Document.StateEquals(before), Is.True);
                Assert.That((edit.UndoCount, edit.RedoCount), Is.EqualTo((undo, redo)));
            });
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static (EditorDocument Document, Guid[] Ids) BuildLargeDocument(
        int width, int height, bool includeLargePayload)
    {
        var document = new EditorDocument
        {
            CanvasWidth = width, CanvasHeight = height,
            BackgroundArgb = 0xFF17232D
        };
        var assets = new List<Guid>();
        for (var index = 0; index < 10; index++)
        {
            var assetId = Guid.NewGuid();
            document.AddAsset(assetId, TestImages.EncodePng(32, 18,
                TestImages.SyntheticBgra(32, 18)));
            assets.Add(assetId);
        }
        Guid? largeAsset = null;
        if (includeLargePayload)
        {
            largeAsset = Guid.NewGuid();
            document.AddAsset(largeAsset.Value,
                TestImages.EncodePng(512, 512,
                    TestImages.SyntheticBgra(512, 512)));
        }

        var ids = new Guid[200];
        var groups = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToArray();
        for (var index = 0; index < 200; index++)
        {
            var x = 30 + (index % 10) * (width - 300.0) / 10;
            var y = 30 + (index / 10) * (height - 120.0) / 20;
            Layer layer;
            if (index < 10)
            {
                layer = new Layer
                {
                    Kind = LayerKind.Image, AssetId = assets[index],
                    Name = $"image-{index}", Slanted = index % 2 == 0,
                    ImageFit = index % 3 == 0 ? ImageFitMode.Cover : ImageFitMode.Contain,
                    Bounds = new RectD(x, y, 220, 120)
                };
            }
            else if (index == 10 && largeAsset is Guid large)
            {
                layer = new Layer
                {
                    Kind = LayerKind.Image, AssetId = large,
                    Name = "large-image", Bounds = new RectD(x, y, 260, 150)
                };
            }
            else if (index < 100)
            {
                layer = new Layer
                {
                    Kind = LayerKind.Shape, Name = $"shape-{index}",
                    Bounds = new RectD(x, y, 210, 90),
                    Slanted = index % 7 == 0, Opacity = .55 + index % 5 * .09,
                    ColorArgb = index % 2 == 0 ? 0xFF2070A0 : 0xFFC04050
                };
            }
            else if (index < 140)
            {
                layer = EditorDocument.NewBadge(new PointD(x, y), 128);
                layer.Name = $"badge-{index}";
            }
            else
            {
                layer = new Layer
                {
                    Kind = LayerKind.Text, Name = $"text-{index}",
                    Text = $"压力测试 {index}", FontSize = 24,
                    Bounds = new RectD(x, y, 240, 80),
                    ColorArgb = 0xFFF0E8D8
                };
            }
            layer.Id = Guid.NewGuid();
            layer.GroupId = groups[index / 5];
            layer.RotationDegrees = index % 9 == 0 ? 23 : index % 13 == 0 ? -17 : 0;
            layer.Visible = index % 37 != 0;
            layer.Locked = index % 41 == 0;
            ids[index] = layer.Id;
            document.Layers.Add(layer);
        }
        document.FixedBackgroundAssetId = assets[0];
        document.Validate();
        return (document, ids);
    }

    private static (EditorDocument Document, Guid ImageLayerId, Guid MovingLayerId)
        PayloadHistoryDocument()
    {
        var document = new EditorDocument
        {
            CanvasWidth = 7680, CanvasHeight = 4320,
            BackgroundArgb = 0xFF17232D
        };
        var assetId = Guid.NewGuid();
        document.AddAsset(assetId, TestImages.EncodePng(512, 512,
            TestImages.SyntheticBgra(512, 512)));
        var image = new Layer
        {
            Kind = LayerKind.Image, Name = "payload image", AssetId = assetId,
            Bounds = new RectD(80, 80, 640, 480)
        };
        document.Layers.Add(image);
        Layer? moving = null;
        for (var index = 1; index < 200; index++)
        {
            var layer = new Layer
            {
                Kind = LayerKind.Shape, Name = $"shape-{index}",
                Bounds = new RectD(40 + index % 10 * 400, 40 + index / 10 * 120, 180, 80),
                ColorArgb = index % 2 == 0 ? 0xFF2070A0 : 0xFFC04050
            };
            moving ??= layer;
            document.Layers.Add(layer);
        }
        document.Validate();
        return (document, image.Id, moving!.Id);
    }

    private sealed class CanvasHost : IDisposable
    {
        public SceneCanvas Canvas { get; }
        public Window Window { get; }

        public CanvasHost(EditSession edit, double width, double height)
        {
            AvaloniaTestHost.EnsureStarted();
            Canvas = new SceneCanvas(edit);
            Window = new Window { Width = width, Height = height, Content = Canvas };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public void Dispose() => Window.Close();
    }
}
