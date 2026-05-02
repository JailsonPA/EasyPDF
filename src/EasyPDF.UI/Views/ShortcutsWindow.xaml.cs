using System.Windows;
using System.Windows.Input;

namespace EasyPDF.UI.Views;

public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseClick(object sender, RoutedEventArgs e) =>
        Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Escape or Key.F1)
        {
            Close();
            e.Handled = true;
        }
    }
}
