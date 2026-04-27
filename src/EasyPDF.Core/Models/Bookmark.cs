namespace EasyPDF.Core.Models;

public sealed record Bookmark(
    Guid Id,
    string DocumentPath,
    int PageIndex,
    string Title,
    DateTime CreatedAt,
    string? Notes = null
);
