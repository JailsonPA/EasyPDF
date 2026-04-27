using EasyPDF.Application.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace EasyPDF.UI.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    protected override async void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        await _vm.InitializeAsync();
    }

    // ─── Custom window chrome ───────────────────────────────────────────────

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e) =>
        ToggleMaximize();

    private void CloseClick(object sender, RoutedEventArgs e) =>
        Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

        MaxRestoreButton.Content = WindowState == WindowState.Maximized
            ? "\xE923"   // Restore icon (Segoe MDL2)
            : "\xE922";  // Maximize icon
    }

    // ─── Keyboard shortcuts ─────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var vm = _vm.Viewer;

        switch (e.Key)
        {
            case Key.Left or Key.PageUp when _vm.HasDocument:
                vm.PreviousPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right or Key.PageDown when _vm.HasDocument:
                vm.NextPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Home when _vm.HasDocument:
                vm.FirstPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.End when _vm.HasDocument:
                vm.LastPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                _vm.OpenFileCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                _vm.ToggleSearchCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.B when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasDocument:
                _vm.AddBookmarkCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Add or Key.OemPlus when Keyboard.Modifiers == ModifierKeys.Control:
                vm.ZoomInCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Subtract or Key.OemMinus when Keyboard.Modifiers == ModifierKeys.Control:
                vm.ZoomOutCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0 when Keyboard.Modifiers == ModifierKeys.Control:
                vm.ResetZoomCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ─── Input box helpers ──────────────────────────────────────────────────

    private void PageInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is System.Windows.Controls.TextBox tb &&
            int.TryParse(tb.Text, out int page))
        {
            _vm.Viewer.GoToPageCommand.Execute(page - 1);
        }
        e.Handled = true;
    }

    private void ZoomInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is System.Windows.Controls.TextBox tb)
        {
            string raw = tb.Text.TrimEnd('%').Trim();
            if (double.TryParse(raw, out double pct))
                _vm.Viewer.ZoomToCommand.Execute(pct);
        }
        e.Handled = true;
    }

    private void SearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.Search.SearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.ToggleSearchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
