using System.Text.Json.Nodes;
using Editor.Application;
using Editor.Core;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3BackgroundContributionTests
{
    [Test]
    public void ExplicitSelectionAndRangeTransitionChangeCompositionDeterministically()
    {
        AvaloniaTestHost.EnsureStarted();
        var document = P4AutomaticBackgroundTests.MakeDocument(3, 800, 600);
        var ids = document.Layers.Select(layer => layer.Id).ToArray();
        document.BackgroundBlend = new(true, 4, 0.2, IncludeFlatImages: true);
        var automatic = TestImages.RenderPng(document);
        Assert.That(BackgroundComposer.ContributingLayers(document), Has.Length.EqualTo(3));

        document.BackgroundBlend = document.BackgroundBlend with
        {
            ContributionMode = BackgroundContributionMode.Selected,
            SelectedArtworkLayerIds = [ids[0], ids[2]],
            Range = 0.8,
            Transition = 0.75
        };
        Assert.That(BackgroundComposer.ContributingLayers(document).Select(layer => layer.Id),
            Is.EqualTo(new[] { ids[0], ids[2] }));
        var selected = TestImages.RenderPng(document);
        Assert.That(selected, Is.Not.EqualTo(automatic));

        document.BackgroundBlend = document.BackgroundBlend with
            { Range = 0.35, Transition = 0.1 };
        var narrow = TestImages.RenderPng(document);
        Assert.That(narrow, Is.Not.EqualTo(selected));
        Assert.That(TestImages.RenderPng(document), Is.EqualTo(narrow));

        document.Layers.Single(layer => layer.Id == ids[2]).Visible = false;
        Assert.That(BackgroundComposer.ContributingLayers(document).Select(layer => layer.Id),
            Is.EqualTo(new[] { ids[0] }));
        document.Layers.Single(layer => layer.Id == ids[2]).Visible = true;

        var path = Path.Combine(Path.GetTempPath(),
            "r3c-background-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, document, "r3c-test");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.StateEquals(document), Is.True);
            Assert.That(TestImages.RenderPng(reopened), Is.EqualTo(narrow));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void BackgroundCacheTracksSelectionAndTransitionChanges()
    {
        var document = P4AutomaticBackgroundTests.MakeDocument(3);
        document.BackgroundBlend = new(true, 3, 0.2, IncludeFlatImages: true);
        var cache = new BackgroundRenderCache();
        var automatic = cache.Get(document, TestImages.Decoder)!;
        document.BackgroundBlend = document.BackgroundBlend with
        {
            ContributionMode = BackgroundContributionMode.Selected,
            SelectedArtworkLayerIds = [document.Layers[0].Id],
            Transition = 0.9
        };
        var selected = cache.Get(document, TestImages.Decoder)!;
        Assert.That(selected, Is.Not.SameAs(automatic));
        Assert.That(selected.ToArray(), Is.EqualTo(
            BackgroundComposer.Generate(document, TestImages.Decoder)));
        Assert.That(cache.Get(document, TestImages.Decoder), Is.SameAs(selected));
    }

    [Test]
    public void V8RoundTripPersistsSelectionAndRangeTransition()
    {
        var document = P4AutomaticBackgroundTests.MakeDocument(2, 640, 360);
        var selected = new[] { document.Layers[0].Id };
        document.BackgroundBlend = new(true, 2, 0.3, IncludeFlatImages: true,
            BackgroundContributionMode.Selected, selected, 0.75, 0.8);
        var native = Path.Combine(Path.GetTempPath(),
            "r3c-v8-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        var temporary = Path.ChangeExtension(native, ".json");
        try
        {
            NativePackageStore.Save(native, document, "r3c-test");
            using (var archive = System.IO.Compression.ZipFile.OpenRead(native))
            using (var reader = new StreamReader(archive.GetEntry("project.json")!.Open()))
            {
                var manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
                Assert.That(manifest["schemaVersion"]!.GetValue<int>(), Is.EqualTo(8));
                var blend = manifest["backgroundBlend"]!.AsObject();
                Assert.That(blend["contributionMode"]!.GetValue<string>(), Is.EqualTo("Selected"));
                Assert.That(blend["selectedArtworkLayerIds"]!.AsArray(), Has.Count.EqualTo(1));
                Assert.That(blend["range"]!.GetValue<double>(), Is.EqualTo(0.75));
                Assert.That(blend["transition"]!.GetValue<double>(), Is.EqualTo(0.8));
            }
            Assert.That(NativePackageStore.Read(native).StateEquals(document), Is.True);
            NativeDocumentStore.Save(temporary, document);
            Assert.That(NativeDocumentStore.Read(temporary).StateEquals(document), Is.True);
        }
        finally
        {
            if (File.Exists(native)) File.Delete(native);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [Test]
    public void OlderSchemaDefaultsToAutomaticAndRejectsV8Fields()
    {
        var document = P4AutomaticBackgroundTests.MakeDocument(1, 640, 360);
        var native = Path.Combine(Path.GetTempPath(),
            "r3c-old-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        var temporary = Path.ChangeExtension(native, ".json");
        try
        {
            NativePackageStore.Save(native, document, "r3c-old");
            var manifest = ReadManifest(native);
            manifest["schemaVersion"] = 7;
            LegacyFormatTestHelpers.RemoveV8BackgroundFields(manifest, temporary: false);
            WriteManifest(native, manifest);
            var migrated = NativePackageStore.Read(native);
            Assert.That(migrated.BackgroundBlend.ContributionMode,
                Is.EqualTo(BackgroundContributionMode.Automatic));
            Assert.That(migrated.BackgroundBlend.SelectedArtworkLayerIds, Is.Null);
            Assert.That(migrated.BackgroundBlend.Range, Is.EqualTo(1));
            Assert.That(migrated.BackgroundBlend.Transition, Is.EqualTo(0.5));

            NativePackageStore.Save(native, document, "r3c-old");
            manifest = ReadManifest(native);
            manifest["schemaVersion"] = 7;
            WriteManifest(native, manifest);
            Assert.That(() => NativePackageStore.Read(native),
                Throws.InstanceOf<InvalidDataException>());

            NativeDocumentStore.Save(temporary, document);
            var json = JsonNode.Parse(File.ReadAllText(temporary))!.AsObject();
            json["FormatVersion"] = 7;
            LegacyFormatTestHelpers.RemoveV8BackgroundFields(json, temporary: true);
            File.WriteAllText(temporary, json.ToJsonString());
            var migratedJson = NativeDocumentStore.Read(temporary);
            Assert.That(migratedJson.BackgroundBlend.ContributionMode,
                Is.EqualTo(BackgroundContributionMode.Automatic));
            Assert.That(migratedJson.BackgroundBlend.Range, Is.EqualTo(1));

            NativeDocumentStore.Save(temporary, document);
            json = JsonNode.Parse(File.ReadAllText(temporary))!.AsObject();
            json["FormatVersion"] = 7;
            File.WriteAllText(temporary, json.ToJsonString());
            Assert.That(() => NativeDocumentStore.Read(temporary),
                Throws.InstanceOf<InvalidDataException>());
        }
        finally
        {
            if (File.Exists(native)) File.Delete(native);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [TestCase(0.1, 0.5)]
    [TestCase(2.1, 0.5)]
    [TestCase(1, -0.1)]
    [TestCase(1, 1.1)]
    public void InvalidRangesAreRejected(double range, double transition)
    {
        var document = P4AutomaticBackgroundTests.MakeDocument(1);
        document.BackgroundBlend = document.BackgroundBlend with
            { Range = range, Transition = transition };
        Assert.That(() => document.Validate(), Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void DuplicateSelectedArtworkAndMissingV8FieldsAreRejected()
    {
        var document = P4AutomaticBackgroundTests.MakeDocument(1);
        var id = document.Layers[0].Id;
        document.BackgroundBlend = document.BackgroundBlend with
        {
            ContributionMode = BackgroundContributionMode.Selected,
            SelectedArtworkLayerIds = [id, id]
        };
        Assert.That(() => document.Validate(), Throws.InstanceOf<InvalidDataException>());

        document.BackgroundBlend = document.BackgroundBlend with
            { SelectedArtworkLayerIds = [id] };
        var native = Path.Combine(Path.GetTempPath(),
            "r3c-missing-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        var temporary = Path.ChangeExtension(native, ".json");
        try
        {
            NativePackageStore.Save(native, document, "r3c-test");
            var manifest = ReadManifest(native);
            manifest["schemaVersion"] = 8;
            manifest["backgroundBlend"]!.AsObject().Remove("range");
            WriteManifest(native, manifest);
            Assert.That(() => NativePackageStore.Read(native),
                Throws.InstanceOf<InvalidDataException>());

            NativeDocumentStore.Save(temporary, document);
            var json = JsonNode.Parse(File.ReadAllText(temporary))!.AsObject();
            json["FormatVersion"] = 8;
            json["Document"]!["BackgroundBlend"]!.AsObject().Remove("Range");
            File.WriteAllText(temporary, json.ToJsonString());
            Assert.That(() => NativeDocumentStore.Read(temporary),
                Throws.InstanceOf<InvalidDataException>());
        }
        finally
        {
            if (File.Exists(native)) File.Delete(native);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static JsonObject ReadManifest(string path)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry("project.json")!.Open());
        return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }

    private static void WriteManifest(string path, JsonObject manifest)
    {
        using var archive = System.IO.Compression.ZipFile.Open(path,
            System.IO.Compression.ZipArchiveMode.Update);
        archive.GetEntry("project.json")!.Delete();
        using var writer = new StreamWriter(archive.CreateEntry("project.json").Open());
        writer.Write(manifest.ToJsonString());
    }
}
