using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace EasyPDF.Application.ViewModels;

/// <summary>
/// Controls page navigation, zoom, and per-page rendering requests for the main viewer.
/// Pages are rendered on demand; virtualization is driven by the View notifying
/// which page indices are currently visible.
/// </summary>
public sealed partial class PdfViewerViewModel : ObservableObject, IDisposable
{
    private const double MinScale = 0.1;
    private const double MaxScale = 8.0;
    private const double ZoomStep = 0.25;
    private const double DefaultScale = 1.5; // ~108 DPI — comfortable default

    private readonly IPdfRenderService _renderService;
    private readonly ITextExtractionService _textService;
    private readonly ILogger<PdfViewerViewModel> _logger;
    private readonly Dictionary<int, CancellationTokenSource> _pendingRenders = new();
    private bool _applyingFitToWidth;
    private bool _applyingFitToPage;

    // Search highlight state — stored so highlights can be recomputed after zoom changes.
    private IReadOnlyList<SearchResult> _searchResults = [];
    private int _activeResultIndex = -1;

    // Text selection state
    private string? _selectedText;
    public string? SelectedText => _selectedText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageDisplay))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoForward))]
    private int _currentPageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercent))]
    private double _scale = DefaultScale;

    [ObservableProperty]
    private int _pageCount;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isFitToWidth = true;

    [ObservableProperty]
    private bool _isFitToPage;

    // Pages exposed to the View; the View's ItemsControl virtualizes rendering.
    // BulkObservableCollection fires a single Reset instead of N Add notifications.
    public BulkObservableCollection<PageViewModel> Pages { get; } = new();

    public string CurrentPageDisplay => $"{CurrentPageIndex + 1} / {PageCount}";
    public string ZoomPercent => $"{Scale * 100:F0}%";
    public bool CanGoBack => CurrentPageIndex > 0;
    public bool CanGoForward => CurrentPageIndex < PageCount - 1;

    public event EventHandler<int>? ScrollToPageRequested;

    // Fired after a document loads when IsFitToWidth is true, so the View can
    // call FitToWidth() immediately — IsFitToWidth doesn't change, so no
    // PropertyChanged fires and SizeChanged doesn't fire on a content-only reload.
    public event EventHandler? FitToWidthRequested;
    public event EventHandler? FitToPageRequested;

    public PdfViewerViewModel(
        IPdfRenderService renderService,
        ITextExtractionService textService,
        ILogger<PdfViewerViewModel> logger)
    {
        _renderService = renderService;
        _textService   = textService;
        _logger        = logger;
    }

    public void LoadDocument(PdfDocument document)
    {
        var vms = document.Pages.Select(p =>
        {
            var vm = new PageViewModel(p.Index, p.WidthPt, p.HeightPt, p.Links);
            vm.Scale = Scale;
            return vm;
        });
        Pages.ReplaceAll(vms); // 1 Reset notification instead of N Add notifications

        PageCount = document.PageCount;
        CurrentPageIndex = 0;
        IsLoading = false;
        ErrorMessage = null;

        if (IsFitToWidth)
            FitToWidthRequested?.Invoke(this, EventArgs.Empty);
        else if (IsFitToPage)
            FitToPageRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        CancelAllPendingRenders();
        ClearSelection();
        Pages.Clear();
        PageCount = 0;
        CurrentPageIndex = 0;
        _searchResults = [];
        _activeResultIndex = -1;
    }

    /// <summary>
    /// Called by the View when a page enters the visible viewport.
    /// <paramref name="dpiScale"/> is the screen's physical-to-logical pixel ratio,
    /// used to render at the correct physical resolution for crisp high-DPI output.
    /// </summary>
    public async Task OnPageBecameVisibleAsync(int pageIndex, double dpiScale = 1.0, CancellationToken viewCt = default)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        var pageVm = Pages[pageIndex];
        if (pageVm.RenderedPage is not null && !pageVm.IsStale) return; // already rendered at current scale

        // Cancel and remove any previous render for this slot.
        // Remove explicitly so the old call's finally block cannot orphan our new CTS.
        if (_pendingRenders.TryGetValue(pageIndex, out var old))
        {
            old.Cancel();
            old.Dispose();
            _pendingRenders.Remove(pageIndex);
        }

        // Do NOT use `using var` — the CTS lifecycle is managed explicitly below
        // so that CancelAllPendingRenders() and this finally block don't double-dispose.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(viewCt);
        _pendingRenders[pageIndex] = cts;

        try
        {
            if (!pageVm.IsStale)
                pageVm.IsRendering = true;  // suppress spinner when old bitmap is still visible
            var rendered = await _renderService.RenderPageAsync(pageIndex, Scale, dpiScale, cts.Token);
            pageVm.IsStale = false;
            pageVm.RenderedPage = rendered;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Render failed for page {Page}", pageIndex);
            pageVm.IsStale = false;
            pageVm.RenderedPage = null;
            pageVm.HasError = true;
        }
        finally
        {
            pageVm.IsRendering = false;
            // Only remove and dispose OUR CTS. If CancelAllPendingRenders() already cleared
            // the dict, or a newer call replaced our entry, ReferenceEquals prevents us from
            // disposing the wrong CTS or leaking the replacement.
            if (_pendingRenders.TryGetValue(pageIndex, out var current) && ReferenceEquals(current, cts))
            {
                _pendingRenders.Remove(pageIndex);
                cts.Dispose();
            }
            // else: CancelAllPendingRenders() already cancelled+disposed cts — nothing to do.
        }
    }

    /// <summary>
    /// Renders a page at 150 DPI for placing on the system clipboard.
    /// Returns null on failure or cancellation.
    /// </summary>
    public async Task<RenderedPage?> RenderPageForClipboardAsync(int pageIndex, CancellationToken ct = default)
    {
        const double clipboardDpi = 150.0;
        try
        {
            return await _renderService.RenderPageAsync(pageIndex, clipboardDpi / 72.0, 1.0, ct)
                                       .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Clipboard render failed for page {Page}", pageIndex);
            return null;
        }
    }

    /// <summary>
    /// Scales all pages to exactly fill <paramref name="viewportWidth"/>.
    /// Guards against the resulting OnScaleChanged resetting IsFitToWidth.
    /// </summary>
    public void FitToWidth(double viewportWidth)
    {
        if (Pages.Count == 0 || viewportWidth <= 0) return;
        var firstPage = Pages[0];
        if (firstPage.WidthPt <= 0) return;
        _applyingFitToWidth = true;
        try
        {
            // 20 = scrollbar/padding safety margin; 16 = Border Margin="8" × 2 sides (ControlWidth offset)
        Scale = Math.Clamp((viewportWidth - 36) / firstPage.WidthPt, MinScale, MaxScale);
            IsFitToWidth = true;
        }
        finally
        {
            _applyingFitToWidth = false;
        }
    }

    /// <summary>
    /// Scales all pages so one page height exactly fills <paramref name="viewportHeight"/>.
    /// </summary>
    public void FitToPage(double viewportHeight)
    {
        if (Pages.Count == 0 || viewportHeight <= 0) return;
        var firstPage = Pages[0];
        if (firstPage.HeightPt <= 0) return;
        _applyingFitToPage = true;
        try
        {
            // 32 = 16 (shadow margin) + 8 (page top+bottom margin) + 8 (buffer)
            Scale = Math.Clamp((viewportHeight - 32) / firstPage.HeightPt, MinScale, MaxScale);
            IsFitToPage = true;
        }
        finally
        {
            _applyingFitToPage = false;
        }
    }

    /// <summary>
    /// Invalidates rendered bitmaps when zoom changes so pages re-render at the new resolution.
    /// Also recomputes highlight positions to match the new scale.
    /// </summary>
    partial void OnScaleChanged(double value)
    {
        if (!_applyingFitToWidth)
            IsFitToWidth = false;
        if (!_applyingFitToPage)
            IsFitToPage = false;
        CancelAllPendingRenders();
        foreach (var p in Pages)
        {
            p.Scale = value;
            if (p.RenderedPage is not null)
                p.IsStale = true;  // keep old bitmap visible; fresh render will clear this
        }
        if (_searchResults.Count > 0)
            ApplySearchHighlights();
    }

    // ── Search highlight public API ───────────────────────────────────────────

    /// <summary>
    /// Pushes converted highlight rectangles onto each affected PageViewModel.
    /// Call this after search completes and after each result navigation.
    /// </summary>
    public void UpdateSearchHighlights(IReadOnlyList<SearchResult> results, int activeIndex)
    {
        _searchResults = results;
        _activeResultIndex = activeIndex;
        ApplySearchHighlights();
    }

    public void ClearSearchHighlights()
    {
        _searchResults = [];
        _activeResultIndex = -1;
        foreach (var p in Pages)
            p.SearchHighlights = [];
    }

    private void ApplySearchHighlights()
    {
        // Clear all pages first so pages with no matches lose stale highlights.
        foreach (var p in Pages)
            p.SearchHighlights = [];

        foreach (var group in _searchResults
            .Select((r, i) => (result: r, globalIndex: i))
            .GroupBy(x => x.result.PageIndex))
        {
            if (group.Key < 0 || group.Key >= Pages.Count) continue;
            var pageVm = Pages[group.Key];

            pageVm.SearchHighlights = group
                .SelectMany(x => x.result.Quads.Select(
                    q => ConvertQuad(q, pageVm.Scale, x.globalIndex == _activeResultIndex)))
                .ToList();
        }
    }

    // ── Text selection public API ─────────────────────────────────────────────

    public void ClearSelection()
    {
        _selectedText = null;
        foreach (var p in Pages) p.SelectionHighlights = [];
    }

    /// <summary>
    /// Extracts the text and highlight quads for the drag region on <paramref name="pageVm"/>
    /// and stores the result for <see cref="SelectedText"/> / Ctrl+C.
    /// </summary>
    public async Task ExtractSelectionAsync(
        PageViewModel pageVm,
        float startX, float startY,
        float endX,   float endY,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _textService.ExtractSelectionAsync(
                pageVm.PageIndex,
                new PdfPoint(startX, startY),
                new PdfPoint(endX, endY),
                ct);

            if (result is null || result.Quads.Count == 0)
            {
                pageVm.SelectionHighlights = [];
                return;
            }

            pageVm.SelectionHighlights = result.Quads
                .Select(q => new HighlightRect(
                    q.X * pageVm.Scale, q.Y * pageVm.Scale,
                    q.Width * pageVm.Scale, q.Height * pageVm.Scale,
                    false))
                .ToList();

            _selectedText = result.Text;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Text selection extraction failed on page {Page}", pageVm.PageIndex);
            pageVm.SelectionHighlights = [];
        }
    }

    // MuPDF search quads use top-left origin, Y increases downward — same as WPF.
    // Multiplying by Scale converts PDF points → WPF logical pixels.
    private static HighlightRect ConvertQuad(PdfRect q, double scale, bool isActive) =>
        new(q.X * scale, q.Y * scale, q.Width * scale, q.Height * scale, isActive);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousPage()
    {
        if (CanGoBack)
        {
            CurrentPageIndex--;
            ScrollToPageRequested?.Invoke(this, CurrentPageIndex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NextPage()
    {
        if (CanGoForward)
        {
            CurrentPageIndex++;
            ScrollToPageRequested?.Invoke(this, CurrentPageIndex);
        }
    }

    [RelayCommand]
    private void GoToPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount) return;
        CurrentPageIndex = pageIndex;
        ScrollToPageRequested?.Invoke(this, CurrentPageIndex);
    }

    [RelayCommand]
    private void FitToWidthMode() => IsFitToWidth = true; // View applies FitToWidth via PropertyChanged

    [RelayCommand]
    private void FitToPageMode() => IsFitToPage = true; // View applies FitToPage via PropertyChanged

    [RelayCommand]
    private void ZoomIn() => Scale = Math.Min(MaxScale, Scale + ZoomStep);

    [RelayCommand]
    private void ZoomOut() => Scale = Math.Max(MinScale, Scale - ZoomStep);

    [RelayCommand]
    private void ResetZoom() => Scale = DefaultScale;

    [RelayCommand]
    private void ZoomTo(double percent) =>
        Scale = Math.Clamp(percent / 100.0, MinScale, MaxScale);

    [RelayCommand]
    private void RotateCurrentPageClockwise()
    {
        if (CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count)
            Pages[CurrentPageIndex].Rotation = (Pages[CurrentPageIndex].Rotation + 90) % 360;
    }

    [RelayCommand]
    private void RotateCurrentPageCounterClockwise()
    {
        if (CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count)
            Pages[CurrentPageIndex].Rotation = (Pages[CurrentPageIndex].Rotation + 270) % 360;
    }

    [RelayCommand]
    private void FirstPage() => GoToPage(0);

    [RelayCommand]
    private void LastPage() => GoToPage(PageCount - 1);

    public void HandleMouseWheelZoom(bool zoomIn, double delta)
    {
        double factor = 1.0 + (Math.Abs(delta) / 1200.0);
        Scale = zoomIn
            ? Math.Min(MaxScale, Scale * factor)
            : Math.Max(MinScale, Scale / factor);
    }

    private void CancelAllPendingRenders()
    {
        foreach (var cts in _pendingRenders.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _pendingRenders.Clear();
    }

    public void Dispose() => CancelAllPendingRenders();
}

/// <summary>
/// Per-page state visible to the ItemsControl; holds the rendered bitmap once available.
/// </summary>
public sealed partial class PageViewModel : ObservableObject
{
    public int PageIndex { get; }
    public int DisplayNumber => PageIndex + 1;
    public double WidthPt { get; }
    public double HeightPt { get; }
    public IReadOnlyList<PdfLink> Links { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayWidth))]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    [NotifyPropertyChangedFor(nameof(ControlWidth))]
    [NotifyPropertyChangedFor(nameof(ControlHeight))]
    [NotifyPropertyChangedFor(nameof(BitmapWidth))]
    [NotifyPropertyChangedFor(nameof(BitmapHeight))]
    private double _scale = 1.5;

    // Rotation in degrees — 0, 90, 180, or 270.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayWidth))]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    [NotifyPropertyChangedFor(nameof(ControlWidth))]
    [NotifyPropertyChangedFor(nameof(ControlHeight))]
    private int _rotation;

    private bool IsTransverse => Rotation == 90 || Rotation == 270;

    // Natural bitmap dimensions — always portrait/landscape as the PDF page.
    // The content Grid in PdfPageControl uses these for sizing (it is then
    // visually rotated via LayoutTransform, making WPF see it as DisplayWidth×DisplayHeight).
    public double BitmapWidth  => WidthPt  * Scale;
    public double BitmapHeight => HeightPt * Scale;

    // Post-rotation display dimensions: width/height swap at 90°/270°.
    public double DisplayWidth  => IsTransverse ? HeightPt * Scale : WidthPt  * Scale;
    public double DisplayHeight => IsTransverse ? WidthPt  * Scale : HeightPt * Scale;

    // ControlWidth/Height = rotated page content + 8 px shadow margin on each side.
    public double ControlWidth  => DisplayWidth  + 16;
    public double ControlHeight => DisplayHeight + 16;

    [ObservableProperty]
    private RenderedPage? _renderedPage;

    // True while the existing RenderedPage is from a superseded scale and a fresh
    // render is queued. The View stretches the stale bitmap to fill the new container
    // so the user always sees content — no blank flash — while re-rendering.
    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private bool _isRendering;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private IReadOnlyList<HighlightRect> _searchHighlights = [];

    [ObservableProperty]
    private IReadOnlyList<HighlightRect> _selectionHighlights = [];

    public PageViewModel(int pageIndex, double widthPt, double heightPt,
                         IReadOnlyList<PdfLink>? links = null)
    {
        PageIndex = pageIndex;
        WidthPt   = widthPt;
        HeightPt  = heightPt;
        Links     = links ?? [];
    }
}
