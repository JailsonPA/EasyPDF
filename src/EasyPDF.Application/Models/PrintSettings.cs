namespace EasyPDF.Application.Models;

public enum PrintRangeKind
{
    AllPages,
    CurrentPage,
    CustomRange,
    OddPagesOnly,
    EvenPagesOnly,
}

/// How a rendered PDF page is sized onto the printer's printable area.
public enum PrintFitMode
{
    /// Print at the PDF's native size — printer driver may clip if larger than paper.
    Actual,
    /// Scale down if the page is bigger than the printable area; never scale up.
    ShrinkToFit,
    /// Scale to fill the printable area (up or down), preserving aspect ratio.
    FitToPage,
}

public sealed record PrintSettings
{
    public PrintRangeKind Range { get; init; } = PrintRangeKind.AllPages;

    /// User-typed custom range like "1-3,5,7-9". Only consulted when Range == CustomRange.
    public string CustomRange { get; init; } = "";

    public PrintFitMode FitMode { get; init; } = PrintFitMode.ShrinkToFit;

    public bool IncludeAnnotations { get; init; } = true;
}

/// <summary>
/// Parses Adobe-style page range strings into zero-based page indices.
/// Examples (over a 10-page doc):
///   "1-3,5,7-9"   → [0,1,2,4,6,7,8]
///   "all" / ""    → all pages
///   "1,1,1"       → [0]    (dedup, sorted)
///   "5-3"         → []     (invalid range, dropped)
///   "100"         → []     (out of bounds, dropped)
/// </summary>
public static class PageRangeParser
{
    public static IReadOnlyList<int> Parse(string range, int totalPages)
    {
        if (totalPages <= 0) return [];
        if (string.IsNullOrWhiteSpace(range)) return Enumerable.Range(0, totalPages).ToArray();

        var set = new SortedSet<int>();
        foreach (var rawToken in range.Split(','))
        {
            var token = rawToken.Trim();
            if (token.Length == 0) continue;

            int dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(token, out int single))
                    AddIfInBounds(set, single - 1, totalPages);
                continue;
            }

            // Range "a-b" — left and right both required and valid
            if (!int.TryParse(token[..dash], out int from)) continue;
            if (!int.TryParse(token[(dash + 1)..], out int to)) continue;
            if (to < from) continue;  // silently drop reversed ranges

            for (int i = from; i <= to; i++)
                AddIfInBounds(set, i - 1, totalPages);
        }
        return set.ToArray();
    }

    private static void AddIfInBounds(SortedSet<int> set, int zeroBased, int total)
    {
        if (zeroBased >= 0 && zeroBased < total)
            set.Add(zeroBased);
    }
}
