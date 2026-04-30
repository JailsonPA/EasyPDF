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
    private readonly IUpdateService _updateService;
    private readonly AppSettings _settings;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
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
    [NotifyPropertyChangedFor(nameof(UpdateAvailable))]
    private UpdateInfo? _pendingUpdate;

    public bool UpdateAvailable => PendingUpdate is not null;

    public string Title => CurrentDocument is null
        ? "EasyPDF"
        : $"{CurrentDocument.FileName} — EasyPDF";

    public bool HasDocument => CurrentDocument is not null;

    public PdfViewerViewModel Viewer { get; }
    public SidebarViewModel Sidebar { get; }
    public SearchViewModel Search { get; }

    public ObservableCollection<RecentFile> RecentFiles { get; } = [];

    public MainViewModel(
        IPdfDocumentService docService,
        IRecentFilesRepository recentRepo,
        IDialogService dialogService,
        IThemeService themeService,
        IPrintService printService,
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
        _updateService = updateService;
        _settings      = settings;
        _logger        = logger;

        Viewer = viewer;
        Sidebar = sidebar;
        Search = search;

        _currentTheme = themeService.CurrentTheme;
        themeService.ThemeChanged += (_, t) => CurrentTheme = t;

        // Wire sidebar → viewer navigation
        Sidebar.PageNavigationRequested += (_, page) => Viewer.GoToPageCommand.Execute(page);

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

        var recents = await _recentRepo.GetAllAsync();
        foreach (var r in recents)
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

    [RelayCommand]
    private void DismissUpdate() => PendingUpdate = null;

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
    private void ToggleSearch() => IsSearchPanelOpen = !IsSearchPanelOpen;

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (CurrentDocument is null) return;
        await _printService.PrintAsync(CurrentDocument.FileName, CurrentDocument.Pages);
        SetStatus("Print job sent.");
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

            // Preserve lastPageIndex so it isn't reset to 0 on every open.
            await _recentRepo.AddOrUpdateAsync(new RecentFile(
                doc.FilePath, doc.FileName, doc.PageCount,
                doc.FileSizeBytes, DateTime.UtcNow, lastPageIndex));

            await RefreshRecentFilesAsync();
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
            foreach (var r in await _recentRepo.GetAllAsync())
                RecentFiles.Add(r);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh recent files list");
        }
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
