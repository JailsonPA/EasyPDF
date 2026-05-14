using EasyPDF.Application.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EasyPDF.UI.Controls;

/// <summary>
/// Displays one thumbnail. Rendering is triggered lazily when the control
/// enters the visible viewport — identical pattern to PdfPageControl.
/// This prevents eager pre-loading of all thumbnails (which starved the main
/// page render semaphore and caused persistent placeholders in the viewer).
/// </summary>
public partial class ThumbnailControl : UserControl
{
    public static readonly DependencyProperty SidebarVmProperty =
        DependencyProperty.Register(
            nameof(SidebarVm),
            typeof(SidebarViewModel),
            typeof(ThumbnailControl),
            new PropertyMetadata(null, OnSidebarVmChanged));

    private static void OnSidebarVmChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailControl ctrl && e.NewValue is not null && ctrl.IsVisible)
            ctrl.TriggerRender();
    }

    public SidebarViewModel? SidebarVm
    {
        get => (SidebarViewModel?)GetValue(SidebarVmProperty);
        set => SetValue(SidebarVmProperty, value);
    }

    private CancellationTokenSource? _cts;
    private ThumbnailItemViewModel? _boundThumb;

    // Fixed thumbnail box size (matches Width/Height on the Grid in XAML).
    private const double BoxWidth  = 140;
    private const double BoxHeight = 180;

    public ThumbnailControl()
    {
        InitializeComponent();
        IsVisibleChanged    += OnIsVisibleChanged;
        DataContextChanged  += OnDataContextChanged;
        Unloaded            += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundThumb is not null)
            _boundThumb.PropertyChanged -= OnThumbPropertyChanged;

        _boundThumb = e.NewValue as ThumbnailItemViewModel;

        if (_boundThumb is not null)
            _boundThumb.PropertyChanged += OnThumbPropertyChanged;

        UpdateViewportOverlay();
    }

    private void OnThumbPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ThumbnailItemViewModel.ViewportTopFrac)
                           or nameof(ThumbnailItemViewModel.ViewportHeightFrac)
                           or nameof(ThumbnailItemViewModel.IsSelected))
            UpdateViewportOverlay();
    }

    private void UpdateViewportOverlay()
    {
        var vm = _boundThumb;
        if (vm is null || !vm.IsSelected || vm.ViewportHeightFrac >= 0.99)
        {
            ViewportOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        // Compute actual rendered image rect within the fixed BoxWidth × BoxHeight,
        // accounting for Stretch="Uniform" letterboxing.
        double pageAspect = (vm.HeightPt > 0 && vm.WidthPt > 0)
            ? vm.WidthPt / vm.HeightPt
            : BoxWidth / BoxHeight;

        double imgH, imgY;
        if (pageAspect < BoxWidth / BoxHeight)
        {
            // Height-constrained (portrait-ish): image fills the full box height.
            imgH = BoxHeight;
            imgY = 0;
        }
        else
        {
            // Width-constrained (landscape-ish): image fills the full box width.
            imgH = BoxWidth / pageAspect;
            imgY = (BoxHeight - imgH) / 2;
        }

        double overlayTop    = imgY + vm.ViewportTopFrac    * imgH;
        double overlayHeight = Math.Max(2, vm.ViewportHeightFrac * imgH);

        ViewportOverlay.Margin     = new Thickness(0, overlayTop, 0, 0);
        ViewportOverlay.Height     = overlayHeight;
        ViewportOverlay.Visibility = Visibility.Visible;
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
        if (SidebarVm is null || DataContext is not ThumbnailItemViewModel thumb) return;
        if (thumb.RenderedPage is not null) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        CancelAndDisposeCts();
        _cts = new CancellationTokenSource();
        _ = SidebarVm.OnThumbnailBecameVisibleAsync(thumb.PageIndex, dpi.DpiScaleX, _cts.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_boundThumb is not null)
            _boundThumb.PropertyChanged -= OnThumbPropertyChanged;
        CancelAndDisposeCts();
    }

    private void CancelAndDisposeCts()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }
}
