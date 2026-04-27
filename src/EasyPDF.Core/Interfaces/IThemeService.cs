using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface IThemeService
{
    AppTheme CurrentTheme { get; }
    event EventHandler<AppTheme> ThemeChanged;
    void ApplyTheme(AppTheme theme);
    void LoadSaved();
}
