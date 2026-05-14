using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using System.Windows;

namespace EasyPDF.UI.Services;

public sealed class WpfThemeService : IThemeService
{
    private readonly IPreferencesRepository _prefsRepo;

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;
    public event EventHandler<AppTheme>? ThemeChanged;

    public WpfThemeService(IPreferencesRepository prefsRepo)
    {
        _prefsRepo = prefsRepo;
    }

    public void LoadSaved() => ApplyTheme(_prefsRepo.Get().Theme);

    public void ApplyTheme(AppTheme theme)
    {
        CurrentTheme = theme;

        var resolved = theme == AppTheme.System ? ResolveSystemTheme() : theme;
        var uri = resolved switch
        {
            AppTheme.Dark         => new Uri("/EasyPDF;component/Themes/DarkTheme.xaml",         UriKind.Relative),
            AppTheme.HighContrast => new Uri("/EasyPDF;component/Themes/HighContrastTheme.xaml", UriKind.Relative),
            _                     => new Uri("/EasyPDF;component/Themes/LightTheme.xaml",        UriKind.Relative),
        };

        var dicts = System.Windows.Application.Current.Resources.MergedDictionaries;

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

        // Persist via the central preferences repo so all settings (theme + zoom + ink + ...)
        // share a single file and we never accidentally overwrite each other's keys.
        _ = _prefsRepo.SaveAsync(_prefsRepo.Get() with { Theme = theme });
        ThemeChanged?.Invoke(this, theme);
    }

    private static AppTheme ResolveSystemTheme()
    {
        try
        {
            if (System.Windows.SystemParameters.HighContrast)
                return AppTheme.HighContrast;

            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            int? value = key?.GetValue("AppsUseLightTheme") as int?;
            return value == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch { return AppTheme.Dark; }
    }
}
