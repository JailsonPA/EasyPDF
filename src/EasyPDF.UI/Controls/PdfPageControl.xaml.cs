using EasyPDF.Application.ViewModels;
using EasyPDF.Core.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EasyPDF.UI.Controls;

/// <summary>
/// Displays one PDF page. Rendering is triggered when:
///   - The control becomes visible (IsVisibleChanged → true), OR
///   - ViewerVm is set while the control is already visible (RelativeSource bindings resolve
///     after the element enters the visual tree, so this fallback is critical), OR
///   - RenderedPage is cleared (e.g. after a zoom change) while still visible.
/// </summary>
public partial class PdfPageControl : UserControl
{
    // PropertyChangedCallback ensures TriggerRender fires even when ViewerVm
    // resolves after IsVisibleChanged (typical for RelativeSource bindings).
    public static readonly DependencyProperty ViewerVmProperty =
        DependencyProperty.Register(
            nameof(ViewerVm),
            typeof(PdfViewerViewModel),
            typeof(PdfPageControl),
            new PropertyMetadata(null, OnViewerVmChanged));

    private static void OnViewerVmChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PdfPageControl ctrl && e.NewValue is not null && ctrl.IsVisible)
            ctrl.TriggerRender();
    }

    public PdfViewerViewModel? ViewerVm
    {
        get => (PdfViewerViewModel?)GetValue(ViewerVmProperty);
        set => SetValue(ViewerVmProperty, value);
    }

    private CancellationTokenSource? _cts;
    private PageViewModel? _boundPage;

    // Text selection state
    private System.Windows.Point _selectionStartPdf; // in PDF point coordinates
    private bool _isSelecting;
    private CancellationTokenSource? _extractCts;

    public PdfPageControl()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundPage is not null)
            _boundPage.PropertyChanged -= OnPagePropertyChanged;

        _boundPage = e.NewValue as PageViewModel;

        if (_boundPage is not null)
        {
            _boundPage.PropertyChanged += OnPagePropertyChanged;
            // Recycled containers may miss the IsStale/RenderedPage=null PropertyChanged that fires
            // during a scale change — if the DataContext swap races with OnScaleChanged.
            // Trigger here so the page always renders when a control is assigned to it.
            if ((_boundPage.RenderedPage is null || _boundPage.IsStale) && IsVisible)
                TriggerRender();
        }
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsVisible || _boundPage is null) return;

        // Re-render when the bitmap is cleared (RenderedPage=null) or marked stale (IsStale=true).
        if (e.PropertyName == nameof(PageViewModel.RenderedPage) && _boundPage.RenderedPage is null)
            TriggerRender();
        else if (e.PropertyName == nameof(PageViewModel.IsStale) && _boundPage.IsStale)
            TriggerRender();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            TriggerRender();
        else
            _cts?.Cancel();
    }

    private void TriggerRender()
    {
        // ViewerVm may still be null if the RelativeSource binding hasn't resolved yet.
        // OnViewerVmChanged covers that case — no action needed here.
        if (ViewerVm is null || DataContext is not PageViewModel page) return;

        var dpi = VisualTreeHelper.GetDpi(this);

        // Render at exactly the screen's physical pixel density so the bitmap maps
        // 1:1 to physical pixels — WPF performs no downscale and applies no blur.
        // MuPDF AA=8 at the native scale is the sole anti-aliasing step, which
        // gives sharper text than oversampling + Fant-downscaling ever could.
        //
        // physicalScale = Scale × dpiScaleX  →  BitmapSource DPI = 96×dpiScaleX
        // WPF logical size = pixels ÷ dpiScaleX = WidthPt×Scale  ✓ (no WPF rescaling)
        //
        // Cap at 6× (~72 MB for A4) to cover common high-DPI scenarios without
        // upscaling: 150% DPI × Scale≈3.2 (full-HD maximised) and 200% DPI × Scale≈3.0
        // both stay within 6× and remain 1:1. Only extreme zoom on 200%+ DPI screens
        // slightly exceeds the cap and WPF upscales via Fant — acceptable at that zoom.
        const double MaxPhysicalScale = 6.0;
        double renderDpiScale = Math.Min(dpi.DpiScaleX, MaxPhysicalScale / Math.Max(ViewerVm.Scale, 0.001));

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = ViewerVm.OnPageBecameVisibleAsync(page.PageIndex, renderDpiScale, _cts.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_boundPage is not null)
            _boundPage.PropertyChanged -= OnPagePropertyChanged;

        _cts?.Cancel();
        _cts?.Dispose();
        _extractCts?.Cancel();
        _extractCts?.Dispose();
    }

    // ─── Link support ──────────────────────────────────────────────────────────

    private static PdfLink? HitTestLink(PageViewModel page, System.Windows.Point imagePos)
    {
        double pdfX = imagePos.X / page.Scale;
        double pdfY = imagePos.Y / page.Scale;
        foreach (var link in page.Links)
        {
            var a = link.Area;
            if (pdfX >= a.X && pdfX <= a.X + a.Width &&
                pdfY >= a.Y && pdfY <= a.Y + a.Height)
                return link;
        }
        return null;
    }

    private void ActivateLink(PdfLink link)
    {
        switch (link.Destination)
        {
            case PdfLinkDestination.Internal dest:
                ViewerVm?.GoToPageCommand.Execute(dest.PageIndex);
                break;
            case PdfLinkDestination.External dest when !string.IsNullOrEmpty(dest.Uri):
                try { Process.Start(new ProcessStartInfo(dest.Uri) { UseShellExecute = true }); }
                catch { }
                break;
        }
    }

    // ─── Text selection ────────────────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ViewerVm is null || DataContext is not PageViewModel page) return;

        var pos = e.GetPosition(PageImage);
        if (pos.X < 0 || pos.Y < 0 || pos.X > page.DisplayWidth || pos.Y > page.DisplayHeight) return;

        // Link click takes priority over text selection.
        var link = HitTestLink(page, pos);
        if (link is not null)
        {
            ActivateLink(link.Value);
            e.Handled = true;
            return;
        }

        ViewerVm.ClearSelection();
        _selectionStartPdf = new System.Windows.Point(pos.X / page.Scale, pos.Y / page.Scale);
        _isSelecting = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (DataContext is not PageViewModel page) return;

        var pos = e.GetPosition(PageImage);

        if (!_isSelecting)
        {
            Cursor = HitTestLink(page, pos) is not null ? Cursors.Hand : Cursors.IBeam;
            return;
        }

        double ex = Math.Clamp(pos.X / page.Scale, 0, page.WidthPt);
        double ey = Math.Clamp(pos.Y / page.Scale, 0, page.HeightPt);

        // Live bounding-box feedback while dragging
        double rx = Math.Min(_selectionStartPdf.X, ex) * page.Scale;
        double ry = Math.Min(_selectionStartPdf.Y, ey) * page.Scale;
        double rw = Math.Abs(ex - _selectionStartPdf.X) * page.Scale;
        double rh = Math.Abs(ey - _selectionStartPdf.Y) * page.Scale;

        page.SelectionHighlights = [new HighlightRect(rx, ry, rw, rh, false)];
    }

    protected override async void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isSelecting || DataContext is not PageViewModel page || ViewerVm is null) return;

        _isSelecting = false;
        ReleaseMouseCapture();

        var pos  = e.GetPosition(PageImage);
        double ex = Math.Clamp(pos.X / page.Scale, 0, page.WidthPt);
        double ey = Math.Clamp(pos.Y / page.Scale, 0, page.HeightPt);

        // Tiny movement = click without drag → clear selection
        if (Math.Abs(ex - _selectionStartPdf.X) < 3 && Math.Abs(ey - _selectionStartPdf.Y) < 3)
        {
            page.SelectionHighlights = [];
            ViewerVm.ClearSelection();
            e.Handled = true;
            return;
        }

        _extractCts?.Cancel();
        _extractCts = new CancellationTokenSource();

        await ViewerVm.ExtractSelectionAsync(
            page,
            (float)_selectionStartPdf.X, (float)_selectionStartPdf.Y,
            (float)ex, (float)ey,
            _extractCts.Token);

        e.Handled = true;
    }
}
