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
            _vm.PropertyChanged      -= OnViewerPropertyChanged;
            _vm.FitToWidthRequested  -= OnFitToWidthRequested;
        }

        _vm = e.NewValue as PdfViewerViewModel;

        if (_vm is not null)
        {
            _vm.ScrollToPageRequested += OnScrollToPageRequested;
            _vm.PropertyChanged       += OnViewerPropertyChanged;
            _vm.FitToWidthRequested   += OnFitToWidthRequested;
        }
    }

    private void OnFitToWidthRequested(object? sender, EventArgs e)
    {
        if (_vm is not null && MainScroller.ActualWidth > 0)
            _vm.FitToWidth(MainScroller.ActualWidth);
    }

    // Applies FitToWidth immediately when the command activates it, without
    // waiting for the next SizeChanged event.
    private void OnViewerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfViewerViewModel.IsFitToWidth) && _vm?.IsFitToWidth == true)
            _vm.FitToWidth(MainScroller.ActualWidth);
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
