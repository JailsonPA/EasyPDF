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
    private readonly IPreferencesRepository _prefsRepo;
    private CancellationTokenSource? _searchCts;

    // Skips persistence while the constructor seeds CaseSensitive from saved prefs —
    // otherwise the field assignment would immediately echo the loaded value back to disk.
    private bool _suppressPrefsPersistence = true;

    private sealed class NoOpPreferences : IPreferencesRepository
    {
        public UserPreferences Get() => new();
        public Task SaveAsync(UserPreferences prefs, CancellationToken ct = default) => Task.CompletedTask;
    }

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private int _totalResults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentResultDisplay))]
    private int _currentResultIndex = -1;

    public int CurrentResultDisplay => CurrentResultIndex < 0 ? 0 : CurrentResultIndex + 1;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private int _searchProgress;

    [ObservableProperty]
    private int _totalPages;

    public bool HasResults => TotalResults > 0;
    public ObservableCollection<SearchResult> Results { get; } = [];

    public event EventHandler<SearchResult>? ResultNavigateRequested;

    public SearchViewModel(
        ISearchService searchService,
        ILogger<SearchViewModel> logger,
        IPreferencesRepository? prefsRepo = null)
    {
        _searchService = searchService;
        _logger = logger;
        _prefsRepo = prefsRepo ?? new NoOpPreferences();

        // Seed from saved prefs via the backing field so the change handler doesn't run.
        _caseSensitive = _prefsRepo.Get().SearchCaseSensitive;
        _suppressPrefsPersistence = false;
    }

    partial void OnCaseSensitiveChanged(bool value)
    {
        if (_suppressPrefsPersistence) return;
        var current = _prefsRepo.Get();
        if (current.SearchCaseSensitive == value) return;
        _ = _prefsRepo.SaveAsync(current with { SearchCaseSensitive = value });
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
