using EasyPDF.Application.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = ViewerVm.OnPageBecameVisibleAsync(page.PageIndex, dpi.DpiScaleX, _cts.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_boundPage is not null)
            _boundPage.PropertyChanged -= OnPagePropertyChanged;

        _cts?.Cancel();
        _cts?.Dispose();
    }
}
