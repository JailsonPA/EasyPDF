using EasyPDF.Application.Interfaces;
using EasyPDF.Application.Models;
using EasyPDF.Application.ViewModels;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using EasyPDF.Core.Rendering;
using EasyPDF.UI.Views;
using Microsoft.Extensions.Logging;
using System.Windows.Controls;
using System.Windows.Documents;
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

    public Task PrintAsync(
        string documentTitle,
        IReadOnlyList<PdfPageInfo> pages,
        IReadOnlyList<Annotation> annotations,
        int currentPageIndex,
        CancellationToken ct = default)
    {
        if (pages.Count == 0) return Task.CompletedTask;

        // Step 1 — show our preview window. User chooses range / fit / annotation toggle.
        var vm = new PrintPreviewViewModel(
            documentTitle, pages, annotations, currentPageIndex, _renderService);
        var preview = new PrintPreviewWindow(vm)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        if (preview.ShowDialog() != true) return Task.CompletedTask;

        var settings = vm.BuildSettings();
        var selection = vm.ResolvedSelection();
        if (selection.Count == 0) return Task.CompletedTask;

        // Step 2 — native PrintDialog for printer / copies / duplex / color / quality.
        var dlg = new System.Windows.Controls.PrintDialog
        {
            PageRangeSelection = PageRangeSelection.AllPages,
            UserPageRangeEnabled = false,   // range already picked in our preview
        };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;

        // 300 DPI gives sharp text and reasonable file sizes for the spooler.
        const double printDpi = 300.0;

        var paginator = new AnnotatedPdfPaginator(
            _renderService, pages, annotations, selection, settings,
            printDpi,
            dlg.PrintableAreaWidth, dlg.PrintableAreaHeight,
            _logger);

        dlg.PrintDocument(paginator, documentTitle);
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Paginator: streams pages one at a time so even 1000-page jobs stay bounded
    // to a single page worth of pixels in memory. Bakes annotations + applies
    // fit-to-page transform per page.
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class AnnotatedPdfPaginator : DocumentPaginator
    {
        private readonly IPdfRenderService _renderService;
        private readonly IReadOnlyList<PdfPageInfo> _allPages;
        private readonly IReadOnlyList<Annotation> _annotations;
        private readonly IReadOnlyList<int> _selection;
        private readonly PrintSettings _settings;
        private readonly double _printDpi;
        private readonly double _paperWidthDip;   // WPF logical units (1/96 inch)
        private readonly double _paperHeightDip;
        private readonly ILogger _logger;

        public AnnotatedPdfPaginator(
            IPdfRenderService renderService,
            IReadOnlyList<PdfPageInfo> allPages,
            IReadOnlyList<Annotation> annotations,
            IReadOnlyList<int> selection,
            PrintSettings settings,
            double printDpi,
            double paperWidthDip,
            double paperHeightDip,
            ILogger logger)
        {
            _renderService = renderService;
            _allPages = allPages;
            _annotations = annotations;
            _selection = selection;
            _settings = settings;
            _printDpi = printDpi;
            _paperWidthDip = paperWidthDip;
            _paperHeightDip = paperHeightDip;
            _logger = logger;
            PageSize = new System.Windows.Size(paperWidthDip, paperHeightDip);
        }

        public override DocumentPage GetPage(int pageNumber)
        {
            if (pageNumber < 0 || pageNumber >= _selection.Count)
                return DocumentPage.Missing;

            int pageIdx = _selection[pageNumber];
            var info = _allPages[pageIdx];

            double renderScale = _printDpi / 72.0;

            RenderedPage rendered;
            try
            {
                // Sync wait is safe here: the paginator runs on a non-UI spooler thread,
                // so blocking it doesn't deadlock anything.
                rendered = _renderService.RenderPageAsync(pageIdx, renderScale, 1.0)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to render page {Page} for printing", pageIdx + 1);
                return DocumentPage.Missing;
            }

            if (_settings.IncludeAnnotations)
                rendered = BakeAnnotations(rendered, pageIdx, renderScale);

            var format = rendered.BitsPerPixel == 32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
            var bitmap = BitmapSource.Create(
                rendered.Width, rendered.Height,
                _printDpi, _printDpi,
                format, null, rendered.PixelData, rendered.Stride);
            bitmap.Freeze();

            // Native size of the PDF page in WPF logical units (1 pt = 96/72 px).
            double pageNativeW = info.WidthPt  * (96.0 / 72.0);
            double pageNativeH = info.HeightPt * (96.0 / 72.0);

            // Decide the actual draw size based on fit mode.
            (double drawW, double drawH) = ComputeDrawSize(pageNativeW, pageNativeH);

            // FixedPage always sized to paper — the printer driver positions it correctly.
            var fixedPage = new FixedPage { Width = _paperWidthDip, Height = _paperHeightDip };
            var image = new Image
            {
                Source = bitmap,
                Width = drawW,
                Height = drawH,
                Stretch = Stretch.Fill,
            };

            // Center on the page.
            FixedPage.SetLeft(image, (_paperWidthDip  - drawW) / 2);
            FixedPage.SetTop (image, (_paperHeightDip - drawH) / 2);

            fixedPage.Children.Add(image);
            fixedPage.Measure(new System.Windows.Size(_paperWidthDip, _paperHeightDip));
            fixedPage.Arrange(new System.Windows.Rect(0, 0, _paperWidthDip, _paperHeightDip));

            var paperSize = new System.Windows.Size(_paperWidthDip, _paperHeightDip);
            return new DocumentPage(fixedPage, paperSize,
                new System.Windows.Rect(paperSize),
                new System.Windows.Rect(paperSize));
        }

        private (double w, double h) ComputeDrawSize(double nativeW, double nativeH)
        {
            switch (_settings.FitMode)
            {
                case PrintFitMode.Actual:
                    return (nativeW, nativeH);

                case PrintFitMode.FitToPage:
                {
                    // Scale to fill paper, preserving aspect (touches the tighter edge).
                    double scale = Math.Min(_paperWidthDip / nativeW, _paperHeightDip / nativeH);
                    return (nativeW * scale, nativeH * scale);
                }

                case PrintFitMode.ShrinkToFit:
                default:
                {
                    // Scale DOWN only when the page is bigger than paper; never up.
                    double scale = Math.Min(1.0, Math.Min(_paperWidthDip / nativeW, _paperHeightDip / nativeH));
                    return (nativeW * scale, nativeH * scale);
                }
            }
        }

        private RenderedPage BakeAnnotations(RenderedPage rendered, int pageIdx, double physicalScale)
        {
            var pageAnns = _annotations
                .Where(a => a.PageIndex == pageIdx
                         && (a.Type == AnnotationType.Highlight || a.Type == AnnotationType.Underline))
                .ToList();
            if (pageAnns.Count == 0) return rendered;

            var pixels = (byte[])rendered.PixelData.Clone();
            foreach (var ann in pageAnns)
            {
                if (ann.Type == AnnotationType.Highlight)
                    AnnotationBaker.BakeHighlight(pixels, rendered, ann, physicalScale);
                else
                    AnnotationBaker.BakeUnderline(pixels, rendered, ann, physicalScale);
            }
            return rendered with { PixelData = pixels };
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => _selection.Count;
        public override System.Windows.Size PageSize { get; set; }
        public override IDocumentPaginatorSource Source => null!;
    }
}
