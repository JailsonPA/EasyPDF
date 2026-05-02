using EasyPDF.Application.Interfaces;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EasyPDF.UI.Services;

internal sealed class WpfPrintService : IPrintService
{
    private readonly IPdfRenderService _renderService;
    private readonly ILogger<WpfPrintService> _logger;

    public WpfPrintService(IPdfRenderService renderService, ILogger<WpfPrintService> logger)
    {
        _renderService = renderService;
        _logger = logger;
    }

    public async Task PrintAsync(string documentTitle, IReadOnlyList<PdfPageInfo> pages, CancellationToken ct = default)
    {
        if (pages.Count == 0) return;

        var dlg = new PrintDialog
        {
            PageRangeSelection = PageRangeSelection.AllPages,
            UserPageRangeEnabled = true,
            MinPage = 1,
            MaxPage = (uint)pages.Count,
        };

        if (dlg.ShowDialog() != true) return;

        int from = 0, to = pages.Count - 1;
        if (dlg.PageRangeSelection == PageRangeSelection.UserPages)
        {
            from = Math.Clamp(dlg.PageRange.PageFrom - 1, 0, pages.Count - 1);
            to   = Math.Clamp(dlg.PageRange.PageTo   - 1, 0, pages.Count - 1);
        }

        // 300 DPI gives sufficient print quality.
        // 1 PDF point = 1/72 inch → at 300 DPI = 300/72 pixels per point.
        const double printDpi    = 300.0;
        const double renderScale = printDpi / 72.0;

        var doc = new FixedDocument();

        for (int i = from; i <= to && !ct.IsCancellationRequested; i++)
        {
            var info = pages[i];

            RenderedPage rendered;
            try
            {
                rendered = await _renderService.RenderPageAsync(info.Index, renderScale, 1.0, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to render page {Page} for printing — skipping", i + 1);
                continue;
            }

            var format = rendered.BitsPerPixel == 32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
            var bitmap = BitmapSource.Create(
                rendered.Width, rendered.Height,
                printDpi, printDpi,
                format, null,
                rendered.PixelData,
                rendered.Stride);
            bitmap.Freeze();

            // FixedPage dimensions in WPF logical units (96 dpi): 1 PDF pt = 96/72 logical px.
            double pageW = info.WidthPt  * (96.0 / 72.0);
            double pageH = info.HeightPt * (96.0 / 72.0);

            var fixedPage = new FixedPage { Width = pageW, Height = pageH };
            var image = new Image { Source = bitmap, Width = pageW, Height = pageH };
            fixedPage.Children.Add(image);

            var content = new PageContent();
            ((IAddChild)content).AddChild(fixedPage);
            doc.Pages.Add(content);
        }

        if (doc.Pages.Count == 0 || ct.IsCancellationRequested) return;
        dlg.PrintDocument(doc.DocumentPaginator, documentTitle);
    }
}
