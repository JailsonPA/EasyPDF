namespace EasyPDF.Core.Models;

public sealed record RecentFile(
    string FilePath,
    string FileName,
    int PageCount,
    long FileSizeBytes,
    DateTime LastOpened,
    int LastPageIndex = 0
)
{
    /// <summary>SHA-256 fingerprint of the source PDF — used to relocate the entry across renames.</summary>
    public string? ContentHash { get; init; }
}
