using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Editor.Core;
using Editor.Rendering;

namespace Editor.Desktop;

/// <summary>Headless rendering evidence, not physical display frame rates or a process memory bound.</summary>
internal static class PreviewCacheProbe
{
    private const int Frames = 24;
    private sealed record Timing(double P50Ms, double P95Ms, int RetainedImages, long EstimatedPixelBytes);

    public static void Run(string directory)
    {
        var scenes = new List<object>();
        foreach (var (layers, moving) in new[] { (40, false), (40, true), (200, false) })
        {
            var document = CreateScene(layers);
            var before = document.Copy();
            var uncached = Measure(document, false, moving);
            var cached = Measure(document, true, moving);
            if (!document.StateEquals(before)) throw new InvalidDataException("Preview measurement changed its document.");
            scenes.Add(new { Layers = layers, MovingArtwork = moving, Frames,
                CanvasWidth = document.CanvasWidth, CanvasHeight = document.CanvasHeight, Uncached = uncached, Cached = cached });
        }
        var boundary = CreateScene(40);
        boundary.CanvasWidth = 7680; boundary.CanvasHeight = 4320;
        var exportWatch = Stopwatch.StartNew();
        var png = PngExporter.Render(boundary, decoder: NativeImaging.Decoder);
        exportWatch.Stop();
        File.WriteAllBytes(Path.Combine(directory, "preview-cache-8k.png"), png);
        var result = new { Status = "PASS", Kind = "Headless scene paint; not physical FPS",
            Runtime = Environment.Version.ToString(), OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
            PreviewWidth = 1280, PreviewHeight = 720, DecodedImageCache.DefaultPixelBudget,
            DecodedImageCache.DefaultEntryLimit, Scenes = scenes,
            BoundaryExport = new { Width = 7680, Height = 4320, DurationMs = exportWatch.Elapsed.TotalMilliseconds,
                EncodedBytes = png.Length },
            MemoryScope = "Estimated retained BGRA pixel bytes only; process peak, codec temporaries and GPU memory are not measured." };
        File.WriteAllText(Path.Combine(directory, "preview-cache-evidence.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PREVIEW CACHE PASS: cached/fresh pixels match; 40/200-layer headless timings and 7680x4320 export recorded.");
    }

    private static Timing Measure(EditorDocument document, bool cached, bool moving)
    {
        using var images = cached ? new DecodedImageCache() : null;
        var background = new BackgroundRenderCache();
        using var target = new RenderTargetBitmap(new(1280, 720), new(96, 96));
        var transform = new ViewportTransform(document.CanvasWidth, document.CanvasHeight, 1280, 720);
        var original = document.Layers[0].Bounds;
        var timings = new double[Frames];
        try
        {
            for (var frame = -3; frame < Frames; frame++)
            {
                if (moving) document.Layers[0].Bounds = original with { X = original.X + Math.Max(0, frame) * 2 };
                var watch = Stopwatch.StartNew();
                using (var context = target.CreateDrawingContext())
                using (context.PushTransform(Matrix.CreateScale(transform.Scale, transform.Scale) *
                                             Matrix.CreateTranslation(transform.Origin.X, transform.Origin.Y)))
                    ScenePainter.Paint(context, document, NativeImaging.Decoder,
                        backgroundCache: background, imageCache: images);
                watch.Stop();
                if (frame >= 0) timings[frame] = watch.Elapsed.TotalMilliseconds;
            }
            // Compare against an independent fresh render at exactly the same viewport transform.
            using var expected = new RenderTargetBitmap(new(1280, 720), new(96, 96));
            using (var context = expected.CreateDrawingContext())
            using (context.PushTransform(Matrix.CreateScale(transform.Scale, transform.Scale) *
                                         Matrix.CreateTranslation(transform.Origin.X, transform.Origin.Y)))
                ScenePainter.Paint(context, document, NativeImaging.Decoder);
            using var actualStream = new MemoryStream(); using var expectedStream = new MemoryStream();
            target.Save(actualStream, new PngBitmapEncoderOptions()); expected.Save(expectedStream, new PngBitmapEncoderOptions());
            if (!actualStream.ToArray().SequenceEqual(expectedStream.ToArray()))
                throw new InvalidDataException("Cached preview pixels differ from fresh painting.");
            if (images is not null && (images.EstimatedPixelBytes > DecodedImageCache.DefaultPixelBudget ||
                                      images.Count > DecodedImageCache.DefaultEntryLimit))
                throw new InvalidDataException("Decoded preview cache exceeded its retention budget.");
            Array.Sort(timings);
            return new((timings[Frames / 2 - 1] + timings[Frames / 2]) / 2,
                timings[(int)Math.Ceiling(Frames * 0.95) - 1], images?.Count ?? 0, images?.EstimatedPixelBytes ?? 0);
        }
        finally { document.Layers[0].Bounds = original; }
    }

    private static EditorDocument CreateScene(int layers)
    {
        var document = new EditorDocument { BackgroundBlend = new(true) };
        var ids = new List<Guid>();
        for (var art = 0; art < 5; art++)
        {
            using var image = new RenderTargetBitmap(new(1024, 576), new(96, 96));
            using (var context = image.CreateDrawingContext())
            {
                context.FillRectangle(new SolidColorBrush(Color.FromRgb((byte)(40 + art * 40), 70, (byte)(190 - art * 30))),
                    new Rect(0, 0, 1024, 576));
                for (var stripe = 0; stripe < 24; stripe++)
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(160, (byte)(stripe * 10), 200, 90)),
                        new Rect(stripe * 43, stripe % 3 * 60, 22, 460));
            }
            using var stream = new MemoryStream(); image.Save(stream, new PngBitmapEncoderOptions());
            var id = Guid.NewGuid(); document.AddAsset(id, stream.ToArray()); ids.Add(id);
        }
        for (var i = 0; i < layers; i++)
        {
            var x = 50 + i % 5 * 530; var y = 50 + i / 40 * 160;
            document.Layers.Add(i % 8 == 0
                ? new Layer { Kind = LayerKind.Image, AssetId = ids[i / 8 % 5], Slanted = true, Bounds = new(x, y, 470, 500) }
                : new Layer { Kind = i % 2 == 0 ? LayerKind.Shape : LayerKind.Text, Text = "曲目 / Charter " + i,
                    FontSize = 24, ColorArgb = i % 2 == 0 ? 0x446688AA : 0xFFFFFFFF,
                    Bounds = new(x, y + i % 8 * 35, 400, 40) });
        }
        return document;
    }
}
