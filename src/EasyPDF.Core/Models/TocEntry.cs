namespace EasyPDF.Core.Models;

public sealed record TocEntry(
    string Title,
    int PageIndex,
    int Level,
    IReadOnlyList<TocEntry> Children
);
