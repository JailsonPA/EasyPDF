using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace EasyPDF.Application.ViewModels;

/// <summary>
/// Top-level coordinator: owns the document lifecycle and delegates to child ViewModels.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IPdfDocumentService _docService;
    private readonly IRecentFilesRepository _recentRepo;
    private readonly IDialogService _dialogService;
    private readonly IThemeService _themeService;
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
        PdfViewerViewModel viewer,
        SidebarViewModel sidebar,
        SearchViewModel search,
        ILogger<MainViewModel> logger)
    {
        _docService = docService;
        _recentRepo = recentRepo;
        _dialogService = dialogService;
        _themeService = themeService;
        _logger = logger;

        Viewer = viewer;
        Sidebar = sidebar;
        Search = search;

        _currentTheme = themeService.CurrentTheme;
        themeService.ThemeChanged += (_, t) => CurrentTheme = t;

        // Wire sidebar → viewer navigation
        Sidebar.PageNavigationRequested += (_, page) => Viewer.GoToPageCommand.Execute(page);
        Search.ResultNavigateRequested += (_, result) => Viewer.GoToPageCommand.Execute(result.PageIndex);
    }

    public async Task InitializeAsync()
    {
        _themeService.LoadSaved();

        var recents = await _recentRepo.GetAllAsync();
        foreach (var r in recents)
            RecentFiles.Add(r);
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
    private void CloseDocument()
    {
        _docService.Close();
        CurrentDocument = null;
        Viewer.Clear();
        Sidebar.Clear();
        Search.ClearSearchCommand.Execute(null);
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
        var next = CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        _themeService.ApplyTheme(next);
    }

    [RelayCommand]
    private void ToggleSearch() => IsSearchPanelOpen = !IsSearchPanelOpen;

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

        IsLoading = true;
        SetStatus("Opening…");

        try
        {
            var doc = await _docService.OpenAsync(filePath);
            CurrentDocument = doc;

            Viewer.LoadDocument(doc);
            await Sidebar.LoadDocumentAsync(doc);

            await _recentRepo.AddOrUpdateAsync(new RecentFile(
                doc.FilePath, doc.FileName, doc.PageCount,
                doc.FileSizeBytes, DateTime.UtcNow));

            RefreshRecentFiles();
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

    private async void RefreshRecentFiles()
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
