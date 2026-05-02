namespace EasyPDF.Application.Interfaces;

public record UpdateInfo(string Version, string ReleaseUrl);

public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);

    bool CanInstall { get; }

    Task DownloadUpdateAsync(UpdateInfo update, IProgress<int>? progress = null, CancellationToken ct = default);

    void ApplyUpdateAndRestart();
}
