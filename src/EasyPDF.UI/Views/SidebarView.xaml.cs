using EasyPDF.Application.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EasyPDF.UI.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private SidebarViewModel? _vm;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SidebarViewModel vm) return;
        _vm = vm;
        _vm.ScrollIntoViewRequested += OnScrollIntoViewRequested;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
            _vm.ScrollIntoViewRequested -= OnScrollIntoViewRequested;
    }

    private void OnScrollIntoViewRequested(object? sender, ThumbnailItemViewModel thumb)
    {
        if (_vm?.ActiveTab != SidebarTab.Thumbnails) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            () => ThumbnailList.ScrollIntoView(thumb));
    }
}
