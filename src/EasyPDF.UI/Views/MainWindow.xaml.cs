using EasyPDF.Application.ViewModels;
using EasyPDF.UI.Services;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace EasyPDF.UI.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private HwndSource? _hwndSource;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    protected override async void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);
        ApplySavedPlacement();
        await _vm.InitializeAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        SavePlacement();
        _ = _vm.SaveLastPageAsync(); // fire-and-forget: persists last page on quit
        base.OnClosed(e);
    }

    // ─── Window placement persistence ──────────────────────────────────────────

    private void ApplySavedPlacement()
    {
        var p = WindowSettingsService.Load();
        if (p is null) return;

        Width  = Math.Clamp(p.Width,  MinWidth,  SystemParameters.VirtualScreenWidth);
        Height = Math.Clamp(p.Height, MinHeight, SystemParameters.VirtualScreenHeight);

        // Only reposition if the saved origin is still reachable on the current screen set.
        double vLeft   = SystemParameters.VirtualScreenLeft;
        double vTop    = SystemParameters.VirtualScreenTop;
        double vRight  = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop  + SystemParameters.VirtualScreenHeight;

        if (p.Left + 100 < vRight  && p.Left > vLeft - Width &&
            p.Top  + 32  < vBottom && p.Top  > vTop  - 32)
        {
            Left = p.Left;
            Top  = p.Top;
        }

        if (p.State == "Maximized")
        {
            WindowState = WindowState.Maximized;
            UpdateMaxRestoreIcon();
        }
    }

    private void SavePlacement()
    {
        Rect bounds;
        string state;

        switch (WindowState)
        {
            case WindowState.Maximized:
                bounds = RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                state  = "Maximized";
                break;
            case WindowState.Minimized:
                // Don't restore the app as minimised — use the pre-minimise bounds.
                bounds = RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                state  = "Normal";
                break;
            default:
                bounds = new Rect(Left, Top, Width, Height);
                state  = "Normal";
                break;
        }

        if (bounds.Width < 1 || bounds.Height < 1) return;

        WindowSettingsService.SaveAsync(new WindowPlacement
        {
            Left   = bounds.Left,
            Top    = bounds.Top,
            Width  = bounds.Width,
            Height = bounds.Height,
            State  = state,
        });
    }

    // ─── Windows 11 Snap Layout support ────────────────────────────────────────
    // With WindowStyle="None" the OS has no maximize button to detect, so the
    // Snap Layout popup never appears on hover. We intercept WM_NCHITTEST and
    // return HTMAXBUTTON when the cursor is over MaxRestoreButton, which tells
    // the OS to treat that region as the native maximize button.

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_NCHITTEST && MaxRestoreButton.ActualWidth > 0)
        {
            // lParam packs screen coordinates as signed 16-bit words.
            int screenX = unchecked((short)(lParam.ToInt32() & 0xFFFF));
            int screenY = unchecked((short)((lParam.ToInt32() >> 16) & 0xFFFF));
            var cursorScreen = new Point(screenX, screenY);

            // Convert both corners via PointToScreen so the Rect is in physical
            // pixels — same coordinate space as the lParam cursor position.
            // Using PointToScreen for both points is DPI-correct at any scale.
            var topLeft     = MaxRestoreButton.PointToScreen(new Point(0, 0));
            var bottomRight = MaxRestoreButton.PointToScreen(
                new Point(MaxRestoreButton.ActualWidth, MaxRestoreButton.ActualHeight));

            if (new Rect(topLeft, bottomRight).Contains(cursorScreen))
            {
                handled = true;
                return new IntPtr(NativeMethods.HTMAXBUTTON);
            }
        }
        return IntPtr.Zero;
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
        UpdateMaxRestoreIcon();
    }

    private void UpdateMaxRestoreIcon()
    {
        MaxRestoreButton.Content = WindowState == WindowState.Maximized
            ? "\xE923"   // Restore icon (Segoe MDL2 Assets)
            : "\xE922";  // Maximize icon
    }

    // ─── Keyboard shortcuts ─────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var viewer = _vm.Viewer;
        // Suppress navigation keys when the user is typing inside a TextBox.
        bool inText = Keyboard.FocusedElement is System.Windows.Controls.TextBox;

        switch (e.Key)
        {
            // ── Page navigation (not when typing) ─────────────────────────
            case Key.Left or Key.PageUp when _vm.HasDocument && !inText:
                viewer.PreviousPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right or Key.PageDown when _vm.HasDocument && !inText:
                viewer.NextPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Home when _vm.HasDocument && !inText:
                viewer.FirstPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.End when _vm.HasDocument && !inText:
                viewer.LastPageCommand.Execute(null);
                e.Handled = true;
                break;

            // ── Dismiss search panel (SearchBoxKeyDown handles the in-box case) ──
            case Key.Escape when _vm.IsSearchPanelOpen && !inText:
                _vm.ToggleSearchCommand.Execute(null);
                e.Handled = true;
                break;

            // ── File ──────────────────────────────────────────────────────
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                _vm.OpenFileCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.W when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasDocument:
                _vm.CloseDocumentCommand.Execute(null);
                e.Handled = true;
                break;

            // ── Search ────────────────────────────────────────────────────
            // Ctrl+F: open the panel if closed, then always focus the search box.
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasDocument:
                if (!_vm.IsSearchPanelOpen)
                    _vm.IsSearchPanelOpen = true;
                Dispatcher.InvokeAsync(() => SearchBox.Focus(),
                    System.Windows.Threading.DispatcherPriority.Input);
                e.Handled = true;
                break;
            // F3 / Shift+F3: cycle through existing results without reopening search.
            case Key.F3 when _vm.Search.HasResults:
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    _vm.Search.PreviousResultCommand.Execute(null);
                else
                    _vm.Search.NextResultCommand.Execute(null);
                e.Handled = true;
                break;

            // ── Go to page ────────────────────────────────────────────────
            case Key.G when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasDocument:
                Dispatcher.InvokeAsync(() => { PageInputBox.Focus(); PageInputBox.SelectAll(); },
                    System.Windows.Threading.DispatcherPriority.Input);
                e.Handled = true;
                break;

            // ── Bookmarks ─────────────────────────────────────────────────
            case Key.B when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasDocument:
                _vm.AddBookmarkCommand.Execute(null);
                e.Handled = true;
                break;

            // ── Print ─────────────────────────────────────────────────────
            case Key.P when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasDocument:
                _vm.PrintCommand.Execute(null);
                e.Handled = true;
                break;

            // ── Page rotation ─────────────────────────────────────────────
            case Key.OemOpenBrackets when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasDocument:
                _vm.Viewer.RotateCurrentPageCounterClockwiseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemCloseBrackets when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasDocument:
                _vm.Viewer.RotateCurrentPageClockwiseCommand.Execute(null);
                e.Handled = true;
                break;

            // ── Copy selected text ────────────────────────────────────────
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                if (!string.IsNullOrEmpty(_vm.Viewer.SelectedText))
                {
                    System.Windows.Clipboard.SetText(_vm.Viewer.SelectedText);
                    e.Handled = true;
                }
                break;

            // ── Zoom ──────────────────────────────────────────────────────
            case Key.Add or Key.OemPlus when Keyboard.Modifiers == ModifierKeys.Control:
                viewer.ZoomInCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Subtract or Key.OemMinus when Keyboard.Modifiers == ModifierKeys.Control:
                viewer.ZoomOutCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0 when Keyboard.Modifiers == ModifierKeys.Control:
                viewer.ResetZoomCommand.Execute(null);
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
