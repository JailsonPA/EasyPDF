using EasyPDF.Core.Interfaces;
using Microsoft.Win32;
using System.Windows;

namespace EasyPDF.UI.Services;

public sealed class WpfDialogService : IDialogService
{
    public Task<string?> OpenPdfFileAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open PDF",
            Filter = "PDF Files (*.pdf)|*.pdf|All Files (*.*)|*.*",
            DefaultExt = ".pdf"
        };
        return Task.FromResult(dlg.ShowDialog() == true ? dlg.FileName : null);
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        var result = MessageBox.Show(message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public Task ShowErrorAsync(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }

    public Task<string?> PromptAsync(string title, string hint, string defaultValue = "")
    {
        // A simple prompt using a dedicated small window would be ideal;
        // for now, InputBox via interaction is sufficient.
        var window = new PromptWindow(title, hint, defaultValue);
        bool? result = window.ShowDialog();
        return Task.FromResult(result == true ? window.Value : null);
    }
}
