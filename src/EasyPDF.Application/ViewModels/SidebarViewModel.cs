using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace EasyPDF.Application.ViewModels;

public enum SidebarTab { Thumbnails, TableOfContents, Bookmarks }

public sealed partial class SidebarViewModel : ObservableObject, IDisposable
{
    private readonly IPdfRenderService _renderService;
    private readonly IBookmarkRepository _bookmarkRepo;
    private readonly ILogger<SidebarViewModel> _logger;

    private CancellationTokenSource _thumbCts = new();
    private readonly Dictionary<int, ThumbnailItemViewModel> _thumbIndex = new();

    [ObservableProperty]
    private SidebarTab _activeTab = SidebarTab.Thumbnails;

    [ObservableProperty]
    private bool _isVisible = true;

    public BulkObservableCollection<ThumbnailItemViewModel> Thumbnails { get; } = new();
    public BulkObservableCollection<TocItemViewModel> TocItems { get; } = new();
    public ObservableCollection<BookmarkItemViewModel> Bookmarks { get; } = [];

    public bool HasTocItems  => TocItems.Count  > 0;
    public bool HasBookmarks => Bookmarks.Count > 0;

    public event EventHandler<int>?                    PageNavigationRequested;
    public event EventHandler<ThumbnailItemViewModel>? ScrollIntoViewRequested;

    public SidebarViewModel(
        IPdfRenderService renderService,
        IBookmarkRepository bookmarkRepo,
        ILogger<SidebarViewModel> logger)
    {
        _renderService = renderService;
        _bookmarkRepo = bookmarkRepo;
        _logger = logger;

        TocItems.CollectionChanged  += (_, _) => OnPropertyChanged(nameof(HasTocItems));
        Bookmarks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasBookmarks));
    }

    public async Task LoadDocumentAsync(PdfDocument document, CancellationToken ct = default)
    {
        // Cancel any in-progress thumbnail renders for the previous document.
        _thumbCts.Cancel();
        _thumbCts.Dispose();
        _thumbCts = new CancellationTokenSource();

        // Populate thumbnail VMs with no rendered content — ThumbnailControl renders
        // each thumbnail lazily when its container enters the visible viewport.
        _thumbIndex.Clear();
        var thumbVms = document.Pages.Select(p =>
        {
            var vm = new ThumbnailItemViewModel(p.Index);
            vm.NavigateRequested += (_, item) => PageNavigationRequested?.Invoke(this, item.PageIndex);
            _thumbIndex[p.Index] = vm;
            return vm;
        }).ToList();
        Thumbnails.ReplaceAll(thumbVms);

        TocItems.ReplaceAll(document.TableOfContents.Select(entry =>
        {
            var vm = new TocItemViewModel(entry);
            vm.NavigateRequested += (_, item) => PageNavigationRequested?.Invoke(this, item.PageIndex);
            return vm;
        }));

        await LoadBookmarksAsync(document.FilePath, ct);
    }

    /// <summary>
    /// Called by ThumbnailControl when its container enters the visible viewport.
    /// Mirrors PdfViewerViewModel.OnPageBecameVisibleAsync — one render per visible slot,
    /// no pre-loading of off-screen thumbnails, no semaphore starvation.
    /// </summary>
    public async Task OnThumbnailBecameVisibleAsync(int pageIndex, double dpiScale = 1.0, CancellationToken ct = default)
    {
        if (!_thumbIndex.TryGetValue(pageIndex, out var thumb) || thumb.RenderedPage is not null) return;

        try
        {
            thumb.IsLoading = true;
            // 128 px matches the effective display width of a portrait A4 page inside
            // the 140×180 container (Stretch=Uniform clamps height at ~180 → width ≈ 127 px).
            // Rendering at 160 was wasteful and caused an unnecessary Stretch downscale.
            thumb.RenderedPage = await _renderService.RenderThumbnailAsync(pageIndex, 128, dpiScale, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail render failed for page {Page}", pageIndex);
        }
        finally
        {
            thumb.IsLoading = false;
        }
    }

    public async Task LoadBookmarksAsync(string documentPath, CancellationToken ct = default)
    {
        Bookmarks.Clear();
        var bookmarks = await _bookmarkRepo.GetAllAsync(documentPath, ct);
        foreach (var b in bookmarks)
        {
            var vm = new BookmarkItemViewModel(b);
            vm.NavigateRequested += (_, item) => PageNavigationRequested?.Invoke(this, item.PageIndex);
            vm.DeleteRequested += async (_, item) => await RemoveBookmarkAsync(item, ct);
            Bookmarks.Add(vm);
        }
    }

    public async Task AddBookmarkAsync(string documentPath, int pageIndex, string title, CancellationToken ct = default)
    {
        var bookmark = new Bookmark(Guid.NewGuid(), documentPath, pageIndex, title, DateTime.UtcNow);
        await _bookmarkRepo.AddAsync(bookmark, ct);
        var vm = new BookmarkItemViewModel(bookmark);
        vm.NavigateRequested += (_, item) => PageNavigationRequested?.Invoke(this, item.PageIndex);
        vm.DeleteRequested += async (_, item) => await RemoveBookmarkAsync(item, ct);
        Bookmarks.Add(vm);
    }

    private async Task RemoveBookmarkAsync(BookmarkItemViewModel vm, CancellationToken ct)
    {
        await _bookmarkRepo.RemoveAsync(vm.Id, ct);
        Bookmarks.Remove(vm);
    }

    private int _selectedThumbIndex = -1;

    public void SetSelectedPage(int pageIndex)
    {
        if (_selectedThumbIndex >= 0 && _thumbIndex.TryGetValue(_selectedThumbIndex, out var prev))
            prev.IsSelected = false;

        if (_thumbIndex.TryGetValue(pageIndex, out var next))
        {
            next.IsSelected = true;
            ScrollIntoViewRequested?.Invoke(this, next);
        }

        _selectedThumbIndex = pageIndex;
    }

    [RelayCommand]
    private void SetTab(SidebarTab tab) => ActiveTab = tab;

    [RelayCommand]
    private void ToggleVisibility() => IsVisible = !IsVisible;

    public void Clear()
    {
        _thumbCts.Cancel();
        _thumbIndex.Clear();
        _selectedThumbIndex = -1;
        Thumbnails.Clear();
        TocItems.Clear();
        Bookmarks.Clear();
    }

    public void Dispose()
    {
        _thumbCts.Cancel();
        _thumbCts.Dispose();
    }
}
