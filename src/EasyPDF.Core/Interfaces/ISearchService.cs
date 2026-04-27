using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface ISearchService
{
    /// <summary>
    /// Searches the open document for <paramref name="query"/>.
    /// Reports progress via <paramref name="progress"/> as pages are scanned.
    /// </summary>
    IAsyncEnumerable<SearchResult> SearchAsync(
        string query,
        bool caseSensitive = false,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}
