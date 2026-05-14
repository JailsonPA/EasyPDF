using CommunityToolkit.Mvvm.ComponentModel;
using EasyPDF.Application.Interfaces;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.IO;

namespace EasyPDF.Application.ViewModels;

public sealed partial class PdfTabViewModel : ObservableObject
{
    private readonly IServiceScope _scope;
    private readonly SynchronizationContext? _uiContext;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _watchDebounce;

    public PdfDocument Document { get; }
    public PdfViewerViewModel Viewer { get; }
    public SidebarViewModel Sidebar { get; }
    public SearchViewModel Search { get; }
    internal IPdfDocumentService DocumentService { get; }
    internal IExportService ExportService { get; }
    internal IPrintService PrintService { get; }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isSearchPanelOpen;

    [ObservableProperty]
    private bool _fileChangedOnDisk;

    public string TabTitle => Document.FileName;

    internal PdfTabViewModel(IServiceScope scope, PdfDocument document, SynchronizationContext? uiContext)
    {
        _scope = scope;
        _uiContext = uiContext;
        Document = document;
        Viewer = scope.ServiceProvider.GetRequiredService<PdfViewerViewModel>();
        Sidebar = scope.ServiceProvider.GetRequiredService<SidebarViewModel>();
        Search = scope.ServiceProvider.GetRequiredService<SearchViewModel>();
        DocumentService = scope.ServiceProvider.GetRequiredService<IPdfDocumentService>();
        ExportService = scope.ServiceProvider.GetRequiredService<IExportService>();
        PrintService = scope.ServiceProvider.GetRequiredService<IPrintService>();

        Sidebar.PageNavigationRequested += (_, page) => Viewer.GoToPageCommand.Execute(page);
        Viewer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PdfViewerViewModel.CurrentPageIndex))
                Sidebar.SetSelectedPage(Viewer.CurrentPageIndex);
        };
        Viewer.ViewportChanged += (_, vp) =>
            Sidebar.SetViewport(Viewer.CurrentPageIndex, vp.TopFrac, vp.HeightFrac);

        Search.ResultNavigateRequested += (_, result) =>
        {
            Viewer.GoToPageCommand.Execute(result.PageIndex);
            Viewer.UpdateSearchHighlights(Search.Results, Search.CurrentResultIndex);
        };
        Search.PropertyChanged += OnSearchPropertyChanged;
    }

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchViewModel.IsSearching) when !Search.IsSearching && Search.TotalResults > 0:
                Viewer.UpdateSearchHighlights(Search.Results, Search.CurrentResultIndex);
                break;
            case nameof(SearchViewModel.TotalResults) when Search.TotalResults == 0:
                Viewer.ClearSearchHighlights();
                break;
        }
    }

    internal void StartWatching(string filePath)
    {
        StopWatching();
        string? dir = Path.GetDirectoryName(filePath);
        if (dir is null || !Directory.Exists(dir)) return;
        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(filePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnWatchedFileEvent;
            _watcher.Created += OnWatchedFileEvent;
        }
        catch { }
    }

    internal void StopWatching()
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

    internal async ValueTask DisposeAsync()
    {
        StopWatching();
        if (_scope is IAsyncDisposable ad) await ad.DisposeAsync();
        else _scope.Dispose();
    }
}
