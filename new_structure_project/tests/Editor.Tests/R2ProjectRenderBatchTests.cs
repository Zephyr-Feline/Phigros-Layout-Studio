using System.Text.Json;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R2ProjectRenderBatchTests
{
    private string _folder = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "r2-project-batch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Test]
    public async Task MultipleProjectsKeepTheirOwnCanvasAndProduceUniqueFiles()
    {
        var projectA = Project("Alpha", 640, 360, 0xFF102030, "A");
        var projectB = Project("Beta", 480, 480, 0xFF203040, "B", font: "missing-font");
        var sources = new ProtectedSourcePaths();
        var tasks = new[] { new PngExportTask(320, false), new PngExportTask(320, true) };
        var result = await ProjectRenderBatch.ExecuteAsync(
            new ProjectRenderPlan(Output("exports"), [projectA, projectB], tasks),
            TestImages.ValidateImages,
            (document, transparent, width) => TestImages.RenderPng(document, transparent, width),
            (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
            sources, () => null, family => family == "missing-font").ConfigureAwait(false);
        Assert.That(result.Cancelled, Is.False);
        Assert.That(result.Projects.All(project => project.Status == ProjectRenderStatus.Completed), Is.True);
        Assert.That(result.Projects.SelectMany(project => project.Tasks).All(task =>
            task.Status == PngExportTaskStatus.Completed), Is.True);
        Assert.That(result.Projects[1].Warnings.Any(warning =>
            warning.Kind == ExportWarningKind.MissingLocalFont), Is.True);
        Assert.That(sources.Contains(projectA), Is.True);
        Assert.That(sources.Contains(projectB), Is.True);
        Assert.That(() => sources.EnsureNotProtected(projectA),
            Throws.InstanceOf<InvalidOperationException>());
        Assert.That(() => sources.EnsureNotProtected(projectB),
            Throws.InstanceOf<InvalidOperationException>());
        var files = result.Projects.SelectMany(project => project.Tasks)
            .Select(task => Path.Combine(result.Directory, task.FileName)).ToArray();
        Assert.That(files.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(4));
        Assert.That(files.All(File.Exists), Is.True);
        foreach (var project in result.Projects)
        {
            var sizes = project.Tasks.Select(task => TestImages.DecodePng(
                File.ReadAllBytes(Path.Combine(result.Directory, task.FileName)))).ToArray();
            Assert.That(sizes.Select(size => size.Width), Is.All.EqualTo(320));
            var source = NativePackageStore.Read(project.ProjectPath);
            Assert.That(sizes.Select(size => size.Height).Distinct().Count(), Is.EqualTo(1));
            Assert.That(sizes[0].Height, Is.EqualTo((int)Math.Round(
                (double)source.CanvasHeight * 320 / source.CanvasWidth)));
        }
        Assert.That(File.Exists(result.ManifestPath), Is.True);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        Assert.That(manifest.RootElement.GetProperty("formatVersion").GetInt32(), Is.EqualTo(2));
        Assert.That(manifest.RootElement.GetProperty("projects").GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task SameProjectBaseNameGetsDeterministicUniqueOutputNames()
    {
        var first = Project("同名", 640, 360, 0xFF111111, "same-a");
        var secondFolder = Directory.CreateDirectory(Path.Combine(_folder, "second")).FullName;
        var second = Project("同名", 640, 360, 0xFF222222, "same-b", secondFolder);
        var result = await ProjectRenderBatch.ExecuteAsync(
            new ProjectRenderPlan(Output("names"),
                [first, second], [new PngExportTask(320, false)]),
            TestImages.ValidateImages,
            (document, transparent, width) => TestImages.RenderPng(document, transparent, width),
            (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
            new ProtectedSourcePaths(), () => null, _ => false).ConfigureAwait(false);
        var names = result.Projects.Select(project => project.Tasks.Single().FileName).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "同名-320.png", "同名-320-2.png" }));
        Assert.That(names.All(name => File.Exists(Path.Combine(result.Directory, name))), Is.True);
    }

    [Test]
    public async Task CorruptProjectFailsAloneAndDoesNotStopTheNextProject()
    {
        var good = Project("good", 640, 360, 0xFF112233, "good");
        var bad = Path.Combine(_folder, "bad.ppgproj");
        File.WriteAllBytes(bad, [1, 2, 3]);
        var result = await ProjectRenderBatch.ExecuteAsync(
            new ProjectRenderPlan(Output("corrupt"), [bad, good],
                [new PngExportTask(320, false)]),
            TestImages.ValidateImages,
            (document, transparent, width) => TestImages.RenderPng(document, transparent, width),
            (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
            new ProtectedSourcePaths(), () => null, _ => false).ConfigureAwait(false);
        Assert.That(result.Projects[0].Status, Is.EqualTo(ProjectRenderStatus.Failed));
        Assert.That(result.Projects[0].Error, Is.Not.Null);
        Assert.That(result.Projects[0].Tasks.Single().Status, Is.EqualTo(PngExportTaskStatus.Failed));
        Assert.That(result.Projects[1].Status, Is.EqualTo(ProjectRenderStatus.Completed));
        Assert.That(File.Exists(Path.Combine(result.Directory,
            result.Projects[1].Tasks.Single().FileName)), Is.True);
    }

    [Test]
    public async Task CancellationBetweenTasksKeepsCompletedFilesAndMarksRemainingProjects()
    {
        var first = Project("cancel-first", 640, 360, 0xFF101010, "cancel-a");
        var second = Project("cancel-second", 640, 360, 0xFF202020, "cancel-b");
        using var cancel = new CancellationTokenSource();
        var renders = 0;
        var result = await ProjectRenderBatch.ExecuteAsync(
            new ProjectRenderPlan(Output("cancel"), [first, second],
                [new PngExportTask(320, false), new PngExportTask(400, false)]),
            TestImages.ValidateImages,
            (document, transparent, width) =>
            {
                renders++;
                if (renders == 2) cancel.Cancel();
                return TestImages.RenderPng(document, transparent, width);
            },
            (path, bytes) => AtomicFile.WriteNew(path, stream => stream.Write(bytes)),
            new ProtectedSourcePaths(), () => null, _ => false, cancellationToken: cancel.Token)
            .ConfigureAwait(false);
        Assert.That(result.Cancelled, Is.True);
        Assert.That(result.Projects[0].Status, Is.EqualTo(ProjectRenderStatus.Cancelled));
        Assert.That(result.Projects[0].Tasks[0].Status, Is.EqualTo(PngExportTaskStatus.Completed));
        Assert.That(result.Projects[0].Tasks[1].Status, Is.EqualTo(PngExportTaskStatus.Cancelled));
        Assert.That(result.Projects[1].Status, Is.EqualTo(ProjectRenderStatus.Cancelled));
        Assert.That(File.Exists(Path.Combine(result.Directory, result.Projects[0].Tasks[0].FileName)), Is.True);
        Assert.That(File.Exists(Path.Combine(result.Directory, result.Projects[0].Tasks[1].FileName)), Is.False);
    }

    [Test]
    public async Task OneWriteFailureDoesNotStopAnotherProject()
    {
        var first = Project("write-fail", 640, 360, 0xFF303030, "fail");
        var second = Project("write-good", 640, 360, 0xFF404040, "good");
        var result = await ProjectRenderBatch.ExecuteAsync(
            new ProjectRenderPlan(Output("write"), [first, second],
                [new PngExportTask(320, false)]),
            TestImages.ValidateImages,
            (document, transparent, width) => TestImages.RenderPng(document, transparent, width),
            (path, bytes) =>
            {
                if (path.Contains("write-fail", StringComparison.Ordinal)) throw new IOException("simulated");
                AtomicFile.WriteNew(path, stream => stream.Write(bytes));
            },
            new ProtectedSourcePaths(), () => null, _ => false).ConfigureAwait(false);
        Assert.That(result.Projects[0].Status, Is.EqualTo(ProjectRenderStatus.Failed));
        Assert.That(result.Projects[0].Tasks.Single().Status, Is.EqualTo(PngExportTaskStatus.Failed));
        Assert.That(result.Projects[1].Status, Is.EqualTo(ProjectRenderStatus.Completed));
        Assert.That(File.Exists(Path.Combine(result.Directory,
            result.Projects[1].Tasks.Single().FileName)), Is.True);
    }

    [Test]
    public void SelectionContractKeepsOriginalCanvasBackgroundRotationAndGroupMembers()
    {
        var group = Guid.NewGuid();
        var art = new Layer
        {
            Kind = LayerKind.Image, Name = "曲绘", GroupId = group,
            AssetId = Guid.NewGuid(), Bounds = new RectD(100, 100, 300, 200),
            RotationDegrees = 17
        };
        var hidden = new Layer
        {
            Kind = LayerKind.Text, Name = "隐藏文字", GroupId = group, Visible = false,
            Text = "隐藏成员", Bounds = new RectD(180, 140, 100, 40)
        };
        var outside = new Layer { Kind = LayerKind.Shape, Name = "其他层",
            Bounds = new RectD(600, 400, 100, 100) };
        var source = new EditorDocument
        {
            CanvasWidth = 1000, CanvasHeight = 600, BackgroundArgb = 0xFF123456,
            Layers = [art, hidden, outside]
        };
        source.AddAsset(art.AssetId!.Value, TestImages.EncodePng(20, 10,
            TestImages.SyntheticBgra(20, 10)));
        var selected = SelectionProject.Create(source, [art.Id], includeGroups: true);
        Assert.That(selected.CanvasWidth, Is.EqualTo(1000));
        Assert.That(selected.CanvasHeight, Is.EqualTo(600));
        Assert.That(selected.BackgroundArgb, Is.EqualTo(0xFF123456));
        Assert.That(selected.Layers.Select(layer => layer.Name),
            Is.EquivalentTo(new[] { "曲绘", "隐藏文字" }));
        Assert.That(selected.Find(hidden.Id).Visible, Is.False);
        Assert.That(selected.Find(art.Id).RotationDegrees, Is.EqualTo(17));
        Assert.That(selected.Find(art.Id).Bounds, Is.EqualTo(art.Bounds));
        Assert.That(selected.Layers.Any(layer => layer.Name == "其他层"), Is.False);
    }

    [Test]
    public void CliBatchUsesTheSamePlanAndManifest()
    {
        AvaloniaTestHost.EnsureStarted();
        var first = Project("cli-a", 640, 360, 0xFF010203, "cli-a");
        var second = Project("cli-b", 480, 480, 0xFF040506, "cli-b");
        var parent = Directory.CreateDirectory(Path.Combine(_folder, "cli-output")).FullName;
        var plan = Path.Combine(_folder, "plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            outputParent = parent,
            projects = new[] { Path.GetFileName(first), Path.GetFileName(second) },
            tasks = new[] { new { width = 320, transparent = false } }
        }));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = ProjectRenderBatchCommand.Run(plan, stdout, stderr, AvaloniaTestHost.EnsureStarted);
        Assert.That(exit, Is.EqualTo(0), stderr.ToString());
        Assert.That(stdout.ToString(), Does.Contain("BATCH RENDER PASS"));
        var manifestPath = Directory.GetFiles(parent, "project-render-manifest.json",
            SearchOption.AllDirectories).Single();
        Assert.That(File.Exists(manifestPath), Is.True);
        var outputFile = Directory.GetFiles(Path.GetDirectoryName(manifestPath)!, "cli-a-320.png").Single();
        var expected = TestImages.RenderPng(NativePackageStore.Read(first), false, 320);
        Assert.That(File.ReadAllBytes(outputFile), Is.EqualTo(expected));
    }

    [Test]
    public void CliBatchDistinguishesInvalidPlanAndProjectFailure()
    {
        AvaloniaTestHost.EnsureStarted();
        var invalid = Path.Combine(_folder, "invalid-plan.json");
        File.WriteAllText(invalid, "{}");
        Assert.That(ProjectRenderBatchCommand.Run(invalid, new StringWriter(), new StringWriter(),
            AvaloniaTestHost.EnsureStarted), Is.EqualTo(2));

        var bad = Path.Combine(_folder, "bad-cli.ppgproj");
        File.WriteAllBytes(bad, [1, 2, 3]);
        var parent = Directory.CreateDirectory(Path.Combine(_folder, "bad-cli-out")).FullName;
        var plan = Path.Combine(_folder, "bad-plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            outputParent = parent,
            projects = new[] { Path.GetFileName(bad) },
            tasks = new[] { new { width = 320, transparent = false } }
        }));
        var output = new StringWriter();
        Assert.That(ProjectRenderBatchCommand.Run(plan, output, new StringWriter(),
            AvaloniaTestHost.EnsureStarted), Is.EqualTo(1));
        Assert.That(output.ToString(), Does.Contain("BATCH RENDER FAILED"));
    }

    [Test]
    public void CliBatchV2BlocksLowResolutionProjectsAndRejectsPolicyInV1()
    {
        AvaloniaTestHost.EnsureStarted();
        var source = Path.Combine(_folder, "quality-cli.ppgproj");
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        var asset = Guid.NewGuid();
        document.AddAsset(asset, TestImages.EncodePng(400, 240,
            TestImages.SyntheticBgra(400, 240)));
        document.Layers.Add(new Layer
        {
            Kind = LayerKind.Image, Name = "upscaled", AssetId = asset,
            Bounds = new RectD(100, 100, 800, 480)
        });
        NativePackageStore.Save(source, document, "r3m-test");
        var parent = Directory.CreateDirectory(Path.Combine(_folder, "quality-cli-out")).FullName;
        var planV2 = Path.Combine(_folder, "quality-plan-v2.json");
        File.WriteAllText(planV2, JsonSerializer.Serialize(new
        {
            formatVersion = 2,
            qualityPolicy = "BlockLowResolution",
            outputParent = parent,
            projects = new[] { Path.GetFileName(source) },
            tasks = new[] { new { width = 1000, transparent = false } }
        }));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.That(ProjectRenderBatchCommand.Run(planV2, stdout, stderr,
            AvaloniaTestHost.EnsureStarted), Is.EqualTo(1));
        Assert.That(stdout.ToString(), Does.Contain("BATCH RENDER FAILED"));
        var manifest = Directory.GetFiles(parent, "project-render-manifest.json",
            SearchOption.AllDirectories).Single();
        using (var json = JsonDocument.Parse(File.ReadAllBytes(manifest)))
            Assert.That(json.RootElement.GetProperty("qualityPolicy").GetString(),
                Is.EqualTo("BlockLowResolution"));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(manifest)!, "*.png"), Is.Empty);

        var planV1 = Path.Combine(_folder, "quality-plan-v1.json");
        File.WriteAllText(planV1, JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            qualityPolicy = "Notify",
            outputParent = parent,
            projects = new[] { Path.GetFileName(source) },
            tasks = new[] { new { width = 1000, transparent = false } }
        }));
        Assert.That(ProjectRenderBatchCommand.Run(planV1, new StringWriter(),
            new StringWriter(), AvaloniaTestHost.EnsureStarted), Is.EqualTo(2));
    }

    private string Project(string name, int width, int height, uint background, string text,
        string? folder = null, string font = "Microsoft YaHei UI")
    {
        folder ??= _folder;
        var path = Path.Combine(folder, name + ".ppgproj");
        NativePackageStore.Save(path, new EditorDocument
        {
            CanvasWidth = width, CanvasHeight = height, BackgroundArgb = background,
            Layers = [new Layer { Kind = LayerKind.Text, Name = text, Text = text, FontFamily = font,
                Bounds = new RectD(40, 40, 200, 60) }]
        }, "r2-batch");
        return path;
    }

    private string Output(string name) =>
        Directory.CreateDirectory(Path.Combine(_folder, name)).FullName;
}
