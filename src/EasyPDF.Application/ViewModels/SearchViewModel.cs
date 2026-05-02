using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace EasyPDF.Application.ViewModels;

public sealed partial class SearchViewModel : ObservableObject
{
    private readonly ISearchService _searchService;
    private readonly ILogger<SearchViewModel> _logger;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private int _totalResults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentResultDisplay))]
    private int _currentResultIndex = -1;

    /// <summary>1-based index for display (0 when no result is active yet).</summary>
    public int CurrentResultDisplay => CurrentResultIndex < 0 ? 0 : CurrentResultIndex + 1;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private int _searchProgress;

    /// <summary>Total pages in the current document — used as Maximum for the search progress bar.</summary>
    [ObservableProperty]
    private int _totalPages;

    public bool HasResults => TotalResults > 0;
    public ObservableCollection<SearchResult> Results { get; } = [];

    public event EventHandler<SearchResult>? ResultNavigateRequested;

    public SearchViewModel(ISearchService searchService, ILogger<SearchViewModel> logger)
    {
        _searchService = searchService;
        _logger = logger;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        Results.Clear();
        TotalResults = 0;
        CurrentResultIndex = -1;
        IsSearching = true;
        SearchProgress = 0;

        try
        {
            var progress = new Progress<int>(p => SearchProgress = p);
            await foreach (var result in _searchService.SearchAsync(Query, CaseSensitive, progress, ct))
            {
                Results.Add(result);
                TotalResults = Results.Count;
            }
        }
        catch (OperationCanceledException) { /* search superseded */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for query '{Query}'", Query);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void NextResult()
    {
        if (!HasResults) return;
        CurrentResultIndex = (CurrentResultIndex + 1) % Results.Count;
        ResultNavigateRequested?.Invoke(this, Results[CurrentResultIndex]);
    }

    [RelayCommand]
    private void PreviousResult()
    {
        if (!HasResults) return;
        CurrentResultIndex = (CurrentResultIndex - 1 + Results.Count) % Results.Count;
        ResultNavigateRequested?.Invoke(this, Results[CurrentResultIndex]);
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _searchCts?.Cancel();
        Query = string.Empty;
        Results.Clear();
        TotalResults = 0;
        CurrentResultIndex = -1;
    }

    partial void OnQueryChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            ClearSearch();
    }
}
