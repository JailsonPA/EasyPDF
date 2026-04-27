namespace EasyPDF.Core.Models;

public sealed record RecentFile(
    string FilePath,
    string FileName,
    int PageCount,
    long FileSizeBytes,
    DateTime LastOpened,
    int LastPageIndex = 0
);
