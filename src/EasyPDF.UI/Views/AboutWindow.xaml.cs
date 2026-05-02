using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace EasyPDF.UI.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var ver = Assembly.GetExecutingAssembly().GetName().Version!;
        VersionText.Text    = $"Version {ver.Major}.{ver.Minor}.{ver.Build}";
        CopyrightText.Text  = $"© {DateTime.Now.Year} jailsonprazeres.com";
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
