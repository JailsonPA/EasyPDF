using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

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
    private readonly ILogger<PdfViewerViewModel> _logger;
    private readonly Dictionary<int, CancellationTokenSource> _pendingRenders = new();
    private bool _applyingFitToWidth;

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

    // Pages exposed to the View; the View's ItemsControl virtualizes rendering.
    // BulkObservableCollection fires a single Reset instead of N Add notifications.
    public BulkObservableCollection<PageViewModel> Pages { get; } = new();

    public string CurrentPageDisplay => $"{CurrentPageIndex + 1} / {PageCount}";
    public string ZoomPercent => $"{Scale * 100:F0}%";
    public bool CanGoBack => CurrentPageIndex > 0;
    public bool CanGoForward => CurrentPageIndex < PageCount - 1;

    public event EventHandler<int>? ScrollToPageRequested;

    public PdfViewerViewModel(IPdfRenderService renderService, ILogger<PdfViewerViewModel> logger)
    {
        _renderService = renderService;
        _logger = logger;
    }

    public void LoadDocument(PdfDocument document)
    {
        var vms = document.Pages.Select(p =>
        {
            var vm = new PageViewModel(p.Index, p.WidthPt, p.HeightPt);
            vm.Scale = Scale;
            return vm;
        });
        Pages.ReplaceAll(vms); // 1 Reset notification instead of N Add notifications

        PageCount = document.PageCount;
        CurrentPageIndex = 0;
        IsLoading = false;
        ErrorMessage = null;
    }

    public void Clear()
    {
        CancelAllPendingRenders();
        Pages.Clear();
        PageCount = 0;
        CurrentPageIndex = 0;
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
        if (pageVm.RenderedPage is not null) return; // already rendered at this scale

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
            pageVm.IsRendering = true;
            var rendered = await _renderService.RenderPageAsync(pageIndex, Scale, dpiScale, cts.Token);
            pageVm.RenderedPage = rendered;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Render failed for page {Page}", pageIndex);
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
            Scale = Math.Clamp((viewportWidth - 20) / firstPage.WidthPt, MinScale, MaxScale);
            IsFitToWidth = true;
        }
        finally
        {
            _applyingFitToWidth = false;
        }
    }

    /// <summary>
    /// Invalidates rendered bitmaps when zoom changes so pages re-render at the new resolution.
    /// </summary>
    partial void OnScaleChanged(double value)
    {
        if (!_applyingFitToWidth)
            IsFitToWidth = false;
        CancelAllPendingRenders();
        foreach (var p in Pages)
        {
            p.Scale = value;
            p.RenderedPage = null;
        }
    }

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
    private void ZoomIn() => Scale = Math.Min(MaxScale, Scale + ZoomStep);

    [RelayCommand]
    private void ZoomOut() => Scale = Math.Max(MinScale, Scale - ZoomStep);

    [RelayCommand]
    private void ResetZoom() => Scale = DefaultScale;

    [RelayCommand]
    private void ZoomTo(double percent) =>
        Scale = Math.Clamp(percent / 100.0, MinScale, MaxScale);

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayWidth))]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    private double _scale = 1.5;

    public double DisplayWidth => WidthPt * Scale;
    public double DisplayHeight => HeightPt * Scale;

    [ObservableProperty]
    private RenderedPage? _renderedPage;

    [ObservableProperty]
    private bool _isRendering;

    [ObservableProperty]
    private bool _hasError;

    public PageViewModel(int pageIndex, double widthPt, double heightPt)
    {
        PageIndex = pageIndex;
        WidthPt = widthPt;
        HeightPt = heightPt;
    }
}
