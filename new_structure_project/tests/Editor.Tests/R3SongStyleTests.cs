using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3SongStyleTests
{
    [Test]
    public void NewSongCardsAndManualCardsPersistStableRolesAfterRenaming()
    {
        var images = Enumerable.Range(0, 3).Select(index => new SongArtwork(
            $"Song {index}",
            new AssetBlob(TestImages.EncodePng(20, 10, TestImages.SyntheticBgra(20, 10))))).ToArray();
        var batch = SongBatch.Create(1600, 900, images);
        var groups = batch.Layers.Select(layer => layer.GroupId).OfType<Guid>().Distinct().ToArray();
        Assert.That(groups, Has.Length.EqualTo(3));
        foreach (var group in groups)
        {
            var layers = batch.Layers.Where(layer => layer.GroupId == group).ToArray();
            Assert.That(layers.Single(layer => layer.SongRole == SongRole.Artwork).Kind,
                Is.EqualTo(LayerKind.Image));
            Assert.That(layers.Single(layer => layer.SongRole == SongRole.Title).Kind,
                Is.EqualTo(LayerKind.Text));
            Assert.That(layers.Single(layer => layer.SongRole == SongRole.Artist).Kind,
                Is.EqualTo(LayerKind.Text));
            Assert.That(layers.Single(layer => layer.SongRole == SongRole.Charter).Kind,
                Is.EqualTo(LayerKind.Text));
            Assert.That(layers.Single(layer => layer.SongRole == SongRole.Badge).Kind,
                Is.EqualTo(LayerKind.Badge));
            Assert.That(layers.Single(layer => layer.SongRole == SongRole.Plate).Kind,
                Is.EqualTo(LayerKind.Shape));
        }

        var single = new EditorDocument { CanvasWidth = 800, CanvasHeight = 450 };
        var asset = Guid.NewGuid();
        single.AddAsset(asset, TestImages.EncodePng(20, 10, TestImages.SyntheticBgra(20, 10)));
        var image = new Layer
        {
            Kind = LayerKind.Image, Name = "manual", AssetId = asset,
            Bounds = new RectD(100, 100, 300, 180)
        };
        single.Layers.Add(image);
        single.Validate();
        var edit = new EditSession(single);
        edit.CreateSongCard(image.Id);
        var manualGroup = edit.Document.Find(image.Id).GroupId!.Value;
        var manualTitle = edit.Document.Layers.Single(layer =>
            layer.GroupId == manualGroup && layer.SongRole == SongRole.Title);
        manualTitle.Name = "用户改过的标题图层";
        var suggestion = SongInfoWorkbench.Suggest(edit.Document, [manualGroup]).Single();
        Assert.That(suggestion.TitleId, Is.EqualTo(manualTitle.Id));

        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3-song-roles-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "r3-song-style");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.Find(manualTitle.Id).SongRole, Is.EqualTo(SongRole.Title));
            Assert.That(SongInfoWorkbench.Suggest(reopened, [manualGroup]).Single().TitleId,
                Is.EqualTo(manualTitle.Id));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void CaptureIsContentFreeAndApplyPreservesTargetContentAndIdentity()
    {
        var fixture = CreateFixture();
        var style = SongStyles.Capture(fixture.Document, fixture.GroupA, "  霓虹样式  ");
        Assert.That(style.Name, Is.EqualTo("霓虹样式"));
        Assert.That(style.Layers, Has.Count.EqualTo(6));
        Assert.That(style.Layers.Single(layer => layer.Role == SongRole.Title).FontFamily,
            Is.EqualTo("Arial"));
        Assert.That(style.Layers.Single(layer => layer.Role == SongRole.Artist).Visible,
            Is.False);
        Assert.That(style.Layers.Single(layer => layer.Role == SongRole.Artist).Locked,
            Is.True);
        var capturedCopy = style;

        var edit = new EditSession(fixture.Document);
        var target = fixture.GroupB;
        var beforeTarget = fixture.Document.Layers.Where(layer => layer.GroupId == target)
            .ToDictionary(layer => layer.Id, layer => layer.Copy());
        var titleBefore = beforeTarget.Values.Single(layer => layer.SongRole == SongRole.Title);
        var artworkBefore = beforeTarget.Values.Single(layer => layer.SongRole == SongRole.Artwork);
        var badgeBefore = beforeTarget.Values.Single(layer => layer.SongRole == SongRole.Badge);
        edit.SetSelection(beforeTarget.Keys);

        edit.ApplySongStyle(style, edit.SelectedLayerIds);

        Assert.That(edit.UndoCount, Is.EqualTo(1));
        var after = edit.Document.Layers.Where(layer => layer.GroupId == target).ToArray();
        var titleAfter = after.Single(layer => layer.SongRole == SongRole.Title);
        var artworkAfter = after.Single(layer => layer.SongRole == SongRole.Artwork);
        var badgeAfter = after.Single(layer => layer.SongRole == SongRole.Badge);
        Assert.That(titleAfter.Id, Is.EqualTo(titleBefore.Id));
        Assert.That(titleAfter.Name, Is.EqualTo(titleBefore.Name));
        Assert.That(titleAfter.Text, Is.EqualTo(titleBefore.Text));
        Assert.That(titleAfter.FontFamily, Is.EqualTo("Arial"));
        Assert.That(titleAfter.FontSize, Is.EqualTo(38.4).Within(1e-9));
        Assert.That(titleAfter.Bold, Is.True);
        Assert.That(titleAfter.Opacity, Is.EqualTo(0.75));
        Assert.That(artworkAfter.Id, Is.EqualTo(artworkBefore.Id));
        Assert.That(artworkAfter.AssetId, Is.EqualTo(artworkBefore.AssetId));
        Assert.That(badgeAfter.Id, Is.EqualTo(badgeBefore.Id));
        Assert.That(badgeAfter.Difficulty, Is.EqualTo(badgeBefore.Difficulty));
        Assert.That(badgeAfter.Level, Is.EqualTo(badgeBefore.Level));
        Assert.That(after.Single(layer => layer.SongRole == SongRole.Artist).Visible, Is.False);
        Assert.That(after.Single(layer => layer.SongRole == SongRole.Artist).Locked, Is.True);
        Assert.That(style.ContentEquals(capturedCopy), Is.True);

        edit.Undo();
        Assert.That(edit.Document.StateEquals(fixture.Document), Is.True);
        edit.Redo();
        Assert.That(edit.Document.Find(titleAfter.Id).FontSize, Is.EqualTo(38.4).Within(1e-9));
    }

    [Test]
    public void ApplyingStyleAddsOptionalPlateAndRollsBackAllGroupsOnFailure()
    {
        var fixture = CreateFixture(targetHasPlate: false);
        var style = SongStyles.Capture(fixture.Document, fixture.GroupA, "带底板");
        var edit = new EditSession(fixture.Document);
        edit.SetSelection(fixture.Document.Layers
            .Where(layer => layer.GroupId == fixture.GroupB).Select(layer => layer.Id));
        edit.ApplySongStyle(style, edit.SelectedLayerIds);
        var group = edit.Document.Layers.Where(layer => layer.GroupId == fixture.GroupB).ToArray();
        var plate = group.Single(layer => layer.SongRole == SongRole.Plate);
        Assert.That(plate.Id, Is.Not.EqualTo(fixture.PlateA));
        Assert.That(plate.Kind, Is.EqualTo(LayerKind.Shape));
        Assert.That(edit.UndoCount, Is.EqualTo(1));

        var before = edit.Document.Copy();
        var locked = edit.Document.Layers.First(layer => layer.GroupId == fixture.GroupB &&
            layer.SongRole == SongRole.Badge);
        locked.Locked = true;
        var lockedSnapshot = edit.Document.Copy();
        edit.SetSelection(edit.Document.Layers
            .Where(layer => layer.GroupId == fixture.GroupA).Select(layer => layer.Id)
            .Concat(edit.Document.Layers.Where(layer => layer.GroupId == fixture.GroupB)
                .Select(layer => layer.Id)));
        Assert.That(() => edit.ApplySongStyle(style, edit.SelectedLayerIds),
            Throws.InstanceOf<InvalidOperationException>());
        Assert.That(edit.Document.StateEquals(lockedSnapshot), Is.True);
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(before.Layers.Count, Is.EqualTo(lockedSnapshot.Layers.Count));
    }

    [Test]
    public void MissingRolesAreRejectedWithoutChangingTheDocument()
    {
        var fixture = CreateFixture();
        var unroled = new EditorDocument { CanvasWidth = 800, CanvasHeight = 450 };
        var asset = Guid.NewGuid();
        unroled.AddAsset(asset, TestImages.EncodePng(20, 10, TestImages.SyntheticBgra(20, 10)));
        var group = Guid.NewGuid();
        unroled.Layers.Add(new Layer
        {
            Kind = LayerKind.Image, GroupId = group, AssetId = asset,
            Name = "legacy · artwork", Bounds = new RectD(20, 20, 200, 100)
        });
        unroled.Validate();
        Assert.That(() => SongStyles.Capture(unroled, group, "旧工程"),
            Throws.InstanceOf<InvalidOperationException>());
        var style = SongStyles.Capture(fixture.Document, fixture.GroupA, "有效");
        var edit = new EditSession(unroled);
        var before = edit.Document.Copy();
        Assert.That(() => edit.ApplySongStyle(style, unroled.Layers.Select(layer => layer.Id)),
            Throws.InstanceOf<InvalidOperationException>());
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void InstantiateCreatesNewIdsAndAssetReferenceWithoutChangingSourceOrStyle()
    {
        var fixture = CreateFixture(targetHasPlate: false);
        var style = SongStyles.Capture(fixture.Document, fixture.GroupA, "实例样式");
        var edit = new EditSession(fixture.Document);
        var sourceArtwork = fixture.Document.Layers.Single(layer =>
            layer.GroupId == fixture.GroupB && layer.SongRole == SongRole.Artwork);
        var sourceTitle = fixture.Document.Layers.Single(layer =>
            layer.GroupId == fixture.GroupB && layer.SongRole == SongRole.Title);
        var before = edit.Document.Copy();
        var ids = edit.InstantiateSongStyle(style, sourceTitle.Id);
        Assert.That(ids, Has.Count.EqualTo(6));
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        var created = ids.Select(id => edit.Document.Find(id)).ToArray();
        var newGroup = created[0].GroupId!.Value;
        Assert.That(created.Select(layer => layer.GroupId).Distinct(), Is.EqualTo(new[] { newGroup }));
        Assert.That(created.Select(layer => layer.Id).Distinct().Count(), Is.EqualTo(6));
        Assert.That(created.Any(layer => layer.SongRole == SongRole.Plate), Is.True);
        var newArtwork = created.Single(layer => layer.SongRole == SongRole.Artwork);
        Assert.That(newArtwork.Id, Is.Not.EqualTo(sourceArtwork.Id));
        Assert.That(newArtwork.AssetId, Is.Not.EqualTo(sourceArtwork.AssetId));
        Assert.That(edit.Document.Assets[newArtwork.AssetId!.Value].ContentEquals(
            edit.Document.Assets[sourceArtwork.AssetId!.Value]), Is.True);
        Assert.That(created.Single(layer => layer.SongRole == SongRole.Title).Text,
            Is.EqualTo(sourceTitle.Text));
        Assert.That(created.Single(layer => layer.SongRole == SongRole.Badge).Difficulty,
            Is.EqualTo("HD"));
        Assert.That(created.Single(layer => layer.SongRole == SongRole.Title).FontSize,
            Is.EqualTo(38.4).Within(1e-9));
        Assert.That(edit.Document.Find(sourceArtwork.Id).AssetId, Is.EqualTo(sourceArtwork.AssetId));
        Assert.That(edit.SelectedLayerIds, Is.EquivalentTo(ids));
        Assert.That(style.ContentEquals(SongStyles.Capture(before, fixture.GroupA, "实例样式")),
            Is.True, "the content-free style remains equal to its source capture");

        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
        edit.Redo();
        Assert.That(edit.Document.Layers.Count, Is.EqualTo(before.Layers.Count + 6));
    }

    [Test]
    public void StyleStoreLibraryAndPngRoundTripSurviveRestartAndIsolateDamage()
    {
        var fixture = CreateFixture();
        var style = SongStyles.Capture(fixture.Document, fixture.GroupA, "库样式");
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3-song-style-library-" + Guid.NewGuid().ToString("N"));
        var libraryPath = Path.Combine(root, "库 目录");
        Directory.CreateDirectory(root);
        try
        {
            var library = new SongStyleLibrary(libraryPath);
            var first = library.Add(style);
            var duplicate = library.Add(style);
            Assert.That(first.Added, Is.True);
            Assert.That(duplicate.Added, Is.False);
            Assert.That(duplicate.Entry.Id, Is.EqualTo(first.Entry.Id));
            Assert.That(Directory.GetFiles(libraryPath, "*.ppgsongstyle"), Has.Length.EqualTo(1));
            var fileName = Path.Combine(libraryPath, first.Entry.Id + SongStyleStore.Extension);
            var serialized = File.ReadAllText(fileName);
            Assert.That(serialized, Does.Not.Contain("Content A title"));
            Assert.That(serialized, Does.Not.Contain("asset"));
            Assert.That(serialized, Does.Not.Contain("GroupId"));
            Assert.That(serialized, Does.Not.Contain("Base64"));

            var different = style with
            {
                Layers = style.Layers.Select(layer => layer.Role == SongRole.Title
                    ? layer with { FontSizeRatio = 0.2 } : layer).ToArray()
            };
            var second = library.Add(different);
            Assert.That(second.Added, Is.True);
            Assert.That(second.Entry.Id, Is.Not.EqualTo(first.Entry.Id));
            Assert.That(library.Load().Entries.Select(entry => entry.Name),
                Has.All.EqualTo("库样式"));

            var corruptPath = Path.Combine(libraryPath,
                Guid.NewGuid().ToString("N") + SongStyleStore.Extension);
            File.WriteAllText(corruptPath, "{broken");
            var futurePath = Path.Combine(libraryPath,
                Guid.NewGuid().ToString("N") + SongStyleStore.Extension);
            File.WriteAllText(futurePath,
                "{\"formatVersion\":2,\"id\":\"x\",\"name\":\"future\",\"referenceAspectRatio\":2,\"layers\":[]}");
            var corruptBytes = File.ReadAllBytes(corruptPath);
            var futureBytes = File.ReadAllBytes(futurePath);
            var snapshot = new SongStyleLibrary(libraryPath).Load();
            Assert.That(snapshot.Entries, Has.Count.EqualTo(2));
            Assert.That(snapshot.Diagnostics, Has.Count.EqualTo(2));
            Assert.That(File.ReadAllBytes(corruptPath), Is.EqualTo(corruptBytes));
            Assert.That(File.ReadAllBytes(futurePath), Is.EqualTo(futureBytes));
            var reopened = SongStyleStore.Read(fileName);
            Assert.That(reopened.Name, Is.EqualTo("库样式"));
            Assert.That(reopened.Layers.Count, Is.EqualTo(6));

            var edit = new EditSession(fixture.Document);
            edit.SetSelection(fixture.Document.Layers
                .Where(layer => layer.GroupId == fixture.GroupB).Select(layer => layer.Id));
            edit.ApplySongStyle(reopened, edit.SelectedLayerIds);
            var png = TestImages.RenderPng(edit.Document, true, 640);
            var native = Path.Combine(root, "style.ppgproj");
            NativePackageStore.Save(native, edit.Document, "r3-style");
            Assert.That(TestImages.RenderPng(NativePackageStore.Read(native), true, 640),
                Is.EqualTo(png));
        }
        finally
        {
            var expected = Path.GetFullPath(TestContext.CurrentContext.WorkDirectory)
                + Path.DirectorySeparatorChar;
            Assert.That(Path.GetFullPath(root).StartsWith(expected, StringComparison.OrdinalIgnoreCase),
                Is.True);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Fixture(EditorDocument Document, Guid GroupA, Guid GroupB, Guid PlateA);

    private static Fixture CreateFixture(bool targetHasPlate = true)
    {
        var document = new EditorDocument { CanvasWidth = 1800, CanvasHeight = 1000,
            BackgroundArgb = 0xFF102030 };
        var groupA = AddGroup(document, "A", 60, withPlate: true, variant: 0, out var plateA);
        AddGroup(document, "B", 760, targetHasPlate, variant: 1, out _);
        document.Validate();
        return new(document, groupA, document.Layers.Single(layer =>
            layer.GroupId != groupA && layer.SongRole == SongRole.Title).GroupId!.Value, plateA);
    }

    private static Guid AddGroup(EditorDocument document, string name, double x,
        bool withPlate, int variant, out Guid plateId)
    {
        var group = Guid.NewGuid();
        var asset = Guid.NewGuid();
        document.AddAsset(asset, TestImages.EncodePng(40, 20,
            TestImages.SyntheticBgra(40, 20)));
        var artwork = new Layer
        {
            Kind = LayerKind.Image, Name = name + " · artwork", GroupId = group,
            SongRole = SongRole.Artwork, AssetId = asset,
            Bounds = new RectD(x, 80, variant == 0 ? 400 : 320, variant == 0 ? 225 : 180),
            Slanted = true, KeepAspect = true, ImageFit = ImageFitMode.Cover
        };
        var plate = new Layer
        {
            Kind = LayerKind.Shape, Name = name + " · plate", GroupId = group,
            SongRole = SongRole.Plate, Bounds = new RectD(x - 10, 280, 420, 110),
            Slanted = true, ColorArgb = variant == 0 ? 0xCC112233u : 0xCC334455u
        };
        var title = new Layer
        {
            Kind = LayerKind.Text, Name = name + " · title", GroupId = group,
            SongRole = SongRole.Title, Text = "Content " + name + " title",
            Bounds = new RectD(x + 10, 310, 330, 45), FontSize = variant == 0 ? 48 : 30,
            FontFamily = variant == 0 ? "Arial" : "Microsoft YaHei UI",
            ColorArgb = variant == 0 ? 0xFFFFCC33u : 0xFFFFFFFFu,
            Bold = variant == 0, Opacity = variant == 0 ? 0.75 : 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        var artist = new Layer
        {
            Kind = LayerKind.Text, Name = name + " · artist", GroupId = group,
            SongRole = SongRole.Artist, Text = "Artist " + name,
            Bounds = new RectD(x + 10, 355, 330, 32), FontSize = variant == 0 ? 24 : 20,
            FontFamily = variant == 0 ? "Arial" : "Microsoft YaHei UI",
            Visible = variant != 0, Locked = variant == 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        var charter = new Layer
        {
            Kind = LayerKind.Text, Name = name + " · charter", GroupId = group,
            SongRole = SongRole.Charter, Text = "Charter " + name,
            Bounds = new RectD(x + 10, 387, 330, 28), FontSize = variant == 0 ? 22 : 18,
            FontFamily = variant == 0 ? "Arial" : "Microsoft YaHei UI",
            VerticalAlignment = VerticalAlignment.Center
        };
        var badge = EditorDocument.NewBadge(new PointD(x + 280, 70));
        badge.Name = name + " · difficulty";
        badge.GroupId = group;
        badge.SongRole = SongRole.Badge;
        badge.Difficulty = variant == 0 ? "IN" : "HD";
        badge.Level = variant == 0 ? 15 : 12;
        document.Layers.Add(plate);
        if (!withPlate) document.Layers.Remove(plate);
        document.Layers.AddRange([artwork, title, artist, charter, badge]);
        plateId = plate.Id;
        return group;
    }
}
