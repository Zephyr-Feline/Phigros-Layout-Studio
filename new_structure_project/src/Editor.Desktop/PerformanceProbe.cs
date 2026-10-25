using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Editor.Core;
using Editor.Rendering;

namespace Editor.Desktop;

internal static class PerformanceProbe
{
    public static int Run(string directory, bool includeLargeResources = false)
    {
        if (includeLargeResources && (File.Exists(directory) ||
            Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()))
            throw new IOException("Large-resource performance output must be new or empty; previous evidence is never overwritten.");
        Directory.CreateDirectory(directory);
        var results = new List<object>
        {
            Measure("typical", 2740, 1080, 40, 24),
            Measure("stress", 7680, 4320, 200, 12),
            MeasureImageHistory("image-history", 16 * 1024 * 1024, 200)
        };
        if (includeLargeResources)
            results.Add(LargeResourceReliabilityProbe.Run(Path.Combine(directory, "large-resources")));
        var jsonOptions = new JsonSerializerOptions
            { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(results, jsonOptions);
        var path = Path.Combine(directory, "performance.json");
        if (includeLargeResources)
            Editor.Formats.AtomicFile.WriteNew(path, stream => JsonSerializer.Serialize(stream, results, jsonOptions));
        else File.WriteAllText(path, json);
        Console.WriteLine(json);
        return 0;
    }

    private static object Measure(string name, int width, int height, int count, int frames)
    {
        var doc = new EditorDocument { CanvasWidth = width, CanvasHeight = height };
        for (var i = 0; i < count; i++)
        {
            var x = 40 + (i % 10) * (width - 200.0) / 10;
            var y = 40 + (i / 10) * Math.Min(120, (height - 100.0) / 22);
            if (i % 5 == 0)
                doc.Layers.Add(EditorDocument.NewBadge(new PointD(x, y)));
            else if (i % 3 == 0)
                doc.Layers.Add(new Layer { Kind = LayerKind.Text, Text = $"中文 Test {i}",
                    Bounds = new RectD(x, y, 260, 95), FontSize = 28 });
            else
                doc.Layers.Add(new Layer { Kind = LayerKind.Shape, Bounds = new RectD(x, y, 240, 85),
                    ColorArgb = i % 2 == 0 ? 0xFF2070A0 : 0xFFC04050 });
        }
        doc.Validate();
        using var target = new RenderTargetBitmap(new PixelSize(1280, 720), new Vector(96, 96));
        var fit = Math.Min(1280.0 / width, 720.0 / height);
        void Frame()
        {
            using var context = target.CreateDrawingContext();
            using (context.PushTransform(Matrix.CreateScale(fit, fit) *
                                         Matrix.CreateTranslation((1280 - width * fit) / 2,
                                             (720 - height * fit) / 2)))
                ScenePainter.Paint(context, doc, NativeImaging.Decoder);
        }
        Frame();
        var samples = new double[frames];
        for (var i = 0; i < frames; i++)
        {
            var sw = Stopwatch.StartNew();
            Frame();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        var exportWatch = Stopwatch.StartNew();
        var png = PngExporter.Render(doc, decoder: NativeImaging.Decoder);
        exportWatch.Stop();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new
        {
            scenario = name,
            layers = count,
            canvas = $"{width}x{height}",
            view = "1280x720",
            frameP50Ms = samples[(int)Math.Floor((samples.Length - 1) * 0.50)],
            frameP95Ms = samples[(int)Math.Floor((samples.Length - 1) * 0.95)],
            exportMs = exportWatch.Elapsed.TotalMilliseconds,
            exportBytes = png.Length,
            processPeakWorkingSetBytes = process.PeakWorkingSet64,
            processPrivateBytes = process.PrivateMemorySize64,
            note = "Headless software scene-draw proxy; not compositor presentation time."
        };
    }

    /// <summary>
    /// Measurement (not an assertion) for REQ-STORAGE-008: many transform edits over one large
    /// payload must not multiply the image bytes in history.
    /// </summary>
    private static object MeasureImageHistory(string name, int payloadBytes, int edits)
    {
        var payload = new byte[payloadBytes];
        new Random(11).NextBytes(payload);
        var session = new Editor.Application.EditSession(new EditorDocument());
        var layer = new Layer { Kind = LayerKind.Image, AssetId = Guid.NewGuid(),
            Bounds = new RectD(0, 0, 100, 100) };
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        session.AddImageLayer(layer, payload);
        var afterImport = GC.GetTotalMemory(forceFullCollection: true);
        for (var i = 0; i < edits; i++)
        {
            session.BeginGesture(layer.Id);
            session.MoveBy(1, 1);
            session.CommitGesture();
        }
        var afterEdits = GC.GetTotalMemory(forceFullCollection: true);
        var distinctPayloads = new HashSet<AssetBlob>(ReferenceEqualityComparer.Instance);
        var undoStates = 0;
        while (session.Undo())
        {
            undoStates++;
            distinctPayloads.Add(session.Document.Assets[layer.AssetId!.Value]);
        }
        var afterUndoWalk = GC.GetTotalMemory(forceFullCollection: true);
        return new
        {
            scenario = name,
            payloadBytes,
            historyEdits = edits,
            historyPolicy = Editor.Application.EditSession.DefaultMaxHistoryEntries,
            retainedUndoStates = undoStates,
            undoCountAfterWalk = session.UndoCount,
            distinctPayloadObjects = distinctPayloads.Count,
            managedBytesBaseline = baseline,
            managedBytesAfterImport = afterImport,
            managedBytesAfterEdits = afterEdits,
            managedBytesAfterUndoWalk = afterUndoWalk,
            note = "Managed heap after a full collection; payload identity, not a memory budget."
        };
    }
}
