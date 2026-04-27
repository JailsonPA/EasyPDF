using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using EasyPDF.Infrastructure.Storage;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace EasyPDF.UI.Services;

public sealed class WpfThemeService : IThemeService
{
    // AppDataPaths.SettingsFile is the single source of truth for this path.
    private static string SettingsPath => AppDataPaths.SettingsFile;

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;
    public event EventHandler<AppTheme>? ThemeChanged;

    public void LoadSaved()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                    ApplyTheme(settings.Theme);
                return;
            }
        }
        catch { /* use default */ }

        ApplyTheme(AppTheme.Dark);
    }

    public void ApplyTheme(AppTheme theme)
    {
        CurrentTheme = theme;

        var resolved = theme == AppTheme.System ? ResolveSystemTheme() : theme;
        var uri = resolved == AppTheme.Dark
            ? new Uri("/EasyPDF;component/Themes/DarkTheme.xaml", UriKind.Relative)
            : new Uri("/EasyPDF;component/Themes/LightTheme.xaml", UriKind.Relative);

        var dicts = System.Windows.Application.Current.Resources.MergedDictionaries;

        // Replace only the colour palette dict; keep ControlStyles
        var existing = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Theme.xaml") == true);

        var newDict = new ResourceDictionary { Source = uri };

        if (existing is not null)
        {
            int idx = dicts.IndexOf(existing);
            dicts[idx] = newDict;
        }
        else
        {
            dicts.Insert(0, newDict);
        }

        PersistAsync(theme);
        ThemeChanged?.Invoke(this, theme);
    }

    private static AppTheme ResolveSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            int? value = key?.GetValue("AppsUseLightTheme") as int?;
            return value == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch { return AppTheme.Dark; }
    }

    private static async void PersistAsync(AppTheme theme)
    {
        try
        {
            // AppDataPaths.SettingsFile already ensures the directory exists.
            await File.WriteAllTextAsync(SettingsPath,
                JsonSerializer.Serialize(new AppSettings(theme)));
        }
        catch { /* non-critical */ }
    }

    private sealed record AppSettings(AppTheme Theme);
}
