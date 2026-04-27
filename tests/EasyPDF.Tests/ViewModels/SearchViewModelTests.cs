using EasyPDF.Application.ViewModels;
using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using Xunit;

namespace EasyPDF.Tests.ViewModels;

public sealed class SearchViewModelTests
{
    private static SearchViewModel Make(params SearchResult[] results) =>
        new(new FakeSearchService(results), NullLogger<SearchViewModel>.Instance);

    // ─── ClearSearch ────────────────────────────────────────────────────────

    [Fact]
    public void ClearSearch_ResetsAllState()
    {
        var vm = Make();
        vm.Query = "hello";

        vm.ClearSearchCommand.Execute(null);

        Assert.Equal(string.Empty, vm.Query);
        Assert.Empty(vm.Results);
        Assert.Equal(0, vm.TotalResults);
        Assert.Equal(-1, vm.CurrentResultIndex);
        Assert.False(vm.HasResults);
    }

    [Fact]
    public void OnQueryChanged_EmptyQuery_ClearsResults()
    {
        var vm = Make(new SearchResult(0, 0, "x", []));
        vm.Results.Add(new SearchResult(0, 0, "x", []));
        vm.TotalResults = 1;

        vm.Query = string.Empty; // triggers OnQueryChanged → ClearSearch

        Assert.Empty(vm.Results);
        Assert.Equal(0, vm.TotalResults);
    }

    // ─── SearchAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_PopulatesResults()
    {
        var expected = new[]
        {
            new SearchResult(0, 0, "hello", []),
            new SearchResult(1, 0, "hello", []),
        };
        var vm = Make(expected);
        vm.Query = "hello";

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.TotalResults);
        Assert.Equal(2, vm.Results.Count);
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_DoesNotSearch()
    {
        var vm = Make(new SearchResult(0, 0, "x", []));
        vm.Query = string.Empty;

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(vm.Results);
    }

    // ─── Navigation ──────────────────────────────────────────────────────────

    [Fact]
    public void NextResult_WrapsFromLastToFirst()
    {
        var vm = Make();
        vm.Results.Add(new SearchResult(0, 0, "q", []));
        vm.Results.Add(new SearchResult(1, 0, "q", []));
        vm.TotalResults = 2;

        vm.NextResultCommand.Execute(null); // -1 → 0
        vm.NextResultCommand.Execute(null); // 0 → 1
        vm.NextResultCommand.Execute(null); // 1 → 0 (wraps)

        Assert.Equal(0, vm.CurrentResultIndex);
    }

    [Fact]
    public void PreviousResult_WrapsFromFirstToLast()
    {
        var vm = Make();
        vm.Results.Add(new SearchResult(0, 0, "q", []));
        vm.Results.Add(new SearchResult(1, 0, "q", []));
        vm.TotalResults = 2;

        vm.NextResultCommand.Execute(null);     // -1 → 0 (first)
        vm.PreviousResultCommand.Execute(null); // 0 → 1 (wraps to last)

        Assert.Equal(1, vm.CurrentResultIndex);
    }

    [Fact]
    public void NextResult_WithNoResults_DoesNothing()
    {
        var vm = Make();

        vm.NextResultCommand.Execute(null);

        Assert.Equal(-1, vm.CurrentResultIndex);
    }

    [Fact]
    public void HasResults_IsTrueWhenTotalResultsIsPositive()
    {
        var vm = Make();
        Assert.False(vm.HasResults);

        vm.TotalResults = 1;

        Assert.True(vm.HasResults);
    }

    // ─── Fake dependency ─────────────────────────────────────────────────────

    private sealed class FakeSearchService(params SearchResult[] results) : ISearchService
    {
        public async IAsyncEnumerable<SearchResult> SearchAsync(
            string query,
            bool caseSensitive = false,
            IProgress<int>? progress = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); // ensure truly async
            foreach (var r in results)
            {
                ct.ThrowIfCancellationRequested();
                yield return r;
            }
        }
    }
}
