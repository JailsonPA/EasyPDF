using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using MuPDFCore;
using MuPDFCore.StructuredText;
using System.Text.RegularExpressions;

namespace EasyPDF.Infrastructure.Pdf;

public sealed class MuPdfSearchService : ISearchService
{
    private readonly MuPdfDocumentService _docService;
    private readonly MuPdfDispatcher _dispatcher;
    private readonly ILogger<MuPdfSearchService> _logger;

    public MuPdfSearchService(
        MuPdfDocumentService docService,
        MuPdfDispatcher dispatcher,
        ILogger<MuPdfSearchService> logger)
    {
        _docService = docService;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query,
        bool caseSensitive = false,
        IProgress<int>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !_docService.IsOpen)
            yield break;

        var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        var needle = new Regex(Regex.Escape(query), options);

        int pageCount = _docService.UseDocument(doc => doc.Pages.Count);

        for (int i = 0; i < pageCount && !ct.IsCancellationRequested; i++)
        {
            progress?.Report(i);

            // Route through the dispatcher — GetStructuredTextPage parses the content
            // stream and can recurse deeply enough to overflow a 1 MB thread-pool stack.
            var results = await _dispatcher.RunAsync(() => SearchPage(i, needle), ct);

            foreach (var r in results)
                yield return r;
        }
    }

    private List<SearchResult> SearchPage(int pageIndex, Regex needle)
    {
        return _docService.UseDocument(doc =>
        {
            var results = new List<SearchResult>();
            try
            {
                using var stp = doc.GetStructuredTextPage(pageIndex, false, StructuredTextFlags.None);

                int matchIdx = 0;
                foreach (var span in stp.Search(needle))
                {
                    var quads = new List<PdfRect>();
                    foreach (var quad in stp.GetHighlightQuads(span, false))
                    {
                        float x = Math.Min(quad.UpperLeft.X, quad.LowerLeft.X);
                        float y = Math.Min(quad.UpperLeft.Y, quad.UpperRight.Y);
                        float w = Math.Max(quad.UpperRight.X, quad.LowerRight.X) - x;
                        float h = Math.Max(quad.LowerLeft.Y, quad.LowerRight.Y) - y;
                        quads.Add(new PdfRect(x, y, w, h));
                    }
                    results.Add(new SearchResult(pageIndex, matchIdx++, needle.ToString(), quads));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Search skipped on page {Page} — no text layer or error",
                    pageIndex);
            }
            return results;
        });
    }
}
