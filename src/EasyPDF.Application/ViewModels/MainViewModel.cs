using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Application.Interfaces;
using EasyPDF.Core;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace EasyPDF.Application.ViewModels;

/// <summary>
/// Top-level coordinator: owns the document lifecycle and delegates to child ViewModels.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IFileDropTarget
{
    private readonly IPdfDocumentService _docService;
    private readonly IRecentFilesRepository _recentRepo;
    private readonly IDialogService _dialogService;
    private readonly IThemeService _themeService;
    private readonly IPrintService _printService;
    private readonly IExportService _exportService;
    private readonly IUpdateService _updateService;
    private readonly AppSettings _settings;
    private readonly ILogger<MainViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _watchDebounce;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    [NotifyPropertyChangedFor(nameof(IsSidebarVisible))]
    private PdfDocument? _currentDocument;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private AppTheme _currentTheme;

    [ObservableProperty]
    private bool _isSearchPanelOpen;

    [ObservableProperty]
    private bool _fileChangedOnDisk;

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

    public string Title => CurrentDocument is null
        ? "EasyPDF"
        : $"{CurrentDocument.FileName} — EasyPDF";

    public bool HasDocument => CurrentDocument is not null;
    public bool IsSidebarVisible => HasDocument && Sidebar.IsVisible;

    public PdfViewerViewModel Viewer { get; }
    public SidebarViewModel Sidebar { get; }
    public SearchViewModel Search { get; }

    public ObservableCollection<RecentFile> RecentFiles { get; } = [];
    public bool HasRecentFiles => RecentFiles.Count > 0;

    public MainViewModel(
        IPdfDocumentService docService,
        IRecentFilesRepository recentRepo,
        IDialogService dialogService,
        IThemeService themeService,
        IPrintService printService,
        IExportService exportService,
        IUpdateService updateService,
        AppSettings settings,
        PdfViewerViewModel viewer,
        SidebarViewModel sidebar,
        SearchViewModel search,
        ILogger<MainViewModel> logger)
    {
        _docService    = docService;
        _recentRepo    = recentRepo;
        _dialogService = dialogService;
        _themeService  = themeService;
        _printService  = printService;
        _exportService = exportService;
        _updateService = updateService;
        _settings      = settings;
        _logger        = logger;
        _uiContext     = SynchronizationContext.Current;

        Viewer = viewer;
        Sidebar = sidebar;
        Search = search;

        _currentTheme = themeService.CurrentTheme;
        themeService.ThemeChanged += (_, t) => CurrentTheme = t;
        RecentFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentFiles));

        // Keep IsSidebarVisible in sync when the user toggles sidebar visibility.
        Sidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SidebarViewModel.IsVisible))
                OnPropertyChanged(nameof(IsSidebarVisible));
        };

        // Wire sidebar → viewer navigation
        Sidebar.PageNavigationRequested += (_, page) => Viewer.GoToPageCommand.Execute(page);

        // Wire viewer → sidebar: keep thumbnail selection in sync with the current page.
        Viewer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PdfViewerViewModel.CurrentPageIndex))
                Sidebar.SetSelectedPage(Viewer.CurrentPageIndex);
        };

        // Wire search → viewer: navigate + update highlight overlay
        Search.ResultNavigateRequested += (_, result) =>
        {
            Viewer.GoToPageCommand.Execute(result.PageIndex);
            Viewer.UpdateSearchHighlights(Search.Results, Search.CurrentResultIndex);
        };

        // Show all highlights when a search finishes; clear them when results are wiped.
        Search.PropertyChanged += OnSearchPropertyChanged;
    }

    public async Task InitializeAsync()
    {
        _themeService.LoadSaved();

        foreach (var r in await LoadAndPurgeMissingRecentFilesAsync())
            RecentFiles.Add(r);

        // Fire-and-forget: check for a newer release in the background.
        // A 3-second startup delay avoids contending with the initial PDF render.
        _ = CheckForUpdateAfterDelayAsync();
    }

    private async Task CheckForUpdateAfterDelayAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            PendingUpdate = await _updateService.CheckForUpdateAsync().ConfigureAwait(false);
        }
        catch { /* never surface update-check errors */ }
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
    private async Task ClearRecentFilesAsync()
    {
        await _recentRepo.ClearAsync();
        RecentFiles.Clear();
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

    /// <summary>
    /// Persists the current page index so the next open restores the reading position.
    /// Safe to call fire-and-forget on app shutdown.
    /// </summary>
    public async Task SaveLastPageAsync()
    {
        if (CurrentDocument is null) return;
        var existing = RecentFiles
            .FirstOrDefault(r => r.FilePath.Equals(CurrentDocument.FilePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            await _recentRepo.AddOrUpdateAsync(existing with { LastPageIndex = Viewer.CurrentPageIndex });
    }

    [RelayCommand]
    private async Task CloseDocumentAsync()
    {
        StopWatching();
        FileChangedOnDisk = false;
        await SaveLastPageAsync();

        try
        {
            _docService.Close();
        }
        catch (TimeoutException ex)
        {
            _logger.LogError(ex, "Timed out waiting for renders to finish before closing document");
            await _dialogService.ShowErrorAsync("Close Failed", "The document could not be closed because a render operation is taking too long. Try again in a moment.");
            return;
        }

        CurrentDocument = null;
        IsSearchPanelOpen = false;
        Viewer.Clear();
        Sidebar.Clear();
        Search.ClearSearchCommand.Execute(null);
        Search.TotalPages = 0;
        SetStatus("Document closed.");
    }

    [RelayCommand]
    private async Task AddBookmarkAsync()
    {
        if (CurrentDocument is null) return;

        string defaultTitle = $"Page {Viewer.CurrentPageIndex + 1}";
        string? title = await _dialogService.PromptAsync("Add Bookmark", "Bookmark name", defaultTitle);
        if (title is null) return;

        await Sidebar.AddBookmarkAsync(CurrentDocument!.FilePath, Viewer.CurrentPageIndex, title);
        Sidebar.ActiveTab = SidebarTab.Bookmarks;
        SetStatus($"Bookmark added: {title}");
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        // Cycles Dark → Light → HighContrast → Dark.
        // System resolves to whichever concrete theme Windows reports, so treat it as Dark when cycling.
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
    private void ToggleSearch()
    {
        IsSearchPanelOpen = !IsSearchPanelOpen;
        if (!IsSearchPanelOpen)
            Search.ClearSearchCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        if (CurrentDocument is null) return;
        try
        {
            // /select highlights the file in Explorer instead of just opening the folder.
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
        if (CurrentDocument is null) return;
        await _printService.PrintAsync(CurrentDocument.FileName, CurrentDocument.Pages);
        SetStatus("Print job sent.");
    }

    [RelayCommand]
    private async Task ExportCurrentPageAsync()
    {
        if (CurrentDocument is null) return;

        string suggested = $"{Path.GetFileNameWithoutExtension(CurrentDocument.FileName)}_page{Viewer.CurrentPageIndex + 1}";
        string? path = await _dialogService.SaveImageFileAsync(suggested);
        if (path is null) return;

        IsLoading = true;
        SetStatus("Exporting…");
        try
        {
            await _exportService.ExportPageAsync(Viewer.CurrentPageIndex, path, dpi: 150);
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

    [RelayCommand]
    private async Task ReloadDocumentAsync()
    {
        if (CurrentDocument is null) return;
        string path = CurrentDocument.FilePath;
        FileChangedOnDisk = false;
        await CloseDocumentAsync();
        await LoadDocumentAsync(path);
    }

    [RelayCommand]
    private void DismissFileChanged() => FileChangedOnDisk = false;

    private void StartWatching(string filePath)
    {
        StopWatching();
        string? dir = Path.GetDirectoryName(filePath);
        if (dir is null || !Directory.Exists(dir)) return;

        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(filePath))
            {
                NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnWatchedFileEvent;
            _watcher.Created += OnWatchedFileEvent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FileSystemWatcher could not be started for {Path}", filePath);
        }
    }

    private void StopWatching()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnWatchedFileEvent;
            _watcher.Created -= OnWatchedFileEvent;
            _watcher.Dispose();
            _watcher = null;
        }

        _watchDebounce?.Cancel();
        _watchDebounce?.Dispose();
        _watchDebounce = null;
    }

    private void OnWatchedFileEvent(object sender, FileSystemEventArgs e)
    {
        // Debounce: many editors write a file in multiple bursts.
        _watchDebounce?.Cancel();
        _watchDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _watchDebounce = cts;

        _ = Task.Delay(600, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            if (_uiContext is not null)
                _uiContext.Post(_ => FileChangedOnDisk = true, null);
            else
                FileChangedOnDisk = true;
        }, TaskScheduler.Default);
    }

    private async Task LoadDocumentAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            await _dialogService.ShowErrorAsync("File Not Found", $"Cannot find:\n{filePath}");
            return;
        }

        // Read stored last-page before the upsert below overwrites it.
        int lastPageIndex = RecentFiles
            .FirstOrDefault(r => r.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            ?.LastPageIndex ?? 0;

        var fileSize = new FileInfo(filePath).Length;
        if (fileSize > _settings.LargeFileSizeBytes)
        {
            double sizeMb = fileSize / (1024.0 * 1024.0);
            bool proceed = await _dialogService.ConfirmAsync(
                "Large File",
                $"This file is {sizeMb:F0} MB and may be slow to open or use significant memory.\n\nOpen it anyway?");
            if (!proceed) return;
        }

        StopWatching();
        FileChangedOnDisk = false;
        IsLoading = true;
        SetStatus("Opening…");

        try
        {
            var doc = await OpenWithPasswordAsync(filePath);
            if (doc is null) { SetStatus("Open cancelled."); return; }

            CurrentDocument = doc;

            Viewer.LoadDocument(doc);
            await Sidebar.LoadDocumentAsync(doc);
            Search.TotalPages = doc.PageCount;

            // Restore the last viewed page (scroll syncs sidebar selection too).
            if (lastPageIndex > 0 && lastPageIndex < doc.PageCount)
                Viewer.GoToPageCommand.Execute(lastPageIndex);

            // Sync sidebar selection after thumb index is populated.
            // Viewer.LoadDocument fires CurrentPageIndex before LoadDocumentAsync
            // fills _thumbIndex, so SetSelectedPage above found nothing.
            Sidebar.SetSelectedPage(Viewer.CurrentPageIndex);

            // Preserve lastPageIndex so it isn't reset to 0 on every open.
            await _recentRepo.AddOrUpdateAsync(new RecentFile(
                doc.FilePath, doc.FileName, doc.PageCount,
                doc.FileSizeBytes, DateTime.UtcNow, lastPageIndex));

            await RefreshRecentFilesAsync();
            StartWatching(doc.FilePath);
            SetStatus($"Opened {doc.FileName}  ·  {doc.PageCount} pages");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open {Path}", filePath);
            await _dialogService.ShowErrorAsync("Cannot Open File", ex.Message);
            SetStatus("Failed to open file.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Opens a PDF, prompting for a password up to 3 times if needed.
    /// Returns null if the user cancels or exhausts all attempts.
    /// </summary>
    private async Task<PdfDocument?> OpenWithPasswordAsync(string filePath)
    {
        // First attempt — works for unencrypted PDFs without any dialog.
        try
        {
            return await _docService.OpenAsync(filePath);
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

            if (password is null) return null; // user cancelled

            IsLoading = true;
            try
            {
                return await _docService.OpenAsync(filePath, password);
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

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // Search just finished — paint all match rectangles (no active one yet).
            case nameof(SearchViewModel.IsSearching) when !Search.IsSearching && Search.TotalResults > 0:
                Viewer.UpdateSearchHighlights(Search.Results, Search.CurrentResultIndex);
                break;

            // Results were cleared (new search started or ClearSearch called).
            case nameof(SearchViewModel.TotalResults) when Search.TotalResults == 0:
                Viewer.ClearSearchHighlights();
                break;
        }
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

    /// <summary>
    /// Returns only recent files that still exist on disk, silently removing the rest
    /// from the repository so they never reappear.
    /// </summary>
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
