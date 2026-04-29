namespace EasyPDF.Core.Interfaces;

public interface IDialogService
{
    Task<string?> OpenPdfFileAsync();
    Task<bool> ConfirmAsync(string title, string message);
    Task ShowErrorAsync(string title, string message);
    Task<string?> PromptAsync(string title, string hint, string defaultValue = "");
    Task<string?> PromptPasswordAsync(string fileName, string? errorMessage = null);
}
