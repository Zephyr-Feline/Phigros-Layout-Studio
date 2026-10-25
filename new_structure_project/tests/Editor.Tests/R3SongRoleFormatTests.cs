using System.IO.Compression;
using System.Text.Json.Nodes;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3SongRoleFormatTests
{
    [Test]
    public void V8PersistsRolesAndRejectsThemInV6Packages()
    {
        var document = RoleDocument();
        var native = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3-role-format-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        var temporary = Path.ChangeExtension(native, ".json");
        try
        {
            NativePackageStore.Save(native, document, "r3-role");
            NativeDocumentStore.Save(temporary, document);
            var manifest = Manifest(native);
            Assert.That(manifest["schemaVersion"]!.GetValue<int>(), Is.EqualTo(8));
            Assert.That(manifest["layers"]!.AsArray().Any(layer =>
                layer!["songRole"]?.GetValue<string>() == "Title"), Is.True);
            Assert.That(NativePackageStore.Read(native).StateEquals(document), Is.True);
            Assert.That(JsonNode.Parse(File.ReadAllText(temporary))!["FormatVersion"]!.GetValue<int>(),
                Is.EqualTo(8));
            Assert.That(NativeDocumentStore.Read(temporary).StateEquals(document), Is.True);

            manifest["schemaVersion"] = 6;
            LegacyFormatTestHelpers.RemoveV8BackgroundFields(manifest, temporary: false);
            WriteManifest(native, manifest);
            Assert.That(() => NativePackageStore.Read(native), Throws.InstanceOf<InvalidDataException>());

            var temporaryJson = JsonNode.Parse(File.ReadAllText(temporary))!.AsObject();
            temporaryJson["FormatVersion"] = 6;
            LegacyFormatTestHelpers.RemoveV8BackgroundFields(temporaryJson, temporary: true);
            File.WriteAllText(temporary, temporaryJson.ToJsonString());
            Assert.That(() => NativeDocumentStore.Read(temporary), Throws.InstanceOf<InvalidDataException>());

            NativePackageStore.Save(native, document, "r3-role");
            manifest = Manifest(native);
            foreach (var layer in manifest["layers"]!.AsArray()) layer!.AsObject().Remove("songRole");
            manifest["schemaVersion"] = 6;
            LegacyFormatTestHelpers.RemoveV8BackgroundFields(manifest, temporary: false);
            WriteManifest(native, manifest);
            Assert.That(NativePackageStore.Read(native).Layers.All(layer => layer.SongRole == SongRole.None),
                Is.True);

            NativeDocumentStore.Save(temporary, document);
            temporaryJson = JsonNode.Parse(File.ReadAllText(temporary))!.AsObject();
            temporaryJson["FormatVersion"] = 6;
            LegacyFormatTestHelpers.RemoveV8BackgroundFields(temporaryJson, temporary: true);
            foreach (var layer in temporaryJson["Document"]!["Layers"]!.AsArray())
                layer!.AsObject().Remove("SongRole");
            File.WriteAllText(temporary, temporaryJson.ToJsonString());
            Assert.That(NativeDocumentStore.Read(temporary).Layers.All(layer => layer.SongRole == SongRole.None),
                Is.True);
        }
        finally
        {
            if (File.Exists(native)) File.Delete(native);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static EditorDocument RoleDocument()
    {
        var group = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var document = new EditorDocument { CanvasWidth = 640, CanvasHeight = 360 };
        document.AddAsset(asset, TestImages.EncodePng(8, 4, TestImages.SyntheticBgra(8, 4)));
        var art = new Layer { Kind = LayerKind.Image, AssetId = asset, GroupId = group,
            SongRole = SongRole.Artwork, Bounds = new RectD(20, 20, 160, 100) };
        var title = new Layer { Kind = LayerKind.Text, GroupId = group, SongRole = SongRole.Title,
            Text = "Title", Bounds = new RectD(20, 130, 160, 30) };
        var artist = new Layer { Kind = LayerKind.Text, GroupId = group, SongRole = SongRole.Artist,
            Text = "Artist", Bounds = new RectD(20, 160, 160, 30) };
        var charter = new Layer { Kind = LayerKind.Text, GroupId = group, SongRole = SongRole.Charter,
            Text = "Charter", Bounds = new RectD(20, 190, 160, 30) };
        var badge = EditorDocument.NewBadge(new PointD(190, 20));
        badge.GroupId = group; badge.SongRole = SongRole.Badge;
        document.Layers.AddRange([art, title, artist, charter, badge]);
        document.Validate();
        return document;
    }

    private static JsonObject Manifest(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry("project.json")!.Open());
        return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
    }

    private static void WriteManifest(string path, JsonNode manifest)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry("project.json")!.Delete();
        using var writer = new StreamWriter(archive.CreateEntry("project.json").Open());
        writer.Write(manifest.ToJsonString());
    }
}
