using EasyPDF.Application.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EasyPDF.UI.Views;

public partial class PdfViewerView : UserControl
{
    private PdfViewerViewModel? _vm;

    public PdfViewerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SizeChanged += OnSizeChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ScrollToPageRequested -= OnScrollToPageRequested;
            _vm.PropertyChanged       -= OnViewerPropertyChanged;
            _vm.FitToWidthRequested   -= OnFitToWidthRequested;
            _vm.FitToPageRequested    -= OnFitToPageRequested;
        }

        _vm = e.NewValue as PdfViewerViewModel;

        if (_vm is not null)
        {
            _vm.ScrollToPageRequested += OnScrollToPageRequested;
            _vm.PropertyChanged       += OnViewerPropertyChanged;
            _vm.FitToWidthRequested   += OnFitToWidthRequested;
            _vm.FitToPageRequested    += OnFitToPageRequested;

            if (_vm.Pages.Count > 0)
                Dispatcher.InvokeAsync(() => ScrollToPage(_vm.CurrentPageIndex),
                    System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    private void OnFitToWidthRequested(object? sender, EventArgs e)
    {
        if (_vm is not null && MainScroller.ActualWidth > 0)
            _vm.FitToWidth(MainScroller.ActualWidth);
    }

    private void OnFitToPageRequested(object? sender, EventArgs e)
    {
        if (_vm is not null && MainScroller.ActualHeight > 0)
            _vm.FitToPage(MainScroller.ActualHeight);
    }

    private void OnViewerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfViewerViewModel.IsFitToWidth) && _vm?.IsFitToWidth == true)
            _vm.FitToWidth(MainScroller.ActualWidth);
        else if (e.PropertyName == nameof(PdfViewerViewModel.IsFitToPage) && _vm?.IsFitToPage == true)
            _vm.FitToPage(MainScroller.ActualHeight);
    }

  
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && _vm is not null)
        {
            e.Handled = true;
            _vm.HandleMouseWheelZoom(e.Delta > 0, e.Delta);
        }
        base.OnPreviewMouseWheel(e);
    }

    private void OnScrollToPageRequested(object? sender, int pageIndex)
    {
        Dispatcher.InvokeAsync(() => ScrollToPage(pageIndex),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm?.IsFitToWidth == true)
            _vm.FitToWidth(MainScroller.ActualWidth);
        else if (_vm?.IsFitToPage == true)
            _vm.FitToPage(MainScroller.ActualHeight);

        PushViewportToSidebar();
    }

    private void MainScroller_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (_vm is null || e.VerticalChange == 0) return;
        UpdateCurrentPageFromScroll();
        PushViewportToSidebar();
    }


    private void UpdateCurrentPageFromScroll()
    {
        if (_vm is null || _vm.Pages.Count == 0) return;

      
        double viewportAnchor = MainScroller.VerticalOffset + MainScroller.ViewportHeight * 0.3;
        double cumulative = 0;
        int detected = _vm.Pages.Count - 1;

        for (int i = 0; i < _vm.Pages.Count; i++)
        {
           
            double pageHeight = _vm.Pages[i].ControlHeight + 8;
            cumulative += pageHeight;
            if (cumulative > viewportAnchor)
            {
                detected = i;
                break;
            }
        }

        if (detected != _vm.CurrentPageIndex)
            _vm.CurrentPageIndex = detected;
    }

    private void PushViewportToSidebar()
    {
        if (_vm is null || _vm.Pages.Count == 0) return;
        int idx = _vm.CurrentPageIndex;
        if (idx < 0 || idx >= _vm.Pages.Count) return;

        // Accumulate vertical offset to the top of this page's item slot.
        // Each item is allocated (ControlHeight + 8): 4px top + ControlHeight + 4px bottom.
        double accumulated = 0;
        for (int i = 0; i < idx; i++)
            accumulated += _vm.Pages[i].ControlHeight + 8;

        var page = _vm.Pages[idx];
        // The page image sits inside a Border with 8px padding (ControlHeight = DisplayHeight + 16).
        // Item top margin = 4, Border padding = 8 → image starts 12px below the item slot top.
        double imageTop    = accumulated + 12;
        double imageHeight = page.DisplayHeight;
        if (imageHeight <= 0) return;

        double viewTop    = MainScroller.VerticalOffset;
        double viewBottom = viewTop + MainScroller.ViewportHeight;

        double visTop    = Math.Max(viewTop,    imageTop)             - imageTop;
        double visHeight = Math.Max(0, Math.Min(viewBottom, imageTop + imageHeight) - Math.Max(viewTop, imageTop));

        double topFrac    = Math.Clamp(visTop    / imageHeight, 0, 1);
        double heightFrac = Math.Clamp(visHeight / imageHeight, 0, 1);

        _vm.SetViewport(topFrac, heightFrac);
    }

    private void ScrollToPage(int pageIndex)
    {
        if (PageList.ItemContainerGenerator.ContainerFromIndex(pageIndex) is FrameworkElement container)
        {
            container.BringIntoView();
            return;
        }

        if (_vm is null || _vm.Pages.Count == 0) return;
        double itemHeight = MainScroller.ScrollableHeight / Math.Max(1, _vm.Pages.Count - 1);
        MainScroller.ScrollToVerticalOffset(pageIndex * itemHeight);
    }
}
