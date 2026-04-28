namespace EasyPDF.Application;

public sealed class AppSettings
{
    /// <summary>Files above this size trigger a confirmation dialog before opening.</summary>
    public long LargeFileSizeBytes { get; init; } = 500L * 1024 * 1024;
}
