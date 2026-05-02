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

    /// <summary>
    /// Ctrl+Scroll = zoom, plain scroll = pan (default).
    /// </summary>
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
        // Bring the requested page into view by finding its container
        Dispatcher.InvokeAsync(() => ScrollToPage(pageIndex),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm?.IsFitToWidth == true)
            _vm.FitToWidth(MainScroller.ActualWidth);
        else if (_vm?.IsFitToPage == true)
            _vm.FitToPage(MainScroller.ActualHeight);
    }

    private void MainScroller_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (_vm is null || e.VerticalChange == 0) return;
        UpdateCurrentPageFromScroll();
    }

    /// <summary>
    /// Finds the page whose area contains 30 % down the visible viewport and updates
    /// CurrentPageIndex so the toolbar counter and thumbnail selection stay in sync
    /// while the user scrolls manually.
    /// </summary>
    private void UpdateCurrentPageFromScroll()
    {
        if (_vm is null || _vm.Pages.Count == 0) return;

        // 30 % from the top biases towards "the page you are reading" rather than
        // a partially visible page at the bottom edge of the viewport.
        double viewportAnchor = MainScroller.VerticalOffset + MainScroller.ViewportHeight * 0.3;
        double cumulative = 0;
        int detected = _vm.Pages.Count - 1;

        for (int i = 0; i < _vm.Pages.Count; i++)
        {
            // ControlHeight already includes the 8 px shadow margin on each side.
            // DataTemplate adds Margin="0,4" → 8 px total per page.
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

    private void ScrollToPage(int pageIndex)
    {
        if (PageList.ItemContainerGenerator.ContainerFromIndex(pageIndex) is FrameworkElement container)
        {
            container.BringIntoView();
            return;
        }

        // If the container isn't realized yet (virtualized), scroll by estimated offset
        if (_vm is null || _vm.Pages.Count == 0) return;
        double itemHeight = MainScroller.ScrollableHeight / Math.Max(1, _vm.Pages.Count - 1);
        MainScroller.ScrollToVerticalOffset(pageIndex * itemHeight);
    }
}
