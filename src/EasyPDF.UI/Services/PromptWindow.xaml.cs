using System.Windows;
using System.Windows.Input;

namespace EasyPDF.UI.Services;

public partial class PromptWindow : Window
{
    public string Value => InputBox.Text;

    public PromptWindow(string title, string hint, string defaultValue)
    {
        InitializeComponent();
        TitleText.Text = title;
        HintText.Text = hint;
        InputBox.Text = defaultValue;
        InputBox.SelectAll();
        Loaded += (_, _) => InputBox.Focus();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OK_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { DialogResult = true; Close(); }
        if (e.Key == Key.Escape) { DialogResult = false; Close(); }
    }
}
