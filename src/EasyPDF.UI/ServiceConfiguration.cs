using EasyPDF.Application;
using EasyPDF.Application.Interfaces;
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

        var logDir = AppDataPaths.LogsDirectory;
        services.AddLogging(b => b
            .SetMinimumLevel(LogLevel.Debug)
            .AddDebug()
            .AddProvider(new FileLoggerProvider(logDir))
            .AddFilter<FileLoggerProvider>(null, LogLevel.Warning));

     
        services.AddSingleton(_ => new MuPdfDispatcher(workerCount: 6, stackBytes: 8 * 1024 * 1024));


        services.AddScoped<IPageCache>(sp =>
        {
            int maxMb = config.GetValue<int>("Cache:MaxMegabytes", 512);
            return new PageCache(sp.GetRequiredService<ILogger<PageCache>>(), maxMb);
        });
        services.AddScoped<MuPdfDocumentService>();
        services.AddScoped<IPdfDocumentService>(sp => sp.GetRequiredService<MuPdfDocumentService>());
        services.AddScoped<MuPdfRenderService>();
        services.AddScoped<IPdfRenderService>(sp => sp.GetRequiredService<MuPdfRenderService>());
        services.AddScoped<ISearchService, MuPdfSearchService>();
        services.AddScoped<ITextExtractionService, MuPdfTextExtractionService>();
        services.AddScoped<IPrintService, WpfPrintService>();
        services.AddScoped<IExportService, WpfExportService>();
        services.AddScoped<IPdfExportService, MuPdfExportService>();
        services.AddScoped<IPdfAnnotationWriter, PdfSharpAnnotationWriter>();
        services.AddScoped<PdfViewerViewModel>();
        services.AddScoped<SidebarViewModel>();
        services.AddScoped<SearchViewModel>();

        // ── Singleton storage ──────────────────────────────────────────────────
        services.AddSingleton<IBookmarkRepository>(_ => new JsonBookmarkRepository(AppDataPaths.BookmarksFile));
        services.AddSingleton<IAnnotationRepository>(_ =>
            new SqliteAnnotationRepository(
                AppDataPaths.AnnotationsDb,
                legacyJsonPathForMigration: AppDataPaths.AnnotationsFile));
        services.AddSingleton<IRecentFilesRepository>(_ => new JsonRecentFilesRepository(AppDataPaths.RecentFilesFile));
        services.AddSingleton<IPreferencesRepository>(_ => new JsonPreferencesRepository(AppDataPaths.SettingsFile));

        services.AddSingleton(_ =>
        {
            int largeMb = config.GetValue<int>("LargeFileSizeMb", 500);
            return new AppSettings { LargeFileSizeBytes = (long)largeMb * 1024 * 1024 };
        });

        // ── Singleton UI services ──────────────────────────────────────────────
        services.AddSingleton<IThemeService, WpfThemeService>();
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<IUpdateService>(_ =>
        {
            string owner = config.GetValue<string>("GitHub:Owner") ?? "JailsonPA";
            string repo  = config.GetValue<string>("GitHub:Repo")  ?? "EasyPDF";
            bool   check = config.GetValue<bool>("GitHub:CheckForUpdates", true);
            return check
                ? new VelopackUpdateService(owner, repo)
                : new NoOpUpdateService();
        });

 
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
