using EasyPDF.Application.ViewModels;
using EasyPDF.Core.Interfaces;
using EasyPDF.Infrastructure.Pdf;
using EasyPDF.Infrastructure.Storage;
using EasyPDF.UI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;

namespace EasyPDF.UI;

internal static class ServiceConfiguration
{
    internal static IServiceProvider Build()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        var services = new ServiceCollection();

        // Logging:
        //   - Debug provider  → all levels (filtered by appsettings.json per category)
        //   - File provider   → Warning+ only (independent of category config)
        //     Logs land in %AppData%\EasyPDF\logs\easypdf-YYYY-MM-DD.log
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EasyPDF", "logs");

        services.AddLogging(b => b
            .SetMinimumLevel(LogLevel.Debug)
            .AddDebug()
            .AddProvider(new FileLoggerProvider(logDir))
            .AddFilter<FileLoggerProvider>(null, LogLevel.Warning));

        // Infrastructure — singletons share state across the app lifetime

        // Shared pool of large-stack threads for all MuPDF native calls.
        // 6 threads × 32 MB stack — covers 4 page renders + 2 thumbnail renders concurrently.
        services.AddSingleton(_ => new MuPdfDispatcher(workerCount: 6, stackBytes: 32 * 1024 * 1024));

        services.AddSingleton<IPageCache>(sp =>
        {
            int maxMb = config.GetValue<int>("Cache:MaxMegabytes", 512);
            return new PageCache(sp.GetRequiredService<ILogger<PageCache>>(), maxMb);
        });
        services.AddSingleton<MuPdfDocumentService>();         // concrete needed by render + search
        services.AddSingleton<IPdfDocumentService>(sp => sp.GetRequiredService<MuPdfDocumentService>());
        services.AddSingleton<MuPdfRenderService>();
        services.AddSingleton<IPdfRenderService>(sp => sp.GetRequiredService<MuPdfRenderService>());
        services.AddSingleton<ISearchService, MuPdfSearchService>();

        // Storage
        services.AddSingleton<IBookmarkRepository, JsonBookmarkRepository>();
        services.AddSingleton<IAnnotationRepository, JsonAnnotationRepository>();
        services.AddSingleton<IRecentFilesRepository, JsonRecentFilesRepository>();

        // UI Services
        services.AddSingleton<IThemeService, WpfThemeService>();
        services.AddSingleton<IDialogService, WpfDialogService>();

        // ViewModels
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<SidebarViewModel>();
        services.AddSingleton<PdfViewerViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
