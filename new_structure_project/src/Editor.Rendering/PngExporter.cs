using Avalonia;
using Avalonia.Media.Imaging;
using Editor.Core;

namespace Editor.Rendering;

public static class PngExporter
{
    /// <summary>Paints document content directly to a bitmap. Selection and guides have no input here.</summary>
    public static byte[] Render(EditorDocument document, bool transparentBackground = false,
        AssetImageDecoder? decoder = null, int? outputWidth = null,
        PngExportRegion? outputRegion = null)
    {
        var size = PngExportSize.Create(document, outputWidth, outputRegion);
        using var target = new RenderTargetBitmap(
            new PixelSize(size.Width, size.Height), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            using (context.PushTransform(Matrix.CreateScale(size.Scale, size.Scale) *
                                         Matrix.CreateTranslation(
                                             -(outputRegion?.X ?? 0) * size.Scale,
                                             -(outputRegion?.Y ?? 0) * size.Scale + size.OffsetY)))
                ScenePainter.Paint(context, document, decoder, transparentBackground);
        }
        using var stream = new MemoryStream();
        target.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }
}
