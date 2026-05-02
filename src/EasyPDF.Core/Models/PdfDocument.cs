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
);
