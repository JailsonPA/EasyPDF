using EasyPDF.Application.Interfaces;
using EasyPDF.Core.Interfaces;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EasyPDF.UI.Services;

internal sealed class WpfExportService : IExportService
{
    private readonly IPdfRenderService _render;

    public WpfExportService(IPdfRenderService render) => _render = render;

    public async Task ExportPageAsync(int pageIndex, string filePath, int dpi = 150, CancellationToken ct = default)
    {
        double scale = dpi / 72.0;
        var page = await _render.RenderPageAsync(pageIndex, scale, 1.0, ct).ConfigureAwait(false);

        var bitmap = BitmapSource.Create(
            page.Width, page.Height,
            dpi, dpi,
            PixelFormats.Bgra32,
            null,
            page.PixelData,
            page.Stride);
        bitmap.Freeze();

        BitmapEncoder encoder = string.Equals(
            Path.GetExtension(filePath), ".jpg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = 92 }
            : new PngBitmapEncoder();

        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        await using var stream = File.Create(filePath);
        encoder.Save(stream);
    }
}
