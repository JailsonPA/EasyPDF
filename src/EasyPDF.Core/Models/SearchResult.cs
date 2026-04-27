namespace EasyPDF.Core.Models;

public sealed record SearchResult(
    int PageIndex,
    int MatchIndex,
    string MatchedText,
    IReadOnlyList<PdfRect> Quads   // can be multiple quads for multi-line match
);
