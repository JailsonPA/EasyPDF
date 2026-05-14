using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Application.Interfaces;
using EasyPDF.Core;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace EasyPDF.Application.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IFileDropTarget
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRecentFilesRepository _recentRepo;
    private readonly IDialogService _dialogService;
    private readonly IThemeService _themeService;
    private readonly IUpdateService _updateService;
    private readonly AppSettings _settings;
    private readonly ILogger<MainViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;


    public ObservableCollection<PdfTabViewModel> Tabs { get; } = [];
    public bool HasTabs => Tabs.Count > 0;

    [ObservableProperty]
    private PdfTabViewModel? _activeTab;

    partial void OnActiveTabChanged(PdfTabViewModel? oldValue, PdfTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.Sidebar.PropertyChanged -= OnActiveSidebarPropertyChanged;
            oldValue.PropertyChanged -= OnActiveTabPropertyChanged;
            oldValue.IsActive = false;
        }
        if (newValue is not null)
        {
            newValue.Sidebar.PropertyChanged += OnActiveSidebarPropertyChanged;
            newValue.PropertyChanged += OnActiveTabPropertyChanged;
            newValue.IsActive = true;
        }

        OnPropertyChanged(nameof(Viewer));
        OnPropertyChanged(nameof(Sidebar));
        OnPropertyChanged(nameof(Search));
        OnPropertyChanged(nameof(CurrentDocument));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsSidebarVisible));
        OnPropertyChanged(nameof(IsSearchPanelOpen));
        OnPropertyChanged(nameof(FileChangedOnDisk));
    }

    private void OnActiveSidebarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarViewModel.IsVisible))
            OnPropertyChanged(nameof(IsSidebarVisible));
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PdfTabViewModel.IsSearchPanelOpen):
                OnPropertyChanged(nameof(IsSearchPanelOpen));
                break;
            case nameof(PdfTabViewModel.FileChangedOnDisk):
                OnPropertyChanged(nameof(FileChangedOnDisk));
                break;
        }
    }


    public PdfViewerViewModel? Viewer => ActiveTab?.Viewer;
    public SidebarViewModel? Sidebar => ActiveTab?.Sidebar;
    public SearchViewModel? Search => ActiveTab?.Search;
    public PdfDocument? CurrentDocument => ActiveTab?.Document;
    public bool HasDocument => ActiveTab is not null;

    public string Title => CurrentDocument is null
        ? "EasyPDF"
        : $"{CurrentDocument.FileName} — EasyPDF";

    public bool IsSidebarVisible => HasDocument && (ActiveTab?.Sidebar.IsVisible ?? false);

    public bool IsSearchPanelOpen
    {
        get => ActiveTab?.IsSearchPanelOpen ?? false;
        set { if (ActiveTab is not null) ActiveTab.IsSearchPanelOpen = value; }
    }

    public bool FileChangedOnDisk => ActiveTab?.FileChangedOnDisk ?? false;


    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private AppTheme _currentTheme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateAvailable))]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    private bool _isUpdateDownloaded;

    [ObservableProperty]
    private int _updateDownloadProgress;

    public bool UpdateAvailable => PendingUpdate is not null;
    public bool UpdateCanInstall => _updateService.CanInstall;
    public bool CanDownloadUpdate => UpdateAvailable && UpdateCanInstall && !IsDownloadingUpdate && !IsUpdateDownloaded;

    public ObservableCollection<RecentFile> RecentFiles { get; } = [];
    public bool HasRecentFiles => RecentFiles.Count > 0;

    public MainViewModel(
        IServiceProvider serviceProvider,
        IRecentFilesRepository recentRepo,
        IDialogService dialogService,
        IThemeService themeService,
        IUpdateService updateService,
        AppSettings settings,
        ILogger<MainViewModel> logger)
    {
        _serviceProvider = serviceProvider;
        _recentRepo      = recentRepo;
        _dialogService   = dialogService;
        _themeService    = themeService;
        _updateService   = updateService;
        _settings        = settings;
        _logger          = logger;
        _uiContext       = SynchronizationContext.Current;

        _currentTheme = themeService.CurrentTheme;
        themeService.ThemeChanged += (_, t) => CurrentTheme = t;

        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
        RecentFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentFiles));
    }

    public async Task InitializeAsync()
    {
        _themeService.LoadSaved();

        foreach (var r in await LoadAndPurgeMissingRecentFilesAsync())
            RecentFiles.Add(r);

        _ = CheckForUpdateAsync();
    }


    private async Task CheckForUpdateAsync()
    {
        // Fire-and-forget on the thread pool — ConfigureAwait(false) keeps the network call
        // off the UI thread without needing an artificial Task.Delay to "yield" startup.
        try
        {
            PendingUpdate = await _updateService.CheckForUpdateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed");
        }
    }

    private CancellationTokenSource? _downloadCts;

    [RelayCommand]
    private void DismissUpdate()
    {
        _downloadCts?.Cancel();
        PendingUpdate = null;
        IsDownloadingUpdate = false;
        IsUpdateDownloaded = false;
        UpdateDownloadProgress = 0;
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (PendingUpdate is null || IsDownloadingUpdate) return;
        _downloadCts = new CancellationTokenSource();
        IsDownloadingUpdate = true;
        UpdateDownloadProgress = 0;
        try
        {
            var progress = new Progress<int>(p => UpdateDownloadProgress = p);
            await _updateService.DownloadUpdateAsync(PendingUpdate, progress, _downloadCts.Token);
            IsUpdateDownloaded = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update download failed");
        }
        finally
        {
            IsDownloadingUpdate = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        if (!IsUpdateDownloaded) return;
        _updateService.ApplyUpdateAndRestart();
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (PendingUpdate is null) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(PendingUpdate.ReleaseUrl)
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open release URL {Url}", PendingUpdate.ReleaseUrl);
        }
    }


    [RelayCommand]
    private void ActivateTab(PdfTabViewModel tab) => ActiveTab = tab;

    [RelayCommand]
    private async Task CloseTabAsync(PdfTabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;

        await SaveLastPageForTabAsync(tab);
        tab.StopWatching();
        tab.FileChangedOnDisk = false;

        try
        {
            tab.DocumentService.Close();
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "Timed out waiting for renders to finish before closing document");
            await _dialogService.ShowErrorAsync("Close Failed", "The document could not be closed because a render operation is taking too long. Try again in a moment.");
            return;
        }

        tab.Viewer.Clear();
        tab.Sidebar.Clear();
        tab.Search.ClearSearchCommand.Execute(null);
        tab.Search.TotalPages = 0;

        bool wasActive = ActiveTab == tab;
        int idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (wasActive)
            ActiveTab = Tabs.Count > 0 ? Tabs[Math.Max(0, Math.Min(idx, Tabs.Count - 1))] : null;

        _ = tab.DisposeAsync().AsTask();
        SetStatus("Document closed.");
    }


    [RelayCommand]
    private async Task OpenFileAsync()
    {
        string? path = await _dialogService.OpenPdfFileAsync();
        if (path is null) return;
        await LoadDocumentAsync(path);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(RecentFile recent) =>
        await LoadDocumentAsync(recent.FilePath);

    [RelayCommand]
    private async Task ClearRecentFilesAsync()
    {
        await _recentRepo.ClearAsync();
        RecentFiles.Clear();
    }

    [RelayCommand]
    private async Task ReloadDocumentAsync()
    {
        if (ActiveTab is null) return;
        string path = ActiveTab.Document.FilePath;
        int tabIndex = Tabs.IndexOf(ActiveTab);
        ActiveTab.FileChangedOnDisk = false;
        await CloseTabAsync(ActiveTab);
        await LoadDocumentAsync(path);
        if (Tabs.Count > 0 && tabIndex >= 0 && tabIndex < Tabs.Count - 1)
        {
            var newTab = Tabs[^1];
            Tabs.Move(Tabs.Count - 1, tabIndex);
            ActiveTab = newTab;
        }
    }

    [RelayCommand]
    private void DismissFileChanged()
    {
        if (ActiveTab is not null)
            ActiveTab.FileChangedOnDisk = false;
    }


    [RelayCommand]
    private async Task AddBookmarkAsync()
    {
        if (CurrentDocument is null || ActiveTab is null) return;

        string defaultTitle = $"Page {Viewer!.CurrentPageIndex + 1}";
        string? title = await _dialogService.PromptAsync("Add Bookmark", "Bookmark name", defaultTitle);
        if (title is null) return;

        await Sidebar!.AddBookmarkAsync(CurrentDocument.FilePath, Viewer.CurrentPageIndex, title);
        Sidebar.ActiveTab = SidebarTab.Bookmarks;
        SetStatus($"Bookmark added: {title}");
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = CurrentTheme switch
        {
            AppTheme.Dark         => AppTheme.Light,
            AppTheme.Light        => AppTheme.HighContrast,
            AppTheme.HighContrast => AppTheme.Dark,
            _                     => AppTheme.Dark,
        };
        _themeService.ApplyTheme(next);
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (Viewer is null) return;
        await Viewer.UndoAsync(default);
    }

    [RelayCommand]
    private async Task RedoAsync()
    {
        if (Viewer is null) return;
        await Viewer.RedoAsync(default);
    }

    [RelayCommand]
    private void ToggleSearch()
    {
        if (ActiveTab is null) return;
        ActiveTab.IsSearchPanelOpen = !ActiveTab.IsSearchPanelOpen;
        if (!ActiveTab.IsSearchPanelOpen)
            ActiveTab.Search.ClearSearchCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        if (CurrentDocument is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"/select,\"{CurrentDocument.FilePath}\"")
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open Explorer for {Path}", CurrentDocument.FilePath);
        }
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (ActiveTab is null) return;
        var viewer = ActiveTab.Viewer;
        await ActiveTab.PrintService.PrintAsync(
            ActiveTab.Document.FileName,
            ActiveTab.Document.Pages,
            viewer.Annotations,
            viewer.CurrentPageIndex);
        SetStatus("Print job sent.");
    }

    [RelayCommand]
    private async Task ExportAnnotatedPdfAsync()
    {
        if (ActiveTab is null || Viewer is null) return;

        string suggested = Path.GetFileNameWithoutExtension(ActiveTab.Document.FileName) + "_anotado";
        string? path = await _dialogService.SavePdfFileAsync(suggested);
        if (path is null) return;

        IsLoading = true;
        SetStatus("Exportando PDF…");
        try
        {
            await Viewer.ExportToPathAsync(path);
            SetStatus($"PDF exportado: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            await _dialogService.ShowErrorAsync("Erro ao Exportar", ex.Message);
            SetStatus("Exportação falhou.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// "Save Annotations to PDF" — preserves the original PDF (selectable text,
    /// vectors) and writes annotations as native PDF objects on top. Distinct from
    /// "Export Annotated PDF" which rasterizes every page; this one keeps the
    /// original quality and lets the receiver's PDF reader edit/remove the
    /// annotations normally.
    [RelayCommand]
    private async Task SaveAnnotationsToPdfAsync()
    {
        if (ActiveTab is null || Viewer is null) return;

        string suggested = Path.GetFileNameWithoutExtension(ActiveTab.Document.FileName) + "_annotated";
        string? path = await _dialogService.SavePdfFileAsync(suggested);
        if (path is null) return;

        // Refuse in-place overwrite — PdfSharp opens the source while writing and an
        // overwrite collapses to the same handle, corrupting the file.
        if (string.Equals(path, ActiveTab.Document.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            await _dialogService.ShowErrorAsync(
                "Choose a different file",
                "Saving over the original PDF is not supported — please pick a different destination.");
            return;
        }

        IsLoading = true;
        SetStatus("Saving annotations to PDF…");
        try
        {
            await Viewer.SaveAnnotationsToPdfAsync(path);
            SetStatus($"Saved: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save annotations to PDF failed");
            await _dialogService.ShowErrorAsync("Save Failed",
                $"Could not save annotations into the PDF. The file may be encrypted, signed, or use a structure that's not supported for editing.\n\n{ex.Message}");
            SetStatus("Save failed.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ExportCurrentPageAsync()
    {
        if (ActiveTab is null || Viewer is null) return;

        string suggested = $"{Path.GetFileNameWithoutExtension(ActiveTab.Document.FileName)}_page{Viewer.CurrentPageIndex + 1}";
        string? path = await _dialogService.SaveImageFileAsync(suggested);
        if (path is null) return;

        IsLoading = true;
        SetStatus("Exporting…");
        try
        {
            await ActiveTab.ExportService.ExportPageAsync(Viewer.CurrentPageIndex, path, dpi: 150);
            SetStatus($"Page {Viewer.CurrentPageIndex + 1} exported.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed for page {Page}", Viewer.CurrentPageIndex);
            await _dialogService.ShowErrorAsync("Export Failed", ex.Message);
            SetStatus("Export failed.");
        }
        finally
        {
            IsLoading = false;
        }
    }


    public async Task DropFileAsync(string filePath)
    {
        if (!filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await _dialogService.ShowErrorAsync("Unsupported File", "Only PDF files are supported.");
            return;
        }
        await LoadDocumentAsync(filePath);
    }


    private async Task LoadDocumentAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            await _dialogService.ShowErrorAsync("File Not Found", $"Cannot find:\n{filePath}");
            return;
        }

        var existing = Tabs.FirstOrDefault(t =>
            t.Document.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveTab = existing;
            return;
        }

        var fileSize = new FileInfo(filePath).Length;
        if (fileSize > _settings.LargeFileSizeBytes)
        {
            double sizeMb = fileSize / (1024.0 * 1024.0);
            bool proceed = await _dialogService.ConfirmAsync(
                "Large File",
                $"This file is {sizeMb:F0} MB and may be slow to open or use significant memory.\n\nOpen it anyway?");
            if (!proceed) return;
        }

        IsLoading = true;
        SetStatus("Opening…");

        var scope = _serviceProvider.CreateScope();
        try
        {
            var docService = scope.ServiceProvider.GetRequiredService<IPdfDocumentService>();
            var doc = await OpenWithPasswordAsync(docService, filePath);
            if (doc is null)
            {
                scope.Dispose();
                SetStatus("Open cancelled.");
                return;
            }

            var tab = new PdfTabViewModel(scope, doc, _uiContext);
            tab.Viewer.LoadDocument(doc);
            await tab.Sidebar.LoadDocumentAsync(doc);
            await tab.Viewer.LoadAnnotationsAsync(doc.FilePath, doc.ContentHash);
            tab.Search.TotalPages = doc.PageCount;

            // Look up "last page" with hash-first preference so the bookmark survives a
            // file rename/move. Falls back to path match for legacy entries with no hash.
            int lastPageIndex = FindLastPageIndex(doc.FilePath, doc.ContentHash);

            if (lastPageIndex > 0 && lastPageIndex < doc.PageCount)
                tab.Viewer.GoToPageCommand.Execute(lastPageIndex);

            tab.Sidebar.SetSelectedPage(tab.Viewer.CurrentPageIndex);

            await _recentRepo.AddOrUpdateAsync(new RecentFile(
                doc.FilePath, doc.FileName, doc.PageCount,
                doc.FileSizeBytes, DateTime.UtcNow, lastPageIndex)
                { ContentHash = doc.ContentHash });

            await RefreshRecentFilesAsync();
            tab.StartWatching(doc.FilePath);

            Tabs.Add(tab);
            ActiveTab = tab;

            SetStatus($"Opened {doc.FileName}  ·  {doc.PageCount} pages");
        }
        catch (Exception ex)
        {
            scope.Dispose();
            _logger.LogError(ex, "Failed to open {Path}", filePath);
            await _dialogService.ShowErrorAsync("Cannot Open File", ex.Message);
            SetStatus("Failed to open file.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<PdfDocument?> OpenWithPasswordAsync(IPdfDocumentService docService, string filePath)
    {
        try
        {
            return await docService.OpenAsync(filePath);
        }
        catch (PdfPasswordRequiredException)
        {
            // PDF is encrypted — fall through to password prompts.
        }

        string? errorMsg = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IsLoading = false;
            string? password = await _dialogService.PromptPasswordAsync(
                Path.GetFileName(filePath), errorMsg);

            if (password is null) return null;

            IsLoading = true;
            try
            {
                return await docService.OpenAsync(filePath, password);
            }
            catch (PdfPasswordRequiredException)
            {
                errorMsg = "Incorrect password. Please try again.";
            }
        }

        await _dialogService.ShowErrorAsync("Cannot Open File",
            "Incorrect password after 3 attempts. The file could not be opened.");
        return null;
    }


    public async Task SaveLastPageAsync()
    {
        foreach (var tab in Tabs.ToList())
            await SaveLastPageForTabAsync(tab);
    }

    private async Task SaveLastPageForTabAsync(PdfTabViewModel tab)
    {
        var existing = FindRecentEntry(tab.Document.FilePath, tab.Document.ContentHash);
        if (existing is not null)
            await _recentRepo.AddOrUpdateAsync(existing with { LastPageIndex = tab.Viewer.CurrentPageIndex });
    }

    private int FindLastPageIndex(string path, string? contentHash) =>
        FindRecentEntry(path, contentHash)?.LastPageIndex ?? 0;

    private RecentFile? FindRecentEntry(string path, string? contentHash)
    {
        if (contentHash is not null)
        {
            var byHash = RecentFiles.FirstOrDefault(r => r.ContentHash == contentHash);
            if (byHash is not null) return byHash;
        }
        return RecentFiles.FirstOrDefault(r =>
            r.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshRecentFilesAsync()
    {
        try
        {
            RecentFiles.Clear();
            foreach (var r in await LoadAndPurgeMissingRecentFilesAsync())
                RecentFiles.Add(r);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh recent files list");
        }
    }

    private async Task<IReadOnlyList<RecentFile>> LoadAndPurgeMissingRecentFilesAsync()
    {
        var all = await _recentRepo.GetAllAsync();
        var missing = all.Where(r => !File.Exists(r.FilePath)).ToList();

        foreach (var m in missing)
        {
            _logger.LogDebug("Removing missing recent file: {Path}", m.FilePath);
            await _recentRepo.RemoveAsync(m.FilePath);
        }

        return all.Where(r => File.Exists(r.FilePath)).ToList();
    }


    private CancellationTokenSource? _statusCts;

    private void SetStatus(string message)
    {
        StatusMessage = message;
        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _statusCts = new CancellationTokenSource();
        _ = ClearStatusAfterDelayAsync(message, _statusCts.Token);
    }

    private async Task ClearStatusAfterDelayAsync(string message, CancellationToken ct)
    {
        try
        {
            await Task.Delay(4000, ct);
            if (StatusMessage == message)
                StatusMessage = null;
        }
        catch (OperationCanceledException) { }
    }
}
