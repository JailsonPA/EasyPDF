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

    public ThumbnailControl()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded += OnUnloaded;
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

    private void OnUnloaded(object sender, RoutedEventArgs e) => CancelAndDisposeCts();

    private void CancelAndDisposeCts()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }
}
