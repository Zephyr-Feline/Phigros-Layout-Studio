using Editor.Application;
using Editor.Core;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R2SongInfoTests
{
    [Test]
    public void SuggestionsUseExactKnownRolesAndNeverGuessDuplicateNames()
    {
        var document = GroupDocument(("第一首", 0), ("第二首", 1));
        var groups = document.Layers.Select(layer => layer.GroupId!.Value).Distinct().ToArray();
        var suggestions = SongInfoWorkbench.Suggest(document, groups);
        Assert.That(suggestions, Has.Count.EqualTo(2));
        Assert.That(suggestions.All(row => row.ArtworkId is not null && row.TitleId is not null &&
            row.ArtistId is not null && row.CharterId is not null && row.BadgeId is not null), Is.True);

        var duplicate = document.Find(suggestions[0].TitleId!.Value).Copy();
        duplicate.Id = Guid.NewGuid();
        duplicate.Name = duplicate.Name;
        document.Layers.Add(duplicate);
        var ambiguous = SongInfoWorkbench.Suggest(document, [groups[0]]).Single();
        Assert.That(ambiguous.TitleId, Is.Null, "two matching role names must not be guessed");
    }

    [Test]
    public void ExplicitMappingUpdatesAllFieldsAndCommonStyleInOneUndo()
    {
        var document = GroupDocument(("第一首", 0), ("第二首", 1));
        var groups = document.Layers.Select(layer => layer.GroupId!.Value).Distinct().ToArray();
        var edit = new EditSession(document);
        var before = edit.Document.Copy();
        var suggestions = SongInfoWorkbench.Suggest(edit.Document, groups);
        var edits = suggestions.Select((suggestion, index) =>
        {
            var mapping = Mapping(suggestion);
            return new SongInfoEdit(mapping, new SongInfoValues(
                "新标题 " + index, "新曲师 " + index, "新谱师 " + index, "AT", 17));
        }).ToArray();
        edit.ApplySongInfoBatch(edits, new SongTextStyle(FontSize: 44, Bold: true),
            TestImages.ValidateImages);
        var after = edit.Document.Copy();
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        foreach (var item in edits)
        {
            Assert.That(edit.Document.Find(item.Mapping.TitleId).Text, Is.EqualTo(item.Values.Title));
            Assert.That(edit.Document.Find(item.Mapping.ArtistId).Text, Is.EqualTo(item.Values.Artist));
            Assert.That(edit.Document.Find(item.Mapping.CharterId).Text, Is.EqualTo(item.Values.Charter));
            Assert.That(edit.Document.Find(item.Mapping.BadgeId).Difficulty, Is.EqualTo("AT"));
            Assert.That(edit.Document.Find(item.Mapping.BadgeId).Level, Is.EqualTo(17));
            Assert.That(edit.Document.Find(item.Mapping.TitleId).FontSize, Is.EqualTo(44));
            Assert.That(edit.Document.Find(item.Mapping.ArtistId).Bold, Is.True);
            Assert.That(edit.Document.Find(item.Mapping.CharterId).Bold, Is.True);
            Assert.That(edit.Document.Find(item.Mapping.ArtworkId).Bounds,
                Is.EqualTo(before.Find(item.Mapping.ArtworkId).Bounds));
            Assert.That(edit.Document.Find(item.Mapping.ArtworkId).Name,
                Is.EqualTo(before.Find(item.Mapping.ArtworkId).Name));
            Assert.That(edit.Document.Find(item.Mapping.ArtworkId).GroupId,
                Is.EqualTo(before.Find(item.Mapping.ArtworkId).GroupId));
            Assert.That(edit.Document.Find(item.Mapping.ArtworkId).SongRole, Is.EqualTo(SongRole.Artwork));
            Assert.That(edit.Document.Find(item.Mapping.TitleId).SongRole, Is.EqualTo(SongRole.Title));
            Assert.That(edit.Document.Find(item.Mapping.ArtistId).SongRole, Is.EqualTo(SongRole.Artist));
            Assert.That(edit.Document.Find(item.Mapping.CharterId).SongRole, Is.EqualTo(SongRole.Charter));
            Assert.That(edit.Document.Find(item.Mapping.BadgeId).SongRole, Is.EqualTo(SongRole.Badge));
        }
        Assert.That(edit.Document.Layers.Select(layer => layer.Id), Is.EqualTo(before.Layers.Select(layer => layer.Id)));
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
        edit.Redo();
        Assert.That(edit.Document.StateEquals(after), Is.True);
    }

    [Test]
    public void DuplicateNamesHiddenTextAndStyleFieldsRemainIndependent()
    {
        var document = GroupDocument(("同名歌曲", 0), ("同名歌曲", 1));
        var groups = document.Layers.Select(layer => layer.GroupId!.Value).Distinct().ToArray();
        var titleLayers = groups.Select(group => document.Layers.Single(layer =>
            layer.GroupId == group && layer.Name.EndsWith("· title", StringComparison.Ordinal))).ToArray();
        titleLayers[0].Visible = false;
        var edit = new EditSession(document);
        var suggestions = SongInfoWorkbench.Suggest(edit.Document, groups);
        var edits = suggestions.Select((suggestion, index) => new SongInfoEdit(
            Mapping(suggestion), new SongInfoValues("不同标题 " + index, "曲师", "谱师", "IN", 12))).ToArray();
        edit.ApplySongInfoBatch(edits, new SongTextStyle(ColorArgb: 0xFF00FF00, Bold: false),
            TestImages.ValidateImages);
        Assert.That(edit.Document.Find(titleLayers[0].Id).Text, Is.EqualTo("不同标题 0"));
        Assert.That(edit.Document.Find(titleLayers[1].Id).Text, Is.EqualTo("不同标题 1"));
        Assert.That(edit.Document.Find(titleLayers[0].Id).Visible, Is.False);
        Assert.That(edit.Document.Find(titleLayers[0].Id).ColorArgb, Is.EqualTo(0xFF00FF00));
        Assert.That(edit.Document.Find(titleLayers[0].Id).FontSize,
            Is.EqualTo(document.Find(titleLayers[0].Id).FontSize));
    }

    [Test]
    public void ArtworkReplacementPreservesGeometryAndIsUndoable()
    {
        var document = GroupDocument(("换图歌曲", 0));
        var group = document.Layers[0].GroupId!.Value;
        var suggestion = SongInfoWorkbench.Suggest(document, [group]).Single();
        var imageId = suggestion.ArtworkId!.Value;
        var before = document.Copy();
        var oldAsset = document.Find(imageId).AssetId;
        var replacement = TestImages.EncodePng(20, 10, TestImages.SyntheticBgra(20, 10));
        var edit = new EditSession(document);
        edit.ApplySongInfoBatch([new SongInfoEdit(Mapping(suggestion),
            new SongInfoValues("换图歌曲", "曲师", "谱师", "HD", 9), replacement)],
            null, TestImages.ValidateImages);
        var after = edit.Document.Copy();
        Assert.That(edit.UndoCount, Is.EqualTo(1));
        Assert.That(edit.Document.Find(imageId).AssetId, Is.Not.EqualTo(oldAsset));
        Assert.That(edit.Document.Find(imageId).Bounds, Is.EqualTo(before.Find(imageId).Bounds));
        Assert.That(edit.Document.Find(imageId).Name, Is.EqualTo(before.Find(imageId).Name));
        Assert.That(edit.Document.Find(imageId).GroupId, Is.EqualTo(before.Find(imageId).GroupId));
        Assert.That(edit.Document.Assets.ContainsKey(oldAsset!.Value), Is.False);
        edit.Undo();
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.Document.Find(imageId).AssetId, Is.EqualTo(oldAsset));
        edit.Redo();
        Assert.That(edit.Document.StateEquals(after), Is.True);
    }

    [Test]
    public void InvalidReplacementArtworkRollsBackTheWholeRow()
    {
        var document = GroupDocument(("失败换图", 0));
        var group = document.Layers[0].GroupId!.Value;
        var suggestion = SongInfoWorkbench.Suggest(document, [group]).Single();
        var edit = new EditSession(document);
        var before = edit.Document.Copy();
        Assert.That(() => edit.ApplySongInfoBatch([new SongInfoEdit(Mapping(suggestion),
            new SongInfoValues("不应提交", "曲师", "谱师", "IN", 15), [1, 2, 3])],
            new SongTextStyle(FontSize: 50), TestImages.ValidateImages),
            Throws.InstanceOf<InvalidDataException>());
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [TestCase("locked")]
    [TestCase("empty-artwork")]
    [TestCase("duplicate-role")]
    [TestCase("wrong-kind")]
    [TestCase("difficulty")]
    [TestCase("level")]
    [TestCase("text")]
    public void InvalidExplicitMappingIsRejectedWithoutPartialChanges(string failure)
    {
        var document = GroupDocument(("错误测试", 0), ("第二首", 1));
        var groups = document.Layers.Select(layer => layer.GroupId!.Value).Distinct().ToArray();
        var suggestions = SongInfoWorkbench.Suggest(document, groups);
        if (failure == "locked")
            document.Layers.First(layer => layer.GroupId == groups[0]).Locked = true;
        var edit = new EditSession(document);
        var selection = edit.SelectedLayerIds.ToArray();
        var before = edit.Document.Copy();
        var history = (edit.UndoCount, edit.RedoCount, edit.IsDirty);
        var mapping = Mapping(suggestions[0]);
        var values = new SongInfoValues("标题", "曲师", "谱师", "IN", 15);
        if (failure == "empty-artwork") mapping = mapping with { ArtworkId = Guid.Empty };
        if (failure == "duplicate-role") mapping = mapping with { ArtistId = mapping.TitleId };
        if (failure == "wrong-kind") mapping = mapping with { BadgeId = mapping.TitleId };
        if (failure == "difficulty") values = values with { Difficulty = "XX" };
        if (failure == "level") values = values with { Level = 0 };
        if (failure == "text") values = values with { Title = new string('长', 4001) };
        Assert.That(() => edit.ApplySongInfoBatch(
            [new SongInfoEdit(mapping, values)], null, TestImages.ValidateImages), Throws.Exception);
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(selection));
        Assert.That((edit.UndoCount, edit.RedoCount, edit.IsDirty), Is.EqualTo(history));
    }

    [Test]
    public void NoChangeAndSaveReopenKeepTheEditedPixels()
    {
        var document = GroupDocument(("保存测试", 0));
        var group = document.Layers[0].GroupId!.Value;
        var suggestion = SongInfoWorkbench.Suggest(document, [group]).Single();
        var edit = new EditSession(document);
        var noChange = new SongInfoEdit(Mapping(suggestion),
            new SongInfoValues(suggestion.Title, suggestion.Artist, suggestion.Charter,
                suggestion.Difficulty, suggestion.Level));
        edit.ApplySongInfoBatch([noChange], null, TestImages.ValidateImages);
        Assert.That(edit.UndoCount, Is.Zero);
        edit.ApplySongInfoBatch([new SongInfoEdit(Mapping(suggestion),
            new SongInfoValues("保存后的标题", "曲师", "谱师", "AT", 18), null)],
            null, TestImages.ValidateImages);
        var pixels = TestImages.RenderPng(edit.Document);
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r2-song-info-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "r2-song-info");
            Assert.That(TestImages.RenderPng(NativePackageStore.Read(path)), Is.EqualTo(pixels));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void PersistedRolesSurviveRenamingAndSaveReopen()
    {
        var document = GroupDocument(("角色歌曲", 0));
        var group = document.Layers[0].GroupId!.Value;
        var suggestion = SongInfoWorkbench.Suggest(document, [group]).Single();
        var edit = new EditSession(document);
        edit.ApplySongInfoBatch([new SongInfoEdit(Mapping(suggestion),
            new SongInfoValues("角色标题", "角色曲师", "角色谱师", "AT", 16))],
            null, TestImages.ValidateImages);
        var title = edit.Document.Find(suggestion.TitleId!.Value);
        title.Name = "用户改名后的标题层";
        Assert.That(SongInfoWorkbench.Suggest(edit.Document, [group]).Single().TitleId,
            Is.EqualTo(title.Id));
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3-role-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "r3-role");
            var reopened = NativePackageStore.Read(path);
            Assert.That(reopened.Find(title.Id).SongRole, Is.EqualTo(SongRole.Title));
            Assert.That(SongInfoWorkbench.Suggest(reopened, [group]).Single().TitleId,
                Is.EqualTo(title.Id));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Test]
    public void DuplicateOrMismatchedPersistedRolesAreRejected()
    {
        var document = GroupDocument(("角色校验", 0));
        var group = document.Layers[0].GroupId!.Value;
        var layers = document.Layers.Where(layer => layer.GroupId == group).ToArray();
        layers[1].SongRole = SongRole.Title;
        layers[2].SongRole = SongRole.Title;
        Assert.That(() => document.Validate(), Throws.InstanceOf<InvalidDataException>());
        layers[2].SongRole = SongRole.None;
        layers[0].SongRole = SongRole.Title;
        Assert.That(() => document.Validate(), Throws.InstanceOf<InvalidDataException>());
        layers[0].SongRole = SongRole.None;
        layers[1].SongRole = SongRole.None;
        layers[1].GroupId = null;
        layers[1].SongRole = SongRole.Title;
        Assert.That(() => document.Validate(), Throws.InstanceOf<InvalidDataException>());
    }

    private static SongInfoMapping Mapping(SongInfoSuggestion suggestion) => new(
        suggestion.GroupId, suggestion.ArtworkId!.Value, suggestion.TitleId!.Value,
        suggestion.ArtistId!.Value, suggestion.CharterId!.Value, suggestion.BadgeId!.Value);

    private static EditorDocument GroupDocument(params (string Name, int Index)[] groups)
    {
        var document = new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600 };
        foreach (var (name, index) in groups)
        {
            var group = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            document.AddAsset(assetId, TestImages.EncodePng(20, 10, TestImages.SyntheticBgra(20, 10)));
            var artwork = new Layer
            {
                Kind = LayerKind.Image, Name = name + " · artwork", GroupId = group,
                SongRole = SongRole.Artwork, AssetId = assetId,
                Bounds = new RectD(40 + index * 400, 40, 200, 120)
            };
            var title = new Layer
            {
                Kind = LayerKind.Text, Name = name + " · title", Text = name + " 标题",
                GroupId = group, SongRole = SongRole.Title,
                Bounds = new RectD(40 + index * 400, 170, 220, 40)
            };
            var artist = new Layer
            {
                Kind = LayerKind.Text, Name = name + " · artist", Text = name + " 曲师",
                GroupId = group, SongRole = SongRole.Artist,
                Bounds = new RectD(40 + index * 400, 210, 220, 35)
            };
            var charter = new Layer
            {
                Kind = LayerKind.Text, Name = name + " · charter", Text = name + " 谱师",
                GroupId = group, SongRole = SongRole.Charter,
                Bounds = new RectD(40 + index * 400, 245, 220, 30)
            };
            var badge = EditorDocument.NewBadge(new PointD(250 + index * 400, 45));
            badge.Name = name + " · difficulty";
            badge.GroupId = group;
            badge.SongRole = SongRole.Badge;
            badge.Difficulty = "IN";
            badge.Level = 15;
            document.Layers.AddRange([artwork, title, artist, charter, badge]);
        }
        document.Validate();
        return document;
    }
}
