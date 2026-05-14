using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Application.Models;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using EasyPDF.Core.Rendering;
using Microsoft.Extensions.Logging;

namespace EasyPDF.Application.ViewModels;

/// <summary>
/// Drives the print preview window. Holds user-facing settings (range / fit / annotations
/// toggle) and renders one preview page at a time at screen DPI with annotations baked
/// in if the user wants them. When the user clicks "Print", the host opens the native
/// Windows print dialog for printer / copies / duplex selection.
/// </summary>
public sealed partial class PrintPreviewViewModel : ObservableObject
{
    private readonly IPdfRenderService _renderService;
    private readonly ILogger<PrintPreviewViewModel>? _logger;

    public string DocumentTitle { get; }
    public IReadOnlyList<PdfPageInfo> Pages { get; }
    public IReadOnlyList<Annotation> Annotations { get; }
    public int InitialCurrentPageIndex { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPageCount), nameof(SelectionSummary))]
    private PrintRangeKind _range = PrintRangeKind.AllPages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPageCount), nameof(SelectionSummary))]
    private string _customRange = string.Empty;

    [ObservableProperty]
    private PrintFitMode _fitMode = PrintFitMode.ShrinkToFit;

    [ObservableProperty]
    private bool _includeAnnotations = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPreviewDisplay), nameof(CanGoPrev), nameof(CanGoNext))]
    private int _previewIndexWithinSelection;

    [ObservableProperty]
    private object? _previewImage;   // boxed BitmapSource — kept as object so the VM stays
                                     // WPF-agnostic at the type level (view casts on bind).

    [ObservableProperty]
    private bool _isRenderingPreview;

    public int SelectedPageCount => ResolvedSelection().Count;
    public string SelectionSummary => SelectedPageCount switch
    {
        0 => "No pages selected",
        1 => "1 page selected",
        var n => $"{n} pages selected"
    };
    public string CurrentPreviewDisplay
    {
        get
        {
            var sel = ResolvedSelection();
            if (sel.Count == 0) return "—";
            int safeIdx = Math.Clamp(PreviewIndexWithinSelection, 0, sel.Count - 1);
            return $"Page {sel[safeIdx] + 1}  ({safeIdx + 1} of {sel.Count})";
        }
    }

    public bool CanGoPrev => PreviewIndexWithinSelection > 0 && SelectedPageCount > 1;
    public bool CanGoNext => PreviewIndexWithinSelection < SelectedPageCount - 1 && SelectedPageCount > 1;

    /// Set by the window before closing — true if the user clicked Print, false on Cancel.
    public bool PrintConfirmed { get; private set; }

    public event EventHandler? CloseRequested;

    public PrintPreviewViewModel(
        string documentTitle,
        IReadOnlyList<PdfPageInfo> pages,
        IReadOnlyList<Annotation> annotations,
        int currentPageIndex,
        IPdfRenderService renderService,
        ILogger<PrintPreviewViewModel>? logger = null)
    {
        DocumentTitle = documentTitle;
        Pages = pages;
        Annotations = annotations;
        InitialCurrentPageIndex = Math.Clamp(currentPageIndex, 0, Math.Max(0, pages.Count - 1));
        _renderService = renderService;
        _logger = logger;
    }

    public PrintSettings BuildSettings() => new()
    {
        Range = Range,
        CustomRange = CustomRange,
        FitMode = FitMode,
        IncludeAnnotations = IncludeAnnotations,
    };

    /// Indices (0-based) of pages that will actually print, in order.
    public IReadOnlyList<int> ResolvedSelection() => Range switch
    {
        PrintRangeKind.AllPages       => Enumerable.Range(0, Pages.Count).ToArray(),
        PrintRangeKind.CurrentPage    => [InitialCurrentPageIndex],
        PrintRangeKind.CustomRange    => PageRangeParser.Parse(CustomRange, Pages.Count),
        PrintRangeKind.OddPagesOnly   => Enumerable.Range(0, Pages.Count).Where(i => i % 2 == 0).ToArray(),
        PrintRangeKind.EvenPagesOnly  => Enumerable.Range(0, Pages.Count).Where(i => i % 2 == 1).ToArray(),
        _                             => [],
    };

    [RelayCommand]
    private void ConfirmPrint()
    {
        if (SelectedPageCount == 0) return;
        PrintConfirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        PrintConfirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanGoPrev))]
    private void PrevPreview() => PreviewIndexWithinSelection--;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPreview() => PreviewIndexWithinSelection++;

    partial void OnPreviewIndexWithinSelectionChanged(int value)
    {
        PrevPreviewCommand.NotifyCanExecuteChanged();
        NextPreviewCommand.NotifyCanExecuteChanged();
    }

    /// Called by the view after construction and whenever settings change.
    /// Renders the page at <see cref="PreviewIndexWithinSelection"/> inside the resolved
    /// selection. Result is a frozen BitmapSource boxed as object — the view casts when
    /// binding to <c>Image.Source</c>.
    public async Task RefreshPreviewAsync(Func<RenderedPage, object> bitmapFactory, CancellationToken ct = default)
    {
        var sel = ResolvedSelection();
        if (sel.Count == 0)
        {
            PreviewImage = null;
            return;
        }

        int safeIdx = Math.Clamp(PreviewIndexWithinSelection, 0, sel.Count - 1);
        if (safeIdx != PreviewIndexWithinSelection)
            PreviewIndexWithinSelection = safeIdx;   // will re-trigger us

        int pageIdx = sel[safeIdx];

        // Low DPI is fine for screen preview — 96 DPI matches WPF's native unit.
        // Bake happens off-thread so the UI stays responsive on big pages.
        IsRenderingPreview = true;
        try
        {
            const double previewScale = 1.0;   // ~72 DPI ≈ "fit document point to screen point"
            var rendered = await _renderService.RenderPageAsync(pageIdx, previewScale, 1.0, ct).ConfigureAwait(true);

            object image = await Task.Run(() =>
            {
                if (!IncludeAnnotations)
                    return bitmapFactory(rendered);

                var pageAnns = Annotations
                    .Where(a => a.PageIndex == pageIdx
                             && (a.Type == AnnotationType.Highlight || a.Type == AnnotationType.Underline))
                    .ToList();

                if (pageAnns.Count == 0) return bitmapFactory(rendered);

                var baked = BakeOnto(rendered, pageAnns, previewScale);
                return bitmapFactory(baked);
            }, ct).ConfigureAwait(true);

            PreviewImage = image;
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Preview render failed for page {Page}", pageIdx);
        }
        finally
        {
            IsRenderingPreview = false;
        }
    }

    /// Clones the pixel buffer and bakes the given annotations using <see cref="AnnotationBaker"/>.
    /// Same call shape the viewer's <c>RebakePageBitmapAsync</c> uses.
    private static RenderedPage BakeOnto(RenderedPage page, IEnumerable<Annotation> annotations, double physicalScale)
    {
        var pixels = (byte[])page.PixelData.Clone();
        foreach (var ann in annotations)
        {
            if (ann.Type == AnnotationType.Highlight)
                AnnotationBaker.BakeHighlight(pixels, page, ann, physicalScale);
            else if (ann.Type == AnnotationType.Underline)
                AnnotationBaker.BakeUnderline(pixels, page, ann, physicalScale);
        }
        return page with { PixelData = pixels };
    }
}
