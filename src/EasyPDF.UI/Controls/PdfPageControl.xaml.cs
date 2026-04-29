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
            // Recycled containers may miss the RenderedPage=null PropertyChanged that fires
            // during a scale change — if the DataContext swap races with OnScaleChanged.
            // Trigger here so the page always renders when a control is assigned to it.
            if (_boundPage.RenderedPage is null && IsVisible)
                TriggerRender();
        }
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // When zoom changes, PdfViewerViewModel sets RenderedPage = null.
        // Re-render immediately if this control is still visible.
        if (e.PropertyName == nameof(PageViewModel.RenderedPage)
            && _boundPage?.RenderedPage is null
            && IsVisible)
        {
            TriggerRender();
        }
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

        // Always render at 2× oversampling relative to the current zoom level.
        // MuPDF produces a bitmap 2× wider/taller than what the screen needs;
        // WPF bicubic-downsamples it to the logical display size, giving crisp
        // edges on both text and embedded images at any zoom.
        //
        // Why 2×: at 1× (native), effective PDF DPI = Scale×72. At typical Fit-to-Width
        // scales (~1.5×), that is only 108 DPI — visibly soft for embedded photos and
        // fine text. At 2× oversampling, effective DPI = Scale×72×2 ≥ 216 — comparable
        // to a 200 DPI print and indistinguishable from the source document on screen.
        //
        // Layout is unaffected: BitmapSource.dpiX = 96×renderDpiScale, so Stretch="None"
        // still displays at exactly BitmapWidth×BitmapHeight logical pixels.
        //
        // Cap physicalScale at 4× to keep per-page memory ≤ ~32 MB (A4 at 4× = 32 MP).
        // At zoom levels where Scale itself already exceeds 4×, no boost is needed.
        const double OversampleFactor = 2.0;
        const double MaxPhysicalScale = 4.0;
        double boost = Math.Min(OversampleFactor, MaxPhysicalScale / Math.Max(ViewerVm.Scale, 0.01));
        double renderDpiScale = Math.Max(dpi.DpiScaleX, boost);

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
