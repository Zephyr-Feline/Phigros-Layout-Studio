using System.Text.RegularExpressions;
using Editor.Core;

namespace Editor.Desktop;

/// <summary>Presentation-only Chinese labels. Model values and file formats remain unchanged.</summary>
public static class UiText
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["New"] = "新建工程", ["Import Image"] = "导入图片", ["Build 3 / 5 songs"] = "生成三曲 / 五曲布局",
        ["Import multiple images"] = "批量导入图片",
        ["Build N-song grid"] = "生成 N 曲网格",
        ["Song info workbench"] = "歌曲信息工作台",
        ["Open song style library"] = "歌曲样式库…",
        ["Add Text"] = "添加文字", ["Apply Text"] = "应用文字", ["Add Shape"] = "添加形状",
        ["Add Badge"] = "添加难度徽标", ["Build song from image"] = "由图片创建歌曲组",
        ["Add Original label"] = "添加 Original 标签", ["Undo"] = "撤销", ["Redo"] = "重做",
        ["Copy selected layers"] = "复制选中图层", ["Paste layers"] = "粘贴图层",
        ["Clear selection"] = "取消选择", ["Invert selection"] = "反向选择可见且未锁定的图层",
        ["Preferences"] = "首选项",
        ["Save Temporary JSON"] = "保存临时 JSON", ["Save Project"] = "保存工程", ["Save Project As"] = "工程另存为",
        ["Open Project"] = "打开工程", ["Export PNG"] = "导出 PNG", ["Export Transparent PNG"] = "导出透明 PNG",
        ["Batch export PNG"] = "批量导出 PNG",
        ["Batch export across projects"] = "跨工程批量导出",
        ["Batch export selected layers"] = "批量导出选中内容",
        ["Fit view"] = "适合窗口", ["Zoom in"] = "放大", ["Zoom out"] = "缩小",
        ["Actual size (100%)"] = "实际大小（100%）", ["Fit selection"] = "适合所选图层",
        ["Rename layer…"] = "重命名图层…",
        ["Import project layers"] = "导入工程图层", ["Save selection as project"] = "将选中图层保存为工程",
        ["Show rulers"] = "显示标尺", ["Hide rulers"] = "隐藏标尺",
        ["Add vertical guide at center"] = "在画布中心添加垂直辅助线",
        ["Add horizontal guide at center"] = "在画布中心添加水平辅助线",
        ["Add safe-area guides"] = "按安全区添加辅助线",
        ["Clear guides"] = "清除全部辅助线",
        ["Guide X (0–canvas width)"] = "辅助线 X（0–画布宽度）",
        ["Guide Y (0–canvas height)"] = "辅助线 Y（0–画布高度）",
        ["Add vertical guide"] = "添加垂直辅助线",
        ["Add horizontal guide"] = "添加水平辅助线",
        ["Guide positions must be finite numbers (use '.' for decimals)."] =
            "辅助线坐标必须是有效数字，小数请使用英文句点。",
        ["Export blocked by the quality policy because one or more tasks upscale source artwork."] =
            "已按质量策略阻止导出：一个或多个任务会放大源图。",
        ["Show / hide"] = "显示 / 隐藏", ["Hide group"] = "隐藏歌曲组", ["Show group"] = "显示歌曲组",
        ["Lock / unlock"] = "锁定 / 解锁", ["Move forward"] = "上移一层", ["Move backward"] = "下移一层",
        ["Bring to front"] = "置于顶层", ["Send to back"] = "置于底层", ["Duplicate"] = "复制图层",
        ["Replace image"] = "替换图片", ["Delete"] = "删除", ["Group selected"] = "编组选中图层", ["Ungroup"] = "取消编组",
        ["Edit whole group"] = "整组编辑", ["Apply arrangement"] = "应用排列", ["Apply canvas settings"] = "应用画布设置",
        ["Resize canvas and fit content"] = "调整画布并适配内容", ["Apply blend settings"] = "应用背景混合设置",
        ["Set background image"] = "设置背景图片", ["Generate blended snapshot"] = "生成混合背景快照",
        ["Clear background image"] = "清除背景图片", ["Apply layout"] = "应用布局", ["Capture selected layout"] = "保存选中内容的布局",
        ["Open layout template"] = "打开布局模板", ["Save layout template"] = "保存布局模板",
        ["Import local font (TTF / OTF)"] = "导入本地字体（TTF / OTF）", ["Use selected local font"] = "使用选中的本地字体",
        ["Apply properties"] = "应用属性", ["Name"] = "名称", ["X"] = "水平位置 X", ["Y"] = "垂直位置 Y",
        ["Width"] = "宽度", ["Height"] = "高度", ["Color (#AARRGGBB)"] = "颜色（AARRGGBB）",
        ["Font family"] = "字体", ["Available local fonts"] = "可用本地字体", ["Font size"] = "字号",
        ["Line spacing"] = "行距", ["Horizontal alignment"] = "水平对齐", ["Vertical alignment"] = "垂直对齐",
        ["Difficulty"] = "难度类型", ["Positive level"] = "难度等级（正整数）", ["Canvas width"] = "画布宽度",
        ["Canvas height"] = "画布高度", ["Background color (#AARRGGBB)"] = "画布底色（AARRGGBB）",
        ["Blend blur (0–20)"] = "背景模糊（0–20）", ["Blend darkness (0–100%)"] = "背景压暗（0–100%）",
        ["Blend range (0.25–2.0)"] = "背景扩散范围（0.25–2.0）",
        ["Blend transition (0–1)"] = "背景过渡柔和度（0–1）",
        ["Background contribution"] = "背景贡献…",
        ["Resource and quality check"] = "资源与质量检查…",
        ["Cancel"] = "取消", ["Save"] = "保存", ["Discard changes"] = "放弃修改", ["Export anyway"] = "继续导出",
        ["Three columns (3)"] = "三列布局（3 曲）", ["Five point (5)"] = "五点布局（5 曲）",
        ["Choose exactly 3 or 5 artwork images."] = "请选择恰好 3 张或 5 张曲绘。当前仅支持三曲和五曲布局。",
        ["Finish the current gesture first."] = "请先完成当前拖动或缩放操作。",
        ["Finish the current file operation first."] = "请先完成当前文件操作。",
        ["Finish the current file operation before closing."] = "请先完成当前文件操作，再关闭窗口。",
        ["Select a layer first."] = "请先选中一个图层。", ["Select a text layer first."] = "请先选中文字图层。",
        ["Select a text layer and a local font."] = "请先选中文字图层和本地字体。",
        ["Locked layers cannot be moved or resized."] = "图层已锁定，请先解锁再移动或缩放。",
        ["Locked layers cannot be edited."] = "图层已锁定，请先解锁再编辑。",
        ["Select an image layer to replace."] = "请先选中要替换的图片图层。",
        ["Select an unlocked image layer to build a song card."] = "请先选中未锁定的图片图层，再创建歌曲组。",
        ["Select an image layer or add a standalone label."] = "请选择图片图层，或添加独立标签。",
        ["Select at least two layers to group."] = "请至少选中两个图层再编组。",
        ["Select layers or a song group to save."] = "请先选中要保存的图层或歌曲组。",
        ["Choose a layout template first."] = "请先选择布局模板。",
        ["Choose or capture a layout template first."] = "请先选择模板或保存选中内容的布局。",
        ["Unlock every affected layer/group member before arranging."] = "请先解锁参与排列的全部图层和组成员。",
        ["Unlock every member of the selected groups before arranging them."] = "请先解锁所选组的全部成员。",
        ["Not enough room for non-overlapping equal gaps. Choose Canvas or spread the selection farther apart."] = "空间不足，无法等间距排列。请选择画布作为参照，或扩大选中内容的范围。",
        ["Artwork is too small or too large for a song card."] = "曲绘尺寸过小或过大，无法创建歌曲组。",
        ["Canvas dimensions are outside the P1 range."] = "画布尺寸超出范围：宽度须为 320–7680，高度须为 180–4320。",
        ["Canvas width and height must be positive integers."] = "画布宽度和高度必须是正整数。",
        ["Difficulty level must be a positive integer."] = "难度等级必须是正整数。",
        ["Blend blur must be an integer from 0 to 20."] = "背景模糊必须是 0 到 20 的整数。",
        ["Background blur must be 0–20 and darkness must be 0–1."] = "背景模糊须为 0–20，压暗比例须为 0–100%。",
        ["Background blend settings are outside their allowed range."] =
            "背景设置超出范围：扩散范围须为 0.25–2.0，过渡柔和度须为 0–1。",
        ["Color must have eight hex digits: AARRGGBB."] = "颜色必须为 8 位十六进制值：AARRGGBB。",
        ["Document contains an invalid layer type or null field."] = "图层属性无效。请检查字号（1–1000）、行距（0.5–4）和难度等级。",
        ["The song batch would exceed the 200 layer limit."] = "添加这些歌曲后将超过 200 个图层的上限。",
        ["Import would exceed the 200 layer limit."] = "导入后将超过 200 个图层的上限。", ["Too many layers or assets."] = "图层或资源数量超过 200 个的上限。",
        ["The source project has no layers to import."] = "所选工程没有可导入的图层。",
        ["Choose another project; use Duplicate for layers in the current project."] = "请选择其他工程。复制当前工程的图层请使用“复制图层”。",
        ["Choose another path; the selection must not replace the current project."] = "请选择其他保存位置，选中内容不能覆盖当前工程。",
        ["Choose another output path; this file is the current project."] = "请选择其他输出位置，不能覆盖当前工程。",
        ["Choose another output path; this file is an imported source."] = "请选择其他输出位置，不能覆盖已导入的源文件。",
        ["A temporary JSON file cannot replace the current native project."] = "临时 JSON 不能覆盖当前原生工程。",
        ["A layout template cannot replace the current project."] = "布局模板不能覆盖当前工程。",
        ["The selected file/output path now points to another location. Restore the original path or choose the file again."] = "所选文件或输出路径已指向其他位置。请恢复原路径或重新选择文件。",
        ["Layout template changed while choosing its save path. Please save again."] = "选择保存位置期间布局模板已发生变化，请重新保存。",
        ["Project changed while choosing a project to open. Please open it again."] = "选择工程期间当前工程已发生变化，请重新打开。",
        ["Project or text selection changed while choosing a font. Please import again."] = "选择字体期间工程或文字选择已发生变化，请重新导入。",
        ["Project changed while opening a layout template. Please open it again."] = "打开模板期间工程已发生变化，请重新打开。",
        ["Project changed while saving a layout template. Please save again."] = "保存模板期间工程已发生变化，请重新保存。",
        ["Project changed while reading artwork. Please build the songs again."] = "读取曲绘期间工程已发生变化，请重新生成歌曲。",
        ["Project changed while reading images. Please import again."] = "读取图片期间工程已发生变化，请重新导入。",
        ["Project changed while reading grid artwork. Please build the grid again."] =
            "读取网格曲绘期间工程已发生变化，请重新生成。",
        ["Choose at least one project and one export task."] = "请至少选择一份工程和一个导出任务。",
        ["Project file not found."] = "找不到工程文件。",
        ["The song grid would exceed the 200 layer limit."] = "生成歌曲网格后将超过 200 个图层上限。",
        ["Project changed while configuring the song grid. Please try again."] =
            "配置歌曲网格期间工程已发生变化，请重新生成。",
        ["Project changed while editing song info. Please reopen the workbench."] =
            "编辑歌曲信息期间工程或选择已发生变化，请重新打开工作台。",
        ["Batch styles support text layers or difficulty badges only."] = "批量样式仅支持文字图层或难度徽标。",
        ["Select layers of one type for batch styling."] = "请选择同一类型的图层进行批量样式。",
        ["Unlock every target layer before applying batch styles."] = "请先解锁全部目标图层，再应用批量样式。",
        ["Choose at least one style field to apply."] = "请至少勾选一个要批量应用的字段。",
        ["Select at least one layer for a batch style."] = "请至少选择一个图层进行批量样式。",
        ["Font family must be a nonempty name of at most 200 characters."] = "字体名称不能为空，且不能超过 200 个字符。",
        ["Font size must be between 1 and 1000."] = "字号必须在 1 到 1000 之间。",
        ["Line spacing must be between 0.5 and 4."] = "行距必须在 0.5 到 4 之间。",
        ["Difficulty must be EZ, HD, IN or AT."] = "难度类型必须是 EZ、HD、IN 或 AT。",
        ["Horizontal alignment is invalid."] = "水平对齐设置无效。",
        ["Vertical alignment is invalid."] = "垂直对齐设置无效。",
        ["A controlled template ID is required."] = "模板库条目 ID 无效。",
        ["Template path escaped the library directory."] = "模板路径超出本地模板库目录，已拒绝。",
        ["Template name is required."] = "模板名称不能为空。",
        ["The template library limit of 200 entries has been reached."] = "本地模板库已达到 200 项上限。",
        ["The destination already exists; new-file output never overwrites it."] = "目标文件已存在；新文件导出不会覆盖它。",
        ["The export queue directory must be new."] = "批量导出目录必须是不存在的新目录，请重新选择。",
        ["Choose an existing parent folder for the export queue."] = "请选择一个已存在的父目录用于批量导出。",
        ["Could not allocate a new export directory."] = "无法创建新的批量导出子目录，请更换父目录。",
        ["Choose another output folder; it must not replace the current project."] = "请选择其他输出目录，不能覆盖当前工程。",
        ["The output path became the current project."] = "输出路径在导出期间变成了当前工程，已停止该任务。",
        ["Add at least one PNG export task."] = "请至少添加一个 PNG 导出任务。",
        ["Project changed during import. Please import again."] = "导入期间工程已发生变化，请重新导入。",
        ["Project or selection changed during save. Please save the selection again."] = "保存期间工程或选择已发生变化，请重新保存选中内容。",
        ["Image file changed while it was being read."] = "读取期间图片文件已发生变化，请重新导入。",
        ["The image loader returned invalid image dimensions or bytes."] = "图片尺寸或数据无效，请选择有效的图片文件。",
        ["PNG output exceeds the 80 million pixel limit."] = "PNG 输出超过 8000 万像素的上限，请减小导出尺寸。",
        ["Hidden; omitted from PNG."] = "已隐藏，不会出现在 PNG 中。",
        ["Entirely outside the canvas; foreground omitted from PNG."] = "完全位于画布之外，不会出现在 PNG 中。",
        ["Partly outside the canvas; PNG clips it at the edge."] = "部分超出画布，PNG 会裁切边缘。",
        ["Local font unavailable; PNG uses the preview fallback font."] = "本地字体不可用，PNG 将使用预览中的替代字体。",
        ["Native package was created by a newer schema version."] = "工程由较新版本创建，请使用新版编辑器打开。",
        ["A local path is required."] = "请选择本地文件路径。",
        ["PNG renderer produced no output."] = "PNG 渲染未生成内容。",
        ["PNG export region must be a positive rectangle inside the canvas."] =
            "PNG 裁切范围必须是画布内的正矩形。",
        ["The selected content is entirely outside the canvas and cannot be cropped."] =
            "选中内容完全位于画布之外，无法使用裁切模式。",
        ["Select at least one layer to calculate a crop region."] =
            "请至少选择一个图层以计算裁切范围。",
        ["Missing fixed background asset."] = "工程缺少固定背景图片。",
        ["Document layers and assets are required."] = "工程缺少图层或资源数据。", ["Ready."] = "就绪。", ["Project loaded."] = "工程已打开。",
        ["Temporary JSON loaded as an unsaved native document."] = "临时 JSON 已打开，请保存为原生工程。",
        ["Recovered unsaved project."] = "已恢复为未保存工程。",
        ["Auto recovery contains applied document content only; side-panel drafts, undo history and view state are not included."] =
            "自动恢复只包含已应用的工程内容，不包含侧栏草稿、撤销历史或视图状态。",
        ["Project or image selection changed while reading an image. Please import again."] = "读取图片期间工程或图片选择已发生变化，请重新导入。",
        ["Project changed while confirming unsaved changes. Please try again."] = "确认未保存修改期间工程已发生变化，请重试。",
        ["Project changed while saving unsaved changes. Please try again."] = "保存未保存修改期间工程已发生变化，请重试。",
        ["Project changed while choosing a save path. Please save again."] = "选择保存位置期间工程已发生变化，请重新保存。",
        ["Project changed while choosing a temporary save path. Please save again."] = "选择临时保存位置期间工程已发生变化，请重新保存。",
        ["Encoded image exceeds the 100 MiB asset limit."] = "图片文件超过 100 MiB 的上限。",
        ["Image header is truncated."] = "图片文件不完整或无效，请选择有效的图片。",
        ["Unsupported image format; expected PNG, JPEG, BMP or TIFF."] = "图片格式不受支持，请使用 PNG、JPEG、BMP 或 TIFF。",
        ["Decoded image dimensions disagree with the header."] = "图片实际尺寸与文件头不一致，文件可能已损坏。",
        ["Blend darkness"] = "背景压暗", ["PNG width must be between 1 and 16384 pixels."] = "PNG 宽度须为 1–16384 像素。",
        ["PNG height must be between 1 and 16384 pixels; choose another width."] = "PNG 高度须为 1–16384 像素，请调整导出宽度。",
        ["The file is not a recognised project document."] = "文件不是可识别的工程，请选择有效的 .ppgproj 工程文件。"
    };

    public static string Translate(string text)
    {
        if (Labels.TryGetValue(text, out var label)) return label;
        var finite = Regex.Match(text, @"^(.*) must be a finite number \(use '\.' for decimals\)\.$");
        if (finite.Success) return $"{Translate(finite.Groups[1].Value)}必须是有效数字，小数请使用英文句点。";
        if (text.StartsWith("Invalid bounds for layer ", StringComparison.Ordinal))
            return "图层位置或尺寸无效。宽高须为 1–20000，位置不能超出 ±20000。";
        if (text.StartsWith("Invalid badge ", StringComparison.Ordinal))
            return "难度徽标属性无效。请保持 128:108 比例、有效难度类型和正整数等级。";
        if (text.StartsWith("Invalid text layer ", StringComparison.Ordinal))
            return "文字属性无效。请检查字体、字号（1–1000）和行距（0.5–4）。";
        if (text.StartsWith("Missing image asset for layer ", StringComparison.Ordinal)) return "工程缺少图层图片资源。";
        var arrangement = Regex.Match(text, @"^Select at least (\d+) independent layers or groups for this arrangement\.$");
        if (arrangement.Success) return $"此排列至少需要 {arrangement.Groups[1].Value} 个独立图层或歌曲组。";
        if (text.StartsWith("Image ", StringComparison.Ordinal) && text.Contains(": ", StringComparison.Ordinal))
        {
            var split = text.IndexOf(": ", StringComparison.Ordinal);
            return $"图片 {text[6..split]}：{Translate(text[(split + 2)..])}";
        }
        if (text.StartsWith("Image dimensions ", StringComparison.Ordinal)) return "图片尺寸超过限制：单边不超过 32768 像素，总像素不超过 8000 万。";
        if (text.StartsWith("Duplicate output task: ", StringComparison.Ordinal))
            return "存在重复的导出任务：" + text["Duplicate output task: ".Length..] +
                "。请先点击“去除重复任务”。";
        if (text.StartsWith("Output already exists: ", StringComparison.Ordinal))
            return "输出文件已存在：" + text["Output already exists: ".Length..] + "。未覆盖原文件。";
        if (text.StartsWith("Invalid layout template: ", StringComparison.Ordinal))
            return "布局模板无效：" + text["Invalid layout template: ".Length..];
        if (text == "Unsupported layout template version; this editor reads version 1.")
            return "布局模板版本不受支持；本编辑器读取版本 1。";
        if (text == "A layout template needs between 1 and 200 slots.")
            return "布局模板需要 1 到 200 个槽位。";
        if (text.StartsWith("Template ID/name is missing or too long", StringComparison.Ordinal))
            return "模板 ID 或名称缺失/过长（ID 最多 80、名称最多 120 个字符）。";
        if (text.StartsWith("Template slots must have positive size", StringComparison.Ordinal))
            return "模板槽位必须为正值并位于归一化画布内。";
        if (text.StartsWith("Imported source document ", StringComparison.Ordinal))
            return "文件已导入。保存工程将创建新的 .ppgproj 文件，原文件受保护。转换详情见侧栏。";
        var upscale = Regex.Match(text,
            @"^Artwork is upscaled at this output size; source (\d+)×(\d+) px, displayed (\d+)×(\d+) px\.$");
        if (upscale.Success)
            return $"源图分辨率不足：采样 {upscale.Groups[1].Value}×{upscale.Groups[2].Value}，" +
                $"导出显示 {upscale.Groups[3].Value}×{upscale.Groups[4].Value}，可能变模糊。";
        if (text.Contains("cannot be decoded", StringComparison.OrdinalIgnoreCase)) return "图片无法解码，请确认文件未损坏且格式受支持。";
        if (text.Contains("must be nonempty and no larger than 100 MiB", StringComparison.Ordinal))
            return "图片文件不能为空，且不能超过 100 MiB。";
        return text;
    }

    public static string LayerKindName(LayerKind kind) => kind switch
    { LayerKind.Image => "图片", LayerKind.Text => "文字", LayerKind.Shape => "形状", LayerKind.Badge => "难度徽标", _ => kind.ToString() };

    public static string LayerName(string name) => name switch
    {
        "Artwork" => "曲绘", "Title" => "曲名", "Artist" => "曲师", "Charter" => "谱师",
        "Badge" => "难度徽标", "Shape" => "形状", "Text" => "文字", "Original plate" => "Original 底板",
        "Fixed background" => "固定背景",
        "Original caption" => "Original 文字", _ => name
    };
}
