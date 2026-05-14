using EasyPDF.Application.ViewModels;
using EasyPDF.Core.Models;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EasyPDF.UI.Views;

public partial class PrintPreviewWindow : Window
{
    private readonly PrintPreviewViewModel _vm;
    private CancellationTokenSource? _previewCts;

    public PrintPreviewWindow(PrintPreviewViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.CloseRequested += OnCloseRequested;
        vm.PropertyChanged += OnVmPropertyChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        DialogResult = _vm.PrintConfirmed;
        Close();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await SchedulePreviewRefreshAsync();
    }

    /// Any setting change that affects the preview triggers a re-render.
    /// Cancellation token replaces in-flight renders so we don't show a stale page.
    private async void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PrintPreviewViewModel.Range):
            case nameof(PrintPreviewViewModel.CustomRange):
            case nameof(PrintPreviewViewModel.IncludeAnnotations):
            case nameof(PrintPreviewViewModel.PreviewIndexWithinSelection):
                await SchedulePreviewRefreshAsync();
                break;
        }
    }

    private async Task SchedulePreviewRefreshAsync()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        try
        {
            await _vm.RefreshPreviewAsync(BuildFrozenBitmap, _previewCts.Token);
        }
        catch (OperationCanceledException) { /* superseded */ }
    }

    private static object BuildFrozenBitmap(RenderedPage page)
    {
        var format = page.BitsPerPixel == 32 ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
        double dpi = 96.0 * page.DpiScale;
        var bmp = BitmapSource.Create(
            page.Width, page.Height, dpi, dpi,
            format, null, page.PixelData, page.Stride);
        bmp.Freeze();
        return bmp;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseClick(sender, e); }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Enter triggers Print iff focus isn't inside the custom-range text box.
            if (FocusManager.GetFocusedElement(this) is not System.Windows.Controls.TextBox)
                _vm.ConfirmPrintCommand.Execute(null);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _vm.CloseRequested -= OnCloseRequested;
        _vm.PropertyChanged -= OnVmPropertyChanged;
    }
}
