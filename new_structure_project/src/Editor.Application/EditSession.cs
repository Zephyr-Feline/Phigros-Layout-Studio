using Editor.Core;

namespace Editor.Application;

public enum LayerOrderAction { Forward, Backward, Front, Back }

public sealed class EditSession
{
    /// <summary>
    /// Product history policy: 128 reversible states, configurable per session between 1 and 10000.
    /// Snapshots share identical immutable payloads; distinct replaced images remain retained.
    /// The current-document admission budget is not a byte limit for these history stacks.
    /// </summary>
    public const int DefaultMaxHistoryEntries = 128;

    public const int MaxAllowedHistoryEntries = 10000;

    private readonly Stack<HistoryState> _undo = new();
    private readonly Stack<HistoryState> _redo = new();
    private EditorDocument? _gestureStart;
    private Guid _gestureLayerId;
    private Guid[] _gestureTargetIds = [];
    private readonly int _maxHistoryEntries;
    internal long MaxDocumentAssetBytes { get; }
    private long _nextRevision;
    private long _revision;
    private long _savedRevision;
    private Guid? _selectedLayerId;
    private Guid[] _selectedLayerIds = [];
    private string _gestureLabel = "";

    private readonly record struct HistoryState(EditorDocument Document, long Revision, string Label);

    /// <summary>Maximum number of labels shown by the read-only history summary.</summary>
    public const int HistorySummaryLimit = 5;

    /// <summary>
    /// Raised only when the whole document is replaced (new/open/import), not by undo/redo.
    /// Session-only presentation state such as rulers and manual guides can reset here.
    /// </summary>
    public event Action? DocumentReplaced;

    public EditorDocument Document { get; private set; }
    public Guid? SelectedLayerId
    {
        get => _selectedLayerId;
        set
        {
            _selectedLayerId = value;
            _selectedLayerIds = value is Guid id ? [id] : [];
        }
    }
    public IReadOnlyList<Guid> SelectedLayerIds => _selectedLayerIds;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public string? UndoLabel => _undo.Count == 0 ? null : _undo.Peek().Label;
    public string? RedoLabel => _redo.Count == 0 ? null : _redo.Peek().Label;
    public IReadOnlyList<string> RecentUndoLabels =>
        _undo.Take(HistorySummaryLimit).Select(state => state.Label).ToArray();
    public IReadOnlyList<string> RecentRedoLabels =>
        _redo.Take(HistorySummaryLimit).Select(state => state.Label).ToArray();
    public bool IsDirty => _revision != _savedRevision;
    public int MaxHistoryEntries => _maxHistoryEntries;
    public IReadOnlyList<Guid> ActiveGestureIds => _gestureTargetIds;
    public RectD ActiveGestureBounds => MoveSnapping.BoundsOf(_gestureTargetIds.Select(id =>
        LayerGeometry.VisualBounds((_gestureStart ?? throw new InvalidOperationException("No active gesture.")).Find(id))));

    public IReadOnlyList<Guid> GestureTargets(Guid layerId, bool includeGroup = false, bool includeSelection = true)
    {
        var selected = Document.Find(layerId);
        if (selected.Locked)
            throw new InvalidOperationException("Locked layers cannot be moved or resized.");
        var directlySelected = includeSelection && _selectedLayerIds.Contains(layerId)
            ? _selectedLayerIds.ToHashSet() : new HashSet<Guid> { layerId };
        var groups = includeGroup ? Document.Layers.Where(layer => directlySelected.Contains(layer.Id))
            .Select(layer => layer.GroupId).OfType<Guid>().ToHashSet() : [];
        return Document.Layers.Where(layer => !layer.Locked &&
            (directlySelected.Contains(layer.Id) || (includeGroup && layer.GroupId is Guid group && groups.Contains(group))))
            .Select(layer => layer.Id).ToArray();
    }

    public EditSession(EditorDocument document, int maxHistoryEntries = DefaultMaxHistoryEntries)
        : this(document, maxHistoryEntries, AssetResourceBudget.MaxDocumentBytes) { }

    // A smaller internal budget keeps boundary regressions bounded without allocating large files.
    internal EditSession(EditorDocument document, int maxHistoryEntries, long maxDocumentAssetBytes)
    {
        if (maxDocumentAssetBytes is < 1 or > AssetResourceBudget.MaxDocumentBytes)
            throw new ArgumentOutOfRangeException(nameof(maxDocumentAssetBytes));
        MaxDocumentAssetBytes = maxDocumentAssetBytes;
        if (maxHistoryEntries is < 1 or > MaxAllowedHistoryEntries)
            throw new ArgumentOutOfRangeException(nameof(maxHistoryEntries));
        document.Validate();
        Document = document.Copy();
        _maxHistoryEntries = maxHistoryEntries;
    }

    public void MarkSaved()
    {
        EnsureNoGesture();
        _savedRevision = _revision;
    }

    /// <summary>Reject persistence of a transient drag or resize preview before any bytes are written.</summary>
    public void EnsureReadyForPersistence() => EnsureNoGesture();

    public void AddLayer(Layer layer)
    {
        EnsureNoGesture();
        var before = Document.Copy();
        Document.Layers.Add(layer.Copy());
        try { Document.Validate(); }
        catch { Document = before; throw; }
        Record(before, "添加图层");
        SelectedLayerId = layer.Id;
    }

    /// <summary>Add one editable Original plate and caption without introducing a new file type.</summary>
    public void AddOriginalTag(Guid? artworkId = null)
    {
        EnsureNoGesture();
        var artwork = artworkId is Guid id ? Document.Find(id) : null;
        if (artwork is not null && artwork.Kind != LayerKind.Image)
            throw new InvalidOperationException("Select an image layer or add a standalone label.");
        var group = artwork?.GroupId ?? Guid.NewGuid();
        var layers = LayerPresets.OriginalTag(Document, artwork, group);
        UpdateDocument(document => document.Layers.AddRange(layers), "添加 Original 标签");
        SetSelection([layers[1].Id, layers[0].Id]);
    }

    /// <summary>Wrap existing artwork in the free editor's six-piece song composition.</summary>
    public void CreateSongCard(Guid artworkId)
    {
        EnsureNoGesture();
        var artwork = Document.Find(artworkId);
        if (artwork.Kind != LayerKind.Image || artwork.Locked)
            throw new InvalidOperationException("Select an unlocked image layer to build a song card.");
        var group = artwork.GroupId ?? Guid.NewGuid();
        if (Document.Layers.Any(layer => layer.GroupId == group &&
            layer.SongRole != SongRole.None))
            throw new InvalidOperationException("该图片所在组已经包含歌曲角色，请先移除旧卡片图层。");
        UpdateDocument(document =>
        {
            var image = document.Find(artworkId);
            var index = document.Layers.IndexOf(image);
            var companions = LayerPresets.SongCompanions(image, group);
            image.GroupId = group;
            image.SongRole = SongRole.Artwork;
            image.Slanted = true;
            image.KeepAspect = true;
            document.Layers.Insert(index, companions[0]);
            document.Layers.InsertRange(index + 2, companions.Skip(1));
        }, "创建歌曲卡片");
        SelectedLayerId = artworkId;
    }

    public void AddImageLayer(Layer layer, byte[] imageBytes)
    {
        EnsureNoGesture();
        AddImageLayer(layer, new AssetBlob(imageBytes));
    }

    public void AddImageLayer(Layer layer, AssetBlob incoming)
    {
        EnsureNoGesture();
        ArgumentNullException.ThrowIfNull(incoming);
        if (layer.Kind != LayerKind.Image || layer.AssetId is not Guid assetId)
            throw new ArgumentException("A nonempty image and asset ID are required.", nameof(layer));
        var before = Document.Copy();
        try
        {
            var retained = Document.Assets.Values
                .Concat(_undo.SelectMany(state => state.Document.Assets.Values))
                .Concat(_redo.SelectMany(state => state.Document.Assets.Values))
                .FirstOrDefault(asset => asset.ContentEquals(incoming)) ?? incoming;
            Document.AddAsset(assetId, retained);
            Document.Layers.Add(layer.Copy());
            Document.Validate();
            ValidateAssetChange(before);
        }
        catch { Document = before; throw; }
        Record(before, "导入图片");
        SelectedLayerId = layer.Id;
    }

    /// <summary>
    /// Adds a validated image batch as one edit. Asset payloads are shared with existing and
    /// historical snapshots when the bytes are identical; every new layer is selected in order.
    /// </summary>
    public void AddImageLayers(IReadOnlyList<(Layer Layer, byte[] ImageBytes)> entries)
    {
        EnsureNoGesture();
        ArgumentNullException.ThrowIfNull(entries);
        var retained = Document.Assets.Values.DistinctBy(asset => asset.Sha256)
            .ToDictionary(asset => asset.Sha256, StringComparer.Ordinal);
        var bytes = AssetResourceBudget.UniqueBytes(Document.Assets.Values);
        var allowed = Math.Max(MaxDocumentAssetBytes, bytes);
        var payloads = new List<(Layer Layer, AssetBlob Image)>(entries.Count);
        foreach (var entry in entries)
        {
            var incoming = new AssetBlob(entry.ImageBytes);
            if (retained.TryGetValue(incoming.Sha256, out var existing))
            {
                if (!existing.ContentEquals(incoming)) throw new InvalidDataException("Asset content-hash collision.");
                incoming = existing;
            }
            else
            {
                bytes = AssetResourceBudget.AddUniqueBytes(bytes, incoming.Length, allowed);
                retained.Add(incoming.Sha256, incoming);
            }
            payloads.Add((entry.Layer, incoming));
        }
        AddImagePayloadLayers(payloads);
    }

    public void AddImagePayloadLayers(IReadOnlyList<(Layer Layer, AssetBlob Image)> entries)
    {
        EnsureNoGesture();
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;
        var before = Document.Copy();
        var ids = new List<Guid>(entries.Count);
        try
        {
            var historyAssets = _undo.Concat(_redo).SelectMany(state => state.Document.Assets.Values);
            foreach (var (layer, incoming) in entries)
            {
                if (layer is null || layer.Kind != LayerKind.Image ||
                    layer.AssetId is not Guid assetId || incoming is null)
                    throw new ArgumentException("Every batch entry needs a nonempty image and asset ID.", nameof(entries));
                var retained = Document.Assets.Values.Concat(historyAssets)
                    .FirstOrDefault(asset => asset.ContentEquals(incoming)) ?? incoming;
                Document.AddAsset(assetId, retained);
                Document.Layers.Add(layer.Copy());
                ids.Add(layer.Id);
            }
            Document.Validate();
            ValidateAssetChange(before);
        }
        catch
        {
            Document = before;
            throw;
        }
        Record(before, "批量导入图片");
        SetSelection(ids);
    }

    /// <summary>Replace image bytes without changing the layer's identity, geometry or group.</summary>
    public void ReplaceImageLayer(Guid layerId, byte[] imageBytes) =>
        ReplaceImageLayer(layerId, new AssetBlob(imageBytes));

    public void ReplaceImageLayer(Guid layerId, AssetBlob incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        EnsureNoGesture();
        var layer = Document.Find(layerId);
        if (layer.Kind != LayerKind.Image || layer.AssetId is not Guid currentId)
            throw new InvalidOperationException("Select an image layer to replace.");
        if (Document.Assets[currentId].ContentEquals(incoming)) return;
        var before = Document.Copy();
        try
        {
            var retained = Document.Assets.Values
                .Concat(_undo.SelectMany(state => state.Document.Assets.Values))
                .Concat(_redo.SelectMany(state => state.Document.Assets.Values))
                .FirstOrDefault(asset => asset.ContentEquals(incoming)) ?? incoming;
            var replacementId = Guid.NewGuid();
            Document.AddAsset(replacementId, retained);
            layer.AssetId = replacementId;
            Document.RemoveUnreferencedAssets();
            Document.Validate();
            ValidateAssetChange(before);
        }
        catch { Document = before; throw; }
        Record(before, "替换图片");
    }

    /// <summary>
    /// Safely replace every current document reference to one embedded asset. The old asset ID
    /// remains available to history snapshots; the replacement gets a new ID and all layer and
    /// fixed-background references move together in one edit.
    /// </summary>
    public AssetReplacement ReplaceAsset(Guid assetId, byte[] imageBytes,
        Action<EditorDocument>? validateImages = null)
    {
        EnsureNoGesture();
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0 || !Document.Assets.ContainsKey(assetId))
            throw new InvalidDataException("The selected embedded image asset is missing.");
        var references = Document.Layers.Where(layer => layer.AssetId == assetId).ToArray();
        var fixedBackground = Document.FixedBackgroundAssetId == assetId;
        if (references.Length == 0 && !fixedBackground)
            throw new InvalidOperationException("The selected asset has no current document references.");
        var before = Document.Copy();
        var replacementId = Guid.NewGuid();
        try
        {
            var incoming = new AssetBlob(imageBytes);
            var retained = Document.Assets.Values
                .Concat(_undo.SelectMany(state => state.Document.Assets.Values))
                .Concat(_redo.SelectMany(state => state.Document.Assets.Values))
                .FirstOrDefault(asset => asset.ContentEquals(incoming)) ?? incoming;
            Document.AddAsset(replacementId, retained);
            foreach (var layer in references) layer.AssetId = replacementId;
            if (fixedBackground) Document.FixedBackgroundAssetId = replacementId;
            // Remove only the replaced asset; unrelated unreferenced inventory is user data.
            if (!Document.Layers.Any(layer => layer.AssetId == assetId) &&
                Document.FixedBackgroundAssetId != assetId)
                Document.Assets.Remove(assetId);
            Document.Validate();
            ValidateAssetChange(before);
            validateImages?.Invoke(Document);
        }
        catch
        {
            Document = before;
            throw;
        }
        Record(before, "替换资源图片");
        return new(replacementId, references.Select(layer => layer.Id).ToArray());
    }

    /// <summary>
    /// Removes embedded assets with no current layer or fixed-background reference as one
    /// reversible edit. Assets retained only by undo/redo snapshots stay available there.
    /// </summary>
    public AssetCleanupResult CleanUnreferencedAssets()
    {
        EnsureNoGesture();
        var plan = AssetResources.PlanCleanup(Document);
        if (!plan.Changed) return plan;
        var before = Document.Copy();
        try
        {
            Document.RemoveUnreferencedAssets();
            Document.Validate();
        }
        catch
        {
            Document = before;
            throw;
        }
        RecordIfChanged(before, "清理未引用资源");
        return plan;
    }

    /// <summary>Store an already validated image as the fixed canvas background.</summary>
    public void SetFixedBackground(byte[] imageBytes) =>
        SetFixedBackground(new AssetBlob(imageBytes));

    public void SetFixedBackground(AssetBlob incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        EnsureNoGesture();
        if (Document.FixedBackgroundAssetId is Guid currentId &&
            Document.Assets[currentId].ContentEquals(incoming)) return;
        var before = Document.Copy();
        try
        {
            var retained = Document.Assets.Values
                .Concat(_undo.SelectMany(state => state.Document.Assets.Values))
                .Concat(_redo.SelectMany(state => state.Document.Assets.Values))
                .FirstOrDefault(asset => asset.ContentEquals(incoming)) ?? incoming;
            var id = Guid.NewGuid();
            Document.AddAsset(id, retained);
            Document.FixedBackgroundAssetId = id;
            Document.RemoveUnreferencedAssets();
            Document.Validate();
            ValidateAssetChange(before);
        }
        catch { Document = before; throw; }
        Record(before, "设置固定背景");
    }

    public void ClearFixedBackground()
    {
        EnsureNoGesture();
        if (Document.FixedBackgroundAssetId is null) return;
        UpdateDocument(document => document.FixedBackgroundAssetId = null, "清除固定背景");
    }

    public void UpdateLayer(Guid id, Action<Layer> change, string label = "编辑图层")
    {
        EnsureNoGesture();
        var before = Document.Copy();
        try
        {
            var layer = Document.Find(id);
            change(layer);
            if (layer.Id != id)
                throw new InvalidOperationException("Layer ID cannot change during an edit.");
            Document.Validate();
            Document.RemoveUnreferencedAssets();
        }
        catch { Document = before; throw; }
        RecordIfChanged(before, label);
    }

    /// <summary>
    /// Applies one explicit style patch to an ordered, homogeneous text or badge selection.
    /// All targets are checked before mutation; the whole patch is one undo entry.
    /// </summary>
    public void ApplyLayerStyleBatch(IEnumerable<Guid> ids, LayerStylePatch patch)
    {
        EnsureNoGesture();
        ArgumentNullException.ThrowIfNull(patch);
        var targets = ids.Distinct().ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException("Select at least one layer for a batch style.");
        var layers = targets.Select(Document.Find).ToArray();
        var kind = layers[0].Kind;
        if (kind is not (LayerKind.Text or LayerKind.Badge))
            throw new InvalidOperationException("Batch styles support text layers or difficulty badges only.");
        if (layers.Any(layer => layer.Kind != kind))
            throw new InvalidOperationException("Select layers of one type for batch styling.");
        if (layers.Any(layer => layer.Locked))
            throw new InvalidOperationException("Unlock every target layer before applying batch styles.");
        patch.Validate(kind);
        if (patch.IsEmpty)
            throw new InvalidOperationException("Choose at least one style field to apply.");
        UpdateDocument(document =>
        {
            foreach (var id in targets) patch.ApplyTo(document.Find(id));
        }, "批量应用图层样式");
    }

    /// <summary>
    /// Applies explicitly mapped song fields and optional artwork replacements as one edit. It
    /// changes only role text/style/image data, never geometry, names, groups or paint order.
    /// </summary>
    public void ApplySongInfoBatch(IReadOnlyList<SongInfoEdit> edits, SongTextStyle? commonStyle,
        Action<EditorDocument> validateImages)
    {
        EnsureNoGesture();
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(validateImages);
        if (edits.Count == 0) throw new InvalidOperationException("请至少选择一个歌曲组。");
        commonStyle?.Validate();
        for (var rowIndex = 0; rowIndex < edits.Count; rowIndex++)
        {
            var edit = edits[rowIndex];
            if (edit is null || edit.Mapping is null || edit.Values is null ||
                edit.Values.Title is null || edit.Values.Artist is null ||
                edit.Values.Charter is null || edit.Values.Difficulty is null)
                throw newInvalidSongInfo();
            var mapping = edit.Mapping;
            var group = Document.Layers.Where(layer => layer.GroupId == mapping.GroupId).ToArray();
            if (group.Length == 0)
                throw new InvalidOperationException($"第 {rowIndex + 1} 行对应的歌曲组不存在或没有成员。");
            if (group.Any(layer => layer.Locked))
                throw new InvalidOperationException($"第 {rowIndex + 1} 行的歌曲组包含锁定图层，请先解锁整组再编辑。");
            var ids = new[]
            {
                mapping.ArtworkId, mapping.TitleId, mapping.ArtistId, mapping.CharterId, mapping.BadgeId
            };
            if (ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
                throw new InvalidOperationException($"第 {rowIndex + 1} 行的歌曲角色映射必须完整且每个角色只对应一个不同图层。");
            foreach (var id in ids)
                if (!group.Any(layer => layer.Id == id))
                    throw new InvalidOperationException($"第 {rowIndex + 1} 行的歌曲角色映射包含不属于该组的图层。");
            if (Document.Find(mapping.ArtworkId).Kind != LayerKind.Image ||
                Document.Find(mapping.TitleId).Kind != LayerKind.Text ||
                Document.Find(mapping.ArtistId).Kind != LayerKind.Text ||
                Document.Find(mapping.CharterId).Kind != LayerKind.Text ||
                Document.Find(mapping.BadgeId).Kind != LayerKind.Badge)
                throw new InvalidOperationException($"第 {rowIndex + 1} 行的歌曲角色映射图层类型不正确。");
            if (edit.Values.Title.Length > 4000 || edit.Values.Artist.Length > 4000 ||
                edit.Values.Charter.Length > 4000)
                throw new InvalidDataException($"第 {rowIndex + 1} 行的歌曲文字不能超过 4000 个字符。");
            if (edit.Values.Difficulty is not ("EZ" or "HD" or "IN" or "AT") ||
                edit.Values.Level < 1)
                throw new InvalidDataException($"第 {rowIndex + 1} 行的难度必须是 EZ/HD/IN/AT，等级必须是正整数。");
        }
        var before = Document.Copy();
        try
        {
            var historyAssets = _undo.Concat(_redo).SelectMany(state => state.Document.Assets.Values);
            foreach (var edit in edits)
            {
                var title = Document.Find(edit.Mapping.TitleId);
                var artist = Document.Find(edit.Mapping.ArtistId);
                var charter = Document.Find(edit.Mapping.CharterId);
                var badge = Document.Find(edit.Mapping.BadgeId);
                foreach (var layer in Document.Layers.Where(layer =>
                    layer.GroupId == edit.Mapping.GroupId &&
                    layer.SongRole is SongRole.Artwork or SongRole.Title or SongRole.Artist or
                        SongRole.Charter or SongRole.Badge))
                    layer.SongRole = SongRole.None;
                Document.Find(edit.Mapping.ArtworkId).SongRole = SongRole.Artwork;
                Document.Find(edit.Mapping.TitleId).SongRole = SongRole.Title;
                Document.Find(edit.Mapping.ArtistId).SongRole = SongRole.Artist;
                Document.Find(edit.Mapping.CharterId).SongRole = SongRole.Charter;
                Document.Find(edit.Mapping.BadgeId).SongRole = SongRole.Badge;
                title.Text = edit.Values.Title;
                artist.Text = edit.Values.Artist;
                charter.Text = edit.Values.Charter;
                badge.Difficulty = edit.Values.Difficulty;
                badge.Level = edit.Values.Level;
                if (commonStyle is not null)
                {
                    commonStyle.ApplyTo(title);
                    commonStyle.ApplyTo(artist);
                    commonStyle.ApplyTo(charter);
                }
                if (edit.ReplacementArtwork is { Length: > 0 } bytes)
                {
                    var incoming = new AssetBlob(bytes);
                    var retained = Document.Assets.Values.Concat(historyAssets)
                        .FirstOrDefault(asset => asset.ContentEquals(incoming)) ?? incoming;
                    var assetId = Guid.NewGuid();
                    Document.AddAsset(assetId, retained);
                    Document.Find(edit.Mapping.ArtworkId).AssetId = assetId;
                }
            }
            Document.RemoveUnreferencedAssets();
            Document.Validate();
            ValidateAssetChange(before);
            validateImages(Document);
        }
        catch
        {
            Document = before;
            throw;
        }
        RecordIfChanged(before, "更新歌曲信息");

        static InvalidDataException newInvalidSongInfo() =>
            new("歌曲信息行缺少必要的角色映射或文字字段。");
    }

    public void UpdateDocument(Action<EditorDocument> change, string label = "编辑文档")
    {
        EnsureNoGesture();
        var before = Document.Copy();
        try
        {
            change(Document);
            Document.Validate();
            Document.RemoveUnreferencedAssets();
            ValidateAssetChange(before);
        }
        catch { Document = before; throw; }
        RecordIfChanged(before, label);
    }

    public void ApplyLayoutTemplate(string templateId, IEnumerable<Guid> selectedIds)
    {
        var ids = selectedIds.ToArray();
        UpdateDocument(document => LayoutTemplates.Apply(document, ids, templateId), "应用布局模板");
    }

    public LayoutTemplate CaptureLayoutTemplate(string name, IEnumerable<Guid> selectedIds)
    {
        EnsureNoGesture();
        return LayoutTemplates.Capture(Document, selectedIds, name);
    }

    public void ApplyLayoutTemplate(LayoutTemplate template, IEnumerable<Guid> selectedIds)
    {
        var ids = selectedIds.ToArray();
        UpdateDocument(document => LayoutTemplates.Apply(document, ids, template), "应用布局模板");
    }

    /// <summary>Apply one content-free style to every selected song group as one edit.</summary>
    public void ApplySongStyle(SongStyle style, IEnumerable<Guid> selectedIds)
    {
        ArgumentNullException.ThrowIfNull(style);
        var groups = selectedIds.Distinct().Select(id => Document.Find(id).GroupId)
            .OfType<Guid>().Distinct().ToArray();
        if (groups.Length == 0)
            throw new InvalidOperationException("请先选择至少一个带有持久角色的歌曲组。");
        UpdateDocument(document =>
        {
            foreach (var group in groups) SongStyles.Apply(document, style, group);
        }, "应用歌曲样式");
    }

    /// <summary>
    /// Build a new group from one selected song's content and the chosen style. Layer, group and
    /// embedded-image references are new; the source group and the library entry stay unchanged.
    /// </summary>
    public IReadOnlyList<Guid> InstantiateSongStyle(SongStyle style, Guid sourceLayerId)
    {
        EnsureNoGesture();
        ArgumentNullException.ThrowIfNull(style);
        var source = Document.Find(sourceLayerId);
        if (source.GroupId is not Guid group)
            throw new InvalidOperationException("请选择歌曲组内的一个图层作为内容来源。");
        var before = Document.Copy();
        IReadOnlyList<Layer> layers;
        try
        {
            layers = SongStyles.Instantiate(Document, style, group);
            Document.Validate();
        }
        catch
        {
            Document = before;
            throw;
        }
        Record(before, "实例化歌曲样式");
        SetSelection(layers.Select(layer => layer.Id));
        return layers.Select(layer => layer.Id).ToArray();
    }

    public void ArrangeSelection(ArrangeAction action, ArrangeReference reference, bool includeGroups = true)
    {
        var ids = _selectedLayerIds.ToArray();
        UpdateDocument(document => SelectionArrangement.Apply(document, ids, action, reference, includeGroups),
            "对齐或分布图层");
    }

    /// <summary>Fit the existing logical layout into a new canvas with one uniform scale.</summary>
    public void ResizeCanvasToFit(int width, int height)
    {
        if (width is < 320 or > 7680 || height is < 180 or > 4320)
            throw new InvalidDataException("Canvas dimensions are outside the P1 range.");
        UpdateDocument(document =>
        {
            var oldWidth = document.CanvasWidth;
            var oldHeight = document.CanvasHeight;
            var scale = Math.Min((double)width / oldWidth, (double)height / oldHeight);
            var offsetX = (width - oldWidth * scale) / 2;
            var offsetY = (height - oldHeight * scale) / 2;
            foreach (var layer in document.Layers)
            {
                var box = layer.Bounds;
                layer.Bounds = new RectD(offsetX + box.X * scale, offsetY + box.Y * scale,
                    box.Width * scale, box.Height * scale);
                if (layer.Kind == LayerKind.Text)
                    layer.FontSize = Math.Clamp(layer.FontSize * scale, 1, 1000);
            }
            document.CanvasWidth = width;
            document.CanvasHeight = height;
        }, "调整画布并适配内容");
    }

    /// <summary>Move the editable selection by a document-unit step as one undoable edit.</summary>
    public bool NudgeSelection(double deltaX, double deltaY, bool includeGroup = true)
    {
        EnsureNoGesture();
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new ArgumentOutOfRangeException(nameof(deltaX));
        if (deltaX == 0 && deltaY == 0) return false;
        var primary = _selectedLayerIds.FirstOrDefault(id => !Document.Find(id).Locked);
        if (primary == Guid.Empty) return false;
        var revision = _revision;
        BeginGesture(primary, includeGroup, label: "微调图层");
        try
        {
            MoveBy(deltaX, deltaY);
            CommitGesture();
        }
        catch
        {
            CancelGesture();
            throw;
        }
        return _revision != revision;
    }

    public void MoveLayerToIndex(Guid id, int index)
    {
        EnsureNoGesture();
        if (index < 0 || index >= Document.Layers.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var oldIndex = Document.Layers.FindIndex(layer => layer.Id == id);
        if (oldIndex < 0) throw new KeyNotFoundException($"Layer {id} not found.");
        if (oldIndex == index) return;
        var before = Document.Copy();
        var layer = Document.Layers[oldIndex];
        Document.Layers.RemoveAt(oldIndex);
        Document.Layers.Insert(index, layer);
        Record(before, "调整图层叠放顺序");
    }

    /// <summary>Reorders the explicit selection as one edit, preserving its relative paint order.</summary>
    public void ReorderLayers(IEnumerable<Guid> ids, LayerOrderAction action)
    {
        EnsureNoGesture();
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        var chosen = ids.ToHashSet();
        foreach (var id in chosen) Document.Find(id);
        if (chosen.Count == 0) return;
        var ordered = Document.Layers.ToList();
        switch (action)
        {
            case LayerOrderAction.Front:
                ordered = ordered.Where(layer => !chosen.Contains(layer.Id))
                    .Concat(ordered.Where(layer => chosen.Contains(layer.Id))).ToList();
                break;
            case LayerOrderAction.Back:
                ordered = ordered.Where(layer => chosen.Contains(layer.Id))
                    .Concat(ordered.Where(layer => !chosen.Contains(layer.Id))).ToList();
                break;
            case LayerOrderAction.Forward:
                for (var i = ordered.Count - 2; i >= 0; i--)
                    if (chosen.Contains(ordered[i].Id) && !chosen.Contains(ordered[i + 1].Id))
                        (ordered[i], ordered[i + 1]) = (ordered[i + 1], ordered[i]);
                break;
            case LayerOrderAction.Backward:
                for (var i = 1; i < ordered.Count; i++)
                    if (chosen.Contains(ordered[i].Id) && !chosen.Contains(ordered[i - 1].Id))
                        (ordered[i], ordered[i - 1]) = (ordered[i - 1], ordered[i]);
                break;
        }
        if (ordered.Select(layer => layer.Id).SequenceEqual(Document.Layers.Select(layer => layer.Id))) return;
        var before = Document.Copy();
        Document.Layers.Clear();
        Document.Layers.AddRange(ordered);
        Record(before, "调整图层叠放顺序");
    }

    /// <summary>
    /// Moves an explicit selection next to a target in document order. The target row's
    /// "above" direction means later in document order, because the layer list is reversed.
    /// </summary>
    public bool MoveLayersRelativeTo(IEnumerable<Guid> ids, Guid targetId, bool aboveTarget)
    {
        EnsureNoGesture();
        var chosen = ids.ToHashSet();
        foreach (var id in chosen) Document.Find(id);
        Document.Find(targetId);
        if (chosen.Count == 0 || chosen.Contains(targetId)) return false;

        var ordered = Document.Layers.ToList();
        var moving = ordered.Where(layer => chosen.Contains(layer.Id)).ToList();
        var remaining = ordered.Where(layer => !chosen.Contains(layer.Id)).ToList();
        var targetIndex = remaining.FindIndex(layer => layer.Id == targetId);
        if (targetIndex < 0) throw new KeyNotFoundException($"Layer {targetId} not found.");
        var insertIndex = aboveTarget ? targetIndex + 1 : targetIndex;
        var reordered = remaining.Take(insertIndex).Concat(moving)
            .Concat(remaining.Skip(insertIndex)).ToList();
        if (reordered.Select(layer => layer.Id).SequenceEqual(ordered.Select(layer => layer.Id)))
            return false;

        var before = Document.Copy();
        Document.Layers.Clear();
        Document.Layers.AddRange(reordered);
        Record(before, "拖拽重排图层");
        return true;
    }

    /// <summary>Fully enclosed visible, unlocked layer boxes; selection never changes history.</summary>
    public void SelectCanvasRegion(RectD region, IEnumerable<Guid>? additiveSelection = null)
    {
        EnsureNoGesture();
        if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) ||
            !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
            region.Width < 0 || region.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(region));
        var enclosed = Document.Layers.AsEnumerable().Reverse().Where(layer =>
            layer.Visible && !layer.Locked && LayerGeometry.Corners(layer.Bounds, layer.RotationDegrees)
                .All(region.Contains))
            .Select(layer => layer.Id);
        SetSelection((additiveSelection ?? []).Concat(enclosed));
    }

    public void SelectAllEditableLayers()
    {
        EnsureNoGesture();
        SetSelection(Document.Layers.AsEnumerable().Reverse()
            .Where(layer => layer.Visible && !layer.Locked).Select(layer => layer.Id));
    }

    /// <summary>
    /// Select the editable layers that are not currently selected. Hidden and locked layers are
    /// always excluded; eligible IDs let the filtered layer list narrow the complement without
    /// changing the document or history.
    /// </summary>
    public void InvertEditableSelection(IEnumerable<Guid>? eligibleIds = null)
    {
        EnsureNoGesture();
        var eligible = eligibleIds?.ToHashSet();
        var current = _selectedLayerIds.ToHashSet();
        SetSelection(Document.Layers.AsEnumerable().Reverse()
            .Where(layer => layer.Visible && !layer.Locked &&
                (eligible is null || eligible.Contains(layer.Id)) &&
                !current.Contains(layer.Id))
            .Select(layer => layer.Id));
    }

    public void DeleteLayer(Guid id)
        => DeleteLayers([id]);

    public void DeleteLayers(IEnumerable<Guid> ids)
    {
        EnsureNoGesture();
        var chosen = ids.ToHashSet();
        if (chosen.Count == 0) return;
        foreach (var id in chosen) Document.Find(id);
        var before = Document.Copy();
        try
        {
            Document.Layers.RemoveAll(layer => chosen.Contains(layer.Id));
            Document.RemoveUnreferencedAssets();
            Document.Validate();
        }
        catch { Document = before; throw; }
        Record(before, "删除图层");
        NormalizeSelection();
    }

    public void DuplicateLayer(Guid id)
        => DuplicateLayers([id]);

    /// <summary>Append a native composition in its original logical units as one reversible edit.</summary>
    public IReadOnlyList<Guid> ImportProjectLayers(EditorDocument source)
    {
        EnsureNoGesture();
        ArgumentNullException.ThrowIfNull(source);
        source.Validate();
        if (source.Layers.Count == 0)
            throw new InvalidOperationException("The source project has no layers to import.");
        // Freeze the source even if a caller imports this session's own document.
        var incoming = source.Copy();
        var copies = incoming.Layers.Select(layer => layer.Copy()).ToArray();
        var groups = new Dictionary<Guid, Guid>();
        var assets = new Dictionary<Guid, Guid>();
        UpdateDocument(document =>
        {
            if (document.Layers.Count + copies.Length > 200)
                throw new InvalidDataException("Import would exceed the 200 layer limit.");
            foreach (var copy in copies)
            {
                copy.Id = Guid.NewGuid();
                if (copy.GroupId is Guid group)
                {
                    if (!groups.TryGetValue(group, out var replacement))
                        groups[group] = replacement = Guid.NewGuid();
                    copy.GroupId = replacement;
                }
                if (copy.AssetId is Guid sourceId)
                {
                    if (!assets.TryGetValue(sourceId, out var replacement))
                    {
                        var bytes = incoming.Assets[sourceId];
                        replacement = document.Assets.FirstOrDefault(pair => pair.Value.ContentEquals(bytes)).Key;
                        if (replacement == Guid.Empty)
                        {
                            replacement = Guid.NewGuid();
                            var retained = _undo.Concat(_redo).SelectMany(state => state.Document.Assets.Values)
                                .FirstOrDefault(asset => asset.ContentEquals(bytes)) ?? bytes;
                            document.AddAsset(replacement, retained);
                        }
                        assets[sourceId] = replacement;
                    }
                    copy.AssetId = replacement;
                }
            }
            document.Layers.AddRange(copies);
        }, "导入工程图层");
        var ids = copies.Select(layer => layer.Id).ToArray();
        SetSelection(ids);
        return ids;
    }

    public void DuplicateLayers(IEnumerable<Guid> ids)
    {
        EnsureNoGesture();
        var selection = ids.Distinct().ToArray();
        var chosen = selection.ToHashSet();
        if (chosen.Count == 0) return;
        foreach (var id in chosen) Document.Find(id);
        var before = Document.Copy();
        var originals = before.Layers.Where(layer => chosen.Contains(layer.Id)).ToArray();
        // Bound one translation for the whole selection so copying cannot tear its layout.
        var dx = Math.Min(24, 20000 - originals.Max(layer => layer.Bounds.X));
        var dy = Math.Min(24, 20000 - originals.Max(layer => layer.Bounds.Y));
        var groupIds = new Dictionary<Guid, Guid>();
        var copyIds = new Dictionary<Guid, Guid>();
        var copies = new List<Layer>();
        foreach (var original in originals)
        {
            var copy = original.Copy();
            copy.Id = Guid.NewGuid();
            copyIds[original.Id] = copy.Id;
            if (copy.GroupId is Guid group)
            {
                if (!groupIds.TryGetValue(group, out var replacement))
                    groupIds[group] = replacement = Guid.NewGuid();
                copy.GroupId = replacement;
            }
            copy.Bounds = copy.Bounds with
            {
                X = copy.Bounds.X + dx,
                Y = copy.Bounds.Y + dy
            };
            copies.Add(copy);
        }
        try { Document.Layers.AddRange(copies); Document.Validate(); }
        catch { Document = before; throw; }
        Record(before, "复制图层");
        SetSelection(selection.Select(id => copyIds[id]));
    }

    public void GroupLayers(IEnumerable<Guid> ids)
    {
        EnsureNoGesture();
        var selection = ids.Distinct().ToArray();
        var chosen = selection.ToHashSet();
        if (chosen.Count < 2) throw new InvalidOperationException("Select at least two layers to group.");
        foreach (var id in chosen) Document.Find(id);
        var before = Document.Copy();
        var group = Guid.NewGuid();
        try
        {
            foreach (var layer in Document.Layers.Where(layer => chosen.Contains(layer.Id))) layer.GroupId = group;
            Document.Validate();
        }
        catch { Document = before; throw; }
        Record(before, "编组图层");
        SetSelection(selection);
    }

    public void UngroupLayers(IEnumerable<Guid> ids)
    {
        EnsureNoGesture();
        var chosen = ids.ToHashSet();
        if (chosen.Count == 0) return;
        var groups = chosen.Select(id => Document.Find(id).GroupId).OfType<Guid>().ToHashSet();
        if (groups.Count == 0) return;
        var before = Document.Copy();
        try
        {
            foreach (var layer in Document.Layers.Where(layer => layer.GroupId is Guid group && groups.Contains(group)))
                layer.GroupId = null;
            Document.Validate();
        }
        catch { Document = before; throw; }
        Record(before, "取消编组");
    }

    public void SetSelection(IEnumerable<Guid> ids)
    {
        var selected = ids.Distinct().ToArray();
        foreach (var id in selected) Document.Find(id);
        _selectedLayerIds = selected;
        _selectedLayerId = selected.Length == 0 ? null : selected[0];
    }

    public void BeginGesture(Guid layerId, bool includeGroup = false, bool includeSelection = true,
        string? label = null)
    {
        EnsureNoGesture();
        var targets = GestureTargets(layerId, includeGroup, includeSelection);
        _gestureStart = Document.Copy();
        _gestureLayerId = layerId;
        _gestureTargetIds = targets.ToArray();
        _gestureLabel = label?.Trim() ?? "";
    }

    public PointD MoveBy(double deltaX, double deltaY)
    {
        GestureOrigin();
        if (_gestureLabel.Length == 0) _gestureLabel = "移动图层";
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new ArgumentOutOfRangeException(nameof(deltaX));
        var origins = _gestureTargetIds.Select(id => _gestureStart!.Find(id).Bounds).ToArray();
        deltaX = Math.Clamp(deltaX, origins.Max(box => -20000 - box.X), origins.Min(box => 20000 - box.X));
        deltaY = Math.Clamp(deltaY, origins.Max(box => -20000 - box.Y), origins.Min(box => 20000 - box.Y));
        for (var i = 0; i < _gestureTargetIds.Length; i++)
        {
            Document.Find(_gestureTargetIds[i]).Bounds = origins[i] with
            { X = origins[i].X + deltaX, Y = origins[i].Y + deltaY };
        }
        return new PointD(deltaX, deltaY);
    }

    public void ResizeBy(double deltaWidth, double deltaHeight)
        => ResizeFromHandle(deltaWidth, deltaHeight, ResizeHandle.BottomRight);

    /// <summary>Resize in the handle's local axes; the opposite corner/edge stays fixed.</summary>
    public void ResizeFromHandle(double deltaX, double deltaY, ResizeHandle handle, bool forceUniform = false)
    {
        GestureOrigin();
        if (_gestureLabel.Length == 0) _gestureLabel = "缩放图层";
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new ArgumentOutOfRangeException(nameof(deltaX));
        var start = _gestureStart!;
        var layers = _gestureTargetIds.Select(start.Find).ToArray();
        var single = layers.Length == 1;
        var origin = single ? layers[0].Bounds : ActiveGestureBounds;
        var angle = single ? layers[0].RotationDegrees : 0;
        var localDelta = LayerGeometry.Rotate(new(deltaX, deltaY), default, -angle);
        var (hx, hy) = LayerGeometry.HandleFraction(handle);
        var changesX = hx != 0.5; var changesY = hy != 0.5;
        var sx = changesX ? Math.Max(0.05, 1 + (hx == 0 ? -localDelta.X : localDelta.X) / origin.Width) : 1;
        var sy = changesY ? Math.Max(0.05, 1 + (hy == 0 ? -localDelta.Y : localDelta.Y) / origin.Height) : 1;
        var minX = layers.Max(layer => MinimumScale(layer.Bounds.Width, layer.Kind == LayerKind.Badge ? 8 : 1));
        var minY = layers.Max(layer => MinimumScale(layer.Bounds.Height, 1));
        var maxX = layers.Min(layer => MaximumScale(layer.Bounds.Width));
        var maxY = layers.Min(layer => MaximumScale(layer.Bounds.Height));
        var uniform = !single || forceUniform || layers[0].KeepAspect || layers[0].Kind == LayerKind.Badge;
        if (uniform)
        {
            var requested = changesX && changesY ? Math.Max(sx, sy) : changesX ? sx : sy;
            var scale = Math.Clamp(requested, Math.Max(minX, minY), Math.Min(maxX, maxY));
            sx = sy = scale;
        }
        else
        {
            sx = Math.Clamp(sx, minX, maxX);
            sy = Math.Clamp(sy, minY, maxY);
        }
        var anchor = new PointD(origin.X + origin.Width * (1 - hx), origin.Y + origin.Height * (1 - hy));
        var rotatedAnchor = LayerGeometry.Rotate(anchor, LayerGeometry.Centre(origin), angle);
        RectD Resized(Layer source, double scaleX, double scaleY)
        {
            var box = source.Bounds;
            if (scaleX == 1 && scaleY == 1) return box;
            var centre = LayerGeometry.Centre(box);
            centre = new(anchor.X + (centre.X - anchor.X) * scaleX,
                anchor.Y + (centre.Y - anchor.Y) * scaleY);
            if (single)
            {
                var offset = LayerGeometry.Rotate(new(centre.X - anchor.X, centre.Y - anchor.Y), default, angle);
                centre = new(rotatedAnchor.X + offset.X, rotatedAnchor.Y + offset.Y);
            }
            return new(centre.X - box.Width * scaleX / 2, centre.Y - box.Height * scaleY / 2,
                box.Width * scaleX, box.Height * scaleY);
        }
        // Apply one common limit instead of clipping member positions independently and
        // tearing apart the selection or moving a rotated layer's fixed anchor.
        var fraction = 1.0;
        foreach (var source in layers)
        {
            var requested = Resized(source, sx, sy);
            Limit(source.Bounds.X, requested.X); Limit(source.Bounds.Y, requested.Y);
        }
        if (fraction < 1)
        {
            if (uniform)
                sx = sy = Math.Clamp(1 + (sx - 1) * fraction, Math.Max(minX, minY), Math.Min(maxX, maxY));
            else
            {
                sx = Math.Clamp(1 + (sx - 1) * fraction, minX, maxX);
                sy = Math.Clamp(1 + (sy - 1) * fraction, minY, maxY);
            }
        }
        foreach (var source in layers)
        {
            var target = Document.Find(source.Id);
            target.Bounds = Resized(source, sx, sy);
            if (source.Kind == LayerKind.Text)
                target.FontSize = Math.Clamp(source.FontSize * Math.Min(sx, sy), 1, 1000);
        }
        void Limit(double original, double requested)
        {
            if (requested > 20000) fraction = Math.Min(fraction, (20000 - original) / (requested - original));
            if (requested < -20000) fraction = Math.Min(fraction, (-20000 - original) / (requested - original));
        }
        // A rounded quotient can multiply back outside a strict size boundary. Move the
        // scale bound inward so every member retains the same legal scale and anchor.
        static double MinimumScale(double size, double minimum)
        {
            var scale = minimum / size;
            return size * scale < minimum ? Math.BitIncrement(scale) : scale;
        }
        static double MaximumScale(double size)
        {
            var scale = 20000 / size;
            return size * scale > 20000 ? Math.BitDecrement(scale) : scale;
        }
    }

    /// <summary>Rotate the frozen editable targets around their shared visual centre.</summary>
    public void RotateBy(double deltaDegrees)
    {
        GestureOrigin();
        if (_gestureLabel.Length == 0) _gestureLabel = "旋转图层";
        if (!double.IsFinite(deltaDegrees)) throw new ArgumentOutOfRangeException(nameof(deltaDegrees));
        deltaDegrees = LayerGeometry.NormalizeDegrees(deltaDegrees);
        var pivot = LayerGeometry.Centre(ActiveGestureBounds);
        var sources = _gestureTargetIds.Select(id => _gestureStart!.Find(id)).ToArray();
        var rotated = sources.Select(source =>
        {
            // Keep the saved rectangle exact when rotating about its own centre, or
            // returning a selection to its original pose after a preview.
            if (sources.Length == 1 || deltaDegrees == 0) return source.Bounds;
            var centre = LayerGeometry.Rotate(LayerGeometry.Centre(source.Bounds), pivot, deltaDegrees);
            return source.Bounds with { X = centre.X - source.Bounds.Width / 2,
                Y = centre.Y - source.Bounds.Height / 2 };
        }).ToArray();
        // A rejected preview leaves the previous valid whole-group pose intact.
        if (rotated.Any(box => Math.Abs(box.X) > 20000 || Math.Abs(box.Y) > 20000)) return;
        for (var i = 0; i < sources.Length; i++)
        {
            var target = Document.Find(sources[i].Id);
            target.Bounds = rotated[i];
            target.RotationDegrees = LayerGeometry.NormalizeDegrees(sources[i].RotationDegrees + deltaDegrees);
        }
    }

    /// <returns>Whether the gesture changed the model and recorded an edit.</returns>
    public bool CommitGesture()
    {
        if (_gestureStart is null) throw new InvalidOperationException("No active gesture.");
        var before = _gestureStart;
        try { Document.Validate(); }
        catch
        {
            Document = before;
            _gestureStart = null;
            _gestureTargetIds = [];
            _gestureLabel = "";
            throw;
        }
        _gestureStart = null;
        var changed = _gestureTargetIds.Any(id => !before.Find(id).StateEquals(Document.Find(id)));
        if (changed) Record(before, _gestureLabel.Length == 0 ? "编辑图层" : _gestureLabel);
        _gestureTargetIds = [];
        _gestureLabel = "";
        return changed;
    }

    public void CancelGesture()
    {
        if (_gestureStart is null) return;
        Document = _gestureStart;
        _gestureStart = null;
        _gestureTargetIds = [];
        _gestureLabel = "";
    }

    public bool Undo()
    {
        EnsureNoGesture();
        if (_undo.Count == 0) return false;
        var state = _undo.Pop();
        _redo.Push(new HistoryState(Document.Copy(), _revision, state.Label));
        Document = state.Document;
        _revision = state.Revision;
        NormalizeSelection();
        return true;
    }

    public bool Redo()
    {
        EnsureNoGesture();
        if (_redo.Count == 0) return false;
        var state = _redo.Pop();
        _undo.Push(new HistoryState(Document.Copy(), _revision, state.Label));
        Document = state.Document;
        _revision = state.Revision;
        NormalizeSelection();
        return true;
    }

    public void ReplaceDocument(EditorDocument document)
        => ReplaceDocument(document, saved: true);

    /// <summary>
    /// Replaces the edited document. <paramref name="saved"/> false marks it as an unsaved
    /// conversion (a legacy import the user has not yet written as a native project).
    /// </summary>
    public void ReplaceDocument(EditorDocument document, bool saved)
    {
        document.Validate();
        Document = document.Copy();
        _undo.Clear();
        _redo.Clear();
        _gestureStart = null;
        _gestureTargetIds = [];
        _gestureLabel = "";
        SelectedLayerId = null;
        _revision = ++_nextRevision;
        _savedRevision = saved ? _revision : -1;
        DocumentReplaced?.Invoke();
    }

    private RectD GestureOrigin() => _gestureStart?.Find(_gestureLayerId).Bounds
        ?? throw new InvalidOperationException("No active gesture.");

    private void ValidateAssetChange(EditorDocument before) =>
        AssetResourceBudget.ValidateChange(before.Assets.Values, Document.Assets.Values, MaxDocumentAssetBytes);

    private void Record(EditorDocument before, string label)
    {
        label = string.IsNullOrWhiteSpace(label) ? "编辑文档" : label.Trim();
        _undo.Push(new HistoryState(before, _revision, label));
        if (_undo.Count > _maxHistoryEntries)
        {
            var kept = _undo.Take(_maxHistoryEntries).Reverse().ToArray();
            _undo.Clear();
            foreach (var state in kept) _undo.Push(state);
        }
        _revision = ++_nextRevision;
        _redo.Clear();
    }

    private void RecordIfChanged(EditorDocument before, string label)
    {
        if (!before.StateEquals(Document)) Record(before, label);
    }

    private void EnsureNoGesture()
    {
        if (_gestureStart is not null) throw new InvalidOperationException("Finish the current gesture first.");
    }

    private void NormalizeSelection()
    {
        _selectedLayerIds = _selectedLayerIds.Where(id => Document.Layers.Any(layer => layer.Id == id)).ToArray();
        _selectedLayerId = _selectedLayerIds.Length == 0 ? null : _selectedLayerIds[0];
    }

}
