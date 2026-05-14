namespace EasyPDF.Core.Models;

public sealed record PdfDocument(
    string FilePath,
    string FileName,
    int PageCount,
    long FileSizeBytes,
    DateTime OpenedAt,
    IReadOnlyList<PdfPageInfo> Pages,
    IReadOnlyList<TocEntry> TableOfContents,
    string PdfVersion   = "Unknown",
    bool IsEncrypted    = false,
    bool IsRestricted   = false
)
{
    /// <summary>SHA-256 of the first 1 MB of the file — stable across path renames.</summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// True when at least one of the sampled pages has an extractable text layer.
    /// False indicates a scanned/image-only or vector-outlined PDF (e.g. exports from Canva)
    /// where selection, search and highlights silently produce no results — the UI surfaces
    /// a banner so the user understands why those features are not working.
    /// </summary>
    public bool HasTextLayer { get; init; } = true;
}
