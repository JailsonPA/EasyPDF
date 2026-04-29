using System.Windows;
using System.Windows.Input;

namespace EasyPDF.UI.Services;

public partial class PasswordWindow : Window
{
    public string Password => PwdBox.Password;

    public PasswordWindow(string fileName, string? errorMessage)
    {
        InitializeComponent();
        MessageText.Text = $"‘{fileName}’ is password-protected. Enter the password to open it.";
        if (errorMessage is not null)
        {
            ErrorText.Text = errorMessage;
            ErrorText.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => PwdBox.Focus();
    }

    private void OK_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { DialogResult = true; Close(); }
        if (e.Key == Key.Escape) { DialogResult = false; Close(); }
    }
}
