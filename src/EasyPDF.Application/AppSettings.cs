namespace EasyPDF.Application;

public sealed class AppSettings
{
    public long LargeFileSizeBytes { get; init; } = 500L * 1024 * 1024;
}
