using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Editor.Core;
using Editor.Formats;
using Editor.Formats.Compatibility;
using Editor.Rendering;

namespace Editor.Desktop;

public sealed record PixelDifference(bool SameDimensions, int ReferenceWidth, int ReferenceHeight,
    int CandidateWidth, int CandidateHeight, long? ChangedPixels = null, double? ChangedPercent = null,
    double? MeanChannelDelta = null, int? MaxChannelDelta = null, RectD? ChangedBounds = null);

/// <summary>Acceptance evidence only. Pixel percentages never approve a migration.</summary>
public static class CompatibilityEvidence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static int Run(string directory, string legacyExecutable, string? corpus = null)
    {
        legacyExecutable = Path.GetFullPath(legacyExecutable);
        if (!File.Exists(legacyExecutable)) throw new FileNotFoundException("Independent V1 reference executable is missing.", legacyExecutable);
        return Run(directory, (source, target) => RenderLegacy(legacyExecutable, source, target), corpus);
    }

    public static int Run(string directory, Func<string, string, int> renderLegacy, string? corpus = null)
    {
        ArgumentNullException.ThrowIfNull(renderLegacy);
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Compatibility evidence output must be new or empty.");
        string[] corpusPaths = [];
        if (corpus is not null)
        {
            corpus = Path.GetFullPath(corpus);
            if (!Directory.Exists(corpus)) throw new DirectoryNotFoundException("Requested corpus is missing.");
            if (Contains(corpus, directory) || Contains(directory, corpus))
                throw new IOException("Corpus and evidence output must be separate directories.");
            corpusPaths = Directory.EnumerateFiles(corpus, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".phipreview" or ".phifree")
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (corpusPaths.Length == 0) throw new InvalidDataException("Requested corpus contains no legacy projects.");
        }
        Directory.CreateDirectory(directory);
        var samples = new List<EvidenceSample>();
        foreach (var fixture in CompatibilityEvidenceFixtures.Create())
        {
            var folder = Path.Combine(directory, fixture.Id);
            Directory.CreateDirectory(folder);
            var source = Path.Combine(folder, "source" + fixture.Extension);
            fixture.Xml.Save(source);
            samples.Add(Collect(fixture.Id, fixture.Title, source, folder, fixture.ExpectedError, false, renderLegacy));
        }
        if (corpus is not null)
        {
            for (var i = 0; i < corpusPaths.Length; i++)
            {
                var id = $"local-{i + 1:000}";
                var folder = Path.Combine(directory, id);
                Directory.CreateDirectory(folder);
                samples.Add(Collect(id, Path.GetFileName(corpusPaths[i]), corpusPaths[i], folder, null, true, renderLegacy));
            }
        }
        var failed = samples.Count(sample => sample.TechnicalStatus == "FAILED");
        var summary = new
        {
            ReportVersion = 1, Status = failed == 0 ? "PASS" : "FAILED", VisualAcceptance = "PENDING",
            LegacyWriterDecision = "PENDING", CreatedUtc = DateTime.UtcNow,
            Environment = new { OperatingSystem = RuntimeInformation.OSDescription,
                Framework = RuntimeInformation.FrameworkDescription, Font = "Microsoft YaHei UI",
                LegacyFontFolder = "No repository FONT files copied; system font/fallback only",
                WindowsFontEvidence = WindowsFontEvidence() },
            Synthetic = samples.Count(sample => !sample.Local), Local = samples.Count(sample => sample.Local),
            Accepted = samples.Count(sample => sample.Report is not null),
            Rejected = samples.Count(sample => sample.TechnicalStatus is "EXPECTED_REJECTION" or "OBSERVED_REJECTION"),
            TechnicalFailures = failed, Samples = samples
        };
        Write(Path.Combine(directory, "compatibility-evidence.json"), JsonSerializer.Serialize(summary, JsonOptions));
        Write(Path.Combine(directory, "index.html"), Html(samples, failed));
        Console.WriteLine($"COMPATIBILITY EVIDENCE {(failed == 0 ? "PASS" : "FAILED")}: " +
            $"{samples.Count} samples, {failed} technical failures; visual acceptance PENDING. {directory}");
        return failed == 0 ? 0 : 1;
    }

    private static EvidenceSample Collect(string id, string title, string source, string folder,
        CompatibilityErrorKind? expectedError, bool local, Func<string, string, int> renderLegacy)
    {
        var sample = new EvidenceSample { Id = id, Title = title, Source = source, Local = local,
            ReportPath = Path.Combine(folder, "report.json") };
        var before = HashFile(source);
        var timestamp = File.GetLastWriteTimeUtc(source);
        sample.SourceSha256 = before;
        sample.SourceLastWriteUtc = timestamp;
        try
        {
            var import = CompatibilityService.Import(source, NativeImaging.Compatibility);
            NativeImaging.ValidateImages(import.Document);
            if (expectedError is not null) throw new InvalidDataException("Expected a rejection but import succeeded.");
            sample.Report = import.Report;
            sample.SourceFields = SourceFields(source);
            sample.ConvertedLayers = import.Document.Layers.Select(layer => layer.Copy()).ToArray();
            var native = Path.Combine(folder, "migrated.ppgproj");
            NativePackageStore.Save(native, import.Document, "p3-acceptance-evidence");
            var nativeHash = HashFile(native);
            var nativeTime = File.GetLastWriteTimeUtc(native);
            var reopened = NativePackageStore.Read(native);
            NativeImaging.ValidateImages(reopened);
            if (!import.Document.StateEquals(reopened)) throw new InvalidDataException("Native editable state changed on reopen.");
            var candidate = PngExporter.Render(import.Document, decoder: NativeImaging.Decoder);
            if (!candidate.SequenceEqual(PngExporter.Render(reopened, decoder: NativeImaging.Decoder)))
                throw new InvalidDataException("Native opaque pixels changed on reopen.");
            var transparent = PngExporter.Render(import.Document, true, NativeImaging.Decoder);
            if (!transparent.SequenceEqual(PngExporter.Render(reopened, true, NativeImaging.Decoder)))
                throw new InvalidDataException("Native transparent pixels changed on reopen.");
            if (nativeHash != HashFile(native) || nativeTime != File.GetLastWriteTimeUtc(native))
                throw new InvalidDataException("Native reopen changed its input file.");
            var candidatePath = Path.Combine(folder, "v2.png");
            NativeDocumentStore.WriteBytesAtomically(candidatePath, candidate);
            NativeDocumentStore.WriteBytesAtomically(Path.Combine(folder, "v2-transparent.png"), transparent);
            sample.NativeSha256 = nativeHash;
            sample.CandidatePngSha256 = HashFile(candidatePath);
            sample.RoundTrip = "PASS (editable state, opaque/transparent PNG, source hash/time)";
            var reference = Path.Combine(folder, "v1.png");
            sample.LegacyExitCode = renderLegacy(source, reference);
            if (sample.LegacyExitCode != 0 || !File.Exists(reference))
                throw new InvalidDataException($"V1 reference render failed (exit {sample.LegacyExitCode}).");
            sample.ReferencePngSha256 = HashFile(reference);
            sample.Pixels = Measure(Decode(reference), Decode(candidatePath), out var heatmap);
            if (heatmap is not null)
                NativeDocumentStore.WriteBytesAtomically(Path.Combine(folder, "difference.png"),
                    ProbeImages.EncodePng(heatmap.Width, heatmap.Height, heatmap.Bgra));
            sample.TechnicalStatus = "PASS";
        }
        catch (CompatibilityException error)
        {
            sample.ErrorKind = error.Kind.ToString();
            sample.Error = error.Message;
            sample.TechnicalStatus = expectedError == error.Kind ? "EXPECTED_REJECTION" :
                local ? "OBSERVED_REJECTION" : "FAILED";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            sample.TechnicalStatus = "FAILED";
            sample.Error = error.Message;
        }
        finally
        {
            sample.SourceUnchanged = File.Exists(source) && HashFile(source) == before &&
                File.GetLastWriteTimeUtc(source) == timestamp;
            if (!sample.SourceUnchanged)
            {
                sample.TechnicalStatus = "FAILED";
                sample.Error = "Source bytes or timestamp changed during evidence collection.";
            }
        }
        Write(sample.ReportPath, JsonSerializer.Serialize(sample, JsonOptions));
        return sample;
    }

    public static PixelDifference Measure(DecodedRaster reference, DecodedRaster candidate, out DecodedRaster? heatmap)
    {
        heatmap = null;
        foreach (var image in new[] { reference, candidate })
            if (image.Width <= 0 || image.Height <= 0 || image.Bgra.LongLength != (long)image.Width * image.Height * 4)
                throw new InvalidDataException("Comparison raster dimensions and pixels disagree.");
        if (reference.Width != candidate.Width || reference.Height != candidate.Height)
            return new(false, reference.Width, reference.Height, candidate.Width, candidate.Height);
        var pixels = new byte[reference.Bgra.Length];
        long changed = 0, sum = 0;
        var max = 0; var left = reference.Width; var top = reference.Height; var right = -1; var bottom = -1;
        for (var at = 0; at < pixels.Length; at += 4)
        {
            var delta = 0;
            for (var channel = 0; channel < 4; channel++)
            {
                var difference = Math.Abs(reference.Bgra[at + channel] - candidate.Bgra[at + channel]);
                delta = Math.Max(delta, difference); sum += difference;
            }
            max = Math.Max(max, delta);
            if (delta > 0)
            {
                changed++;
                var x = at / 4 % reference.Width; var y = at / 4 / reference.Width;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            pixels[at] = pixels[at + 2] = (byte)Math.Min(255, delta * 8);
            pixels[at + 3] = 255;
        }
        heatmap = new(reference.Width, reference.Height, pixels);
        return new(true, reference.Width, reference.Height, candidate.Width, candidate.Height,
            changed, changed * 100.0 / reference.PixelCount, sum / (double)reference.Bgra.Length, max,
            changed == 0 ? null : new RectD(left, top, right - left + 1, bottom - top + 1));
    }

    private static DecodedRaster Decode(string path)
    {
        using var bitmap = new Bitmap(path);
        var size = bitmap.PixelSize;
        var pixels = new byte[checked(size.Width * size.Height * 4)];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), pixels.Length, size.Width * 4); }
        finally { handle.Free(); }
        return new(size.Width, size.Height, pixels);
    }

    private static int RenderLegacy(string executable, string source, string target)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        start.ArgumentList.Add(Path.GetExtension(source).Equals(".phipreview", StringComparison.OrdinalIgnoreCase)
            ? "--render" : "--free-render");
        start.ArgumentList.Add(source); start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new IOException("V1 reference process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true); process.WaitForExit();
            throw new IOException("V1 reference process exceeded 30 seconds.");
        }
        Task.WaitAll(output, error);
        Write(Path.ChangeExtension(target, ".log"), output.Result + error.Result);
        return process.ExitCode;
    }

    private static Dictionary<string, string> SourceFields(string source)
    {
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = CompatibilityInputLimits.FreeBytes });
        var root = XDocument.Load(reader).Root!;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in root.Elements().Where(element => !element.HasElements))
            fields["document/" + element.Name.LocalName] = Value(element);
        var layers = root.Element("Layers")?.Elements("FreeLayer") ?? root.Element("Labels")?.Elements("ChartLabel") ?? [];
        var index = 0;
        foreach (var layer in layers)
        {
            foreach (var element in layer.Elements().Where(element => !element.HasElements))
                fields[$"layer[{index}]/" + element.Name.LocalName] = Value(element);
            index++;
        }
        return fields;

        static string Value(XElement element) => element.Name.LocalName is "Image" or "Background"
            ? $"[embedded bytes; {element.Value.Length} base64 characters]" : element.Value;
    }

    private static string Html(IReadOnlyList<EvidenceSample> samples, int failed)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
            <title>P3 兼容对照证据</title><style>
            :root{color-scheme:dark}body{font:15px/1.6 system-ui,sans-serif;margin:0;background:#111820;color:#e5edf5}
            main{max-width:1440px;margin:auto;padding:32px}h1{margin:0}p{color:#b6c7d8}.note{border-left:3px solid #f0b85b;padding:12px 18px;background:#232b35}
            article{margin:24px 0;padding:24px;background:#1c2530;border:1px solid #344353;border-radius:12px}h2{margin:0 0 12px}
            .images{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}figure{margin:0}img{width:100%;background:#101010;border:1px solid #48596a}figcaption{color:#b6c7d8}
            .overlay{position:relative;max-width:900px}.overlay img{display:block}.overlay .candidate{position:absolute;inset:0;opacity:.5}input[type=range]{width:min(100%,900px)}
            table{border-collapse:collapse;width:100%;font-size:13px}td,th{border:1px solid #425365;padding:8px;text-align:left;overflow-wrap:anywhere}pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:12px}
            select{font:inherit;padding:8px;background:#273747;color:white}summary{cursor:pointer;color:#84cbff}code{color:#9dd5ff}.tag{color:#f0b85b}@media(max-width:800px){.images{grid-template-columns:1fr}main{padding:16px}}
            </style><main><h1>P3 · V1 / V2 兼容对照</h1>
            <p>独立增强器源码 GDI+ 参考渲染 · 当前 V2 导入及原生重开 · 所有图片嵌入本页，无网络资源</p>
            <div class="note">技术收集通过不等于视觉认可。像素百分比仅用于定位差异，不设通过阈值。旧格式 Writer、字体与视觉容差仍待确认。参考程序没有复制仓库 FONT；结果依赖当前 Windows 系统字体。差分图为四通道最大差值 ×8，黑色表示相同。</div>
            <p>
            """);
        html.Append($"样例 {samples.Count} · 技术失败 {failed} · 视觉验收 PENDING</p>");
        html.Append("<label>筛选 <select id=filter><option value=all>全部样例</option><option value=Lossy>Lossy</option><option value=Exact>Exact</option><option value=Normalized>Normalized</option><option value=rejected>拒绝项</option><option value=local>本地样例</option></select></label>");
        foreach (var sample in samples)
        {
            var folder = Path.GetDirectoryName(sample.ReportPath)!;
            html.Append($"<article id=\"{E(sample.Id)}\" data-level=\"{E(sample.Report?.Level.ToString() ?? "rejected")}\" data-local=\"{sample.Local.ToString().ToLowerInvariant()}\"><h2>{E(sample.Title)}</h2>");
            html.Append($"<p><code>{E(sample.Id)}</code> · {E(sample.TechnicalStatus)} · <span class=tag>{E(sample.Report?.Summary ?? sample.ErrorKind ?? "FAILED")}</span> · 来源保持 {sample.SourceUnchanged}</p>");
            if (sample.Error is not null) html.Append($"<p>{E(sample.Error)}</p>");
            if (sample.RoundTrip is not null) html.Append($"<p>保存重开：{E(sample.RoundTrip)}</p>");
            html.Append("<div class=images>");
            foreach (var (file, title) in new[] { ("v1.png", "V1 GDI+"), ("v2.png", "V2 / native reopen"), ("difference.png", "差分 · ×8") })
            {
                var image = Path.Combine(folder, file);
                if (File.Exists(image)) html.Append($"<figure><img alt=\"{title}\" src=\"{Data(image)}\"><figcaption>{title}</figcaption></figure>");
            }
            html.Append("</div>");
            if (sample.Pixels is { } pixels)
            {
                html.Append($"<p>尺寸 {pixels.ReferenceWidth}×{pixels.ReferenceHeight} → {pixels.CandidateWidth}×{pixels.CandidateHeight} · " +
                    $"变化像素 {pixels.ChangedPixels?.ToString() ?? "尺寸不同，不重采样"} · {pixels.ChangedPercent?.ToString("0.####", CultureInfo.InvariantCulture)}% · " +
                    $"最大通道差 {pixels.MaxChannelDelta} · 包围框 {E(pixels.ChangedBounds?.ToString() ?? "无")}</p>");
                if (pixels.SameDimensions)
                    html.Append($"<details><summary>叠加检查（拖动透明度，100% 原图比例请打开 PNG）</summary><div class=overlay><img alt=V1 src=\"{Data(Path.Combine(folder, "v1.png"))}\"><img class=candidate alt=V2 src=\"{Data(Path.Combine(folder, "v2.png"))}\"></div><input aria-label=V2透明度 type=range min=0 max=100 value=50></details>");
            }
            if (sample.Error is not null) html.Append($"<p>{E(sample.Error)}</p>");
            if (sample.Report is { } report)
            {
                html.Append("<table><tr><th>维度</th><th>状态</th><th>说明</th></tr>");
                foreach (var dimension in report.Dimensions)
                    html.Append($"<tr><td>{E(dimension.Kind.ToString())}</td><td>{E(dimension.Status.ToString())}</td><td>{E(dimension.Detail)}</td></tr>");
                html.Append("</table><details><summary>字段损失与视觉诊断</summary><table><tr><th>级别 / 代码</th><th>说明</th></tr>");
                foreach (var diagnostic in report.Diagnostics)
                    html.Append($"<tr><td>{E(diagnostic.Severity + " / " + diagnostic.Code)}</td><td>{E(diagnostic.Message)}</td></tr>");
                html.Append("</table></details>");
            }
            html.Append($"<details><summary>源字段与转换后图层快照</summary><pre>{E(JsonSerializer.Serialize(new { sample.SourceFields, sample.ConvertedLayers }, JsonOptions))}</pre></details></article>");
        }
        return html.Append("""
            <script>document.getElementById('filter').addEventListener('change',e=>document.querySelectorAll('article').forEach(a=>a.hidden=e.target.value!=='all'&&(e.target.value==='local'?a.dataset.local!=='true':a.dataset.level!==e.target.value)));document.querySelectorAll('input[type=range]').forEach(r=>r.addEventListener('input',()=>r.previousElementSibling.querySelector('.candidate').style.opacity=r.value/100));</script></main></html>
            """).ToString();
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static Dictionary<string, string> WindowsFontEvidence()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return new[] { "msyh.ttc", "msyhbd.ttc", "msyhl.ttc", "arial.ttf" }.ToDictionary(name => name,
            name => File.Exists(Path.Combine(directory, name)) ? HashFile(Path.Combine(directory, name)) : "NOT_PRESENT",
            StringComparer.Ordinal);
    }
    private static string Data(string path) => "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
    private static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void Write(string path, string value) => NativeDocumentStore.WriteBytesAtomically(path, Encoding.UTF8.GetBytes(value));

    private sealed class EvidenceSample
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string Source { get; init; }
        public bool Local { get; init; }
        public string TechnicalStatus { get; set; } = "FAILED";
        public string? ErrorKind { get; set; }
        public string? Error { get; set; }
        public string SourceSha256 { get; set; } = "";
        public DateTime SourceLastWriteUtc { get; set; }
        public bool SourceUnchanged { get; set; }
        public string? NativeSha256 { get; set; }
        public string? ReferencePngSha256 { get; set; }
        public string? CandidatePngSha256 { get; set; }
        public int? LegacyExitCode { get; set; }
        public string? RoundTrip { get; set; }
        public CompatibilityReport? Report { get; set; }
        public PixelDifference? Pixels { get; set; }
        public Dictionary<string, string>? SourceFields { get; set; }
        public Layer[]? ConvertedLayers { get; set; }
        [JsonIgnore] public string ReportPath { get; set; } = "";
    }
}
