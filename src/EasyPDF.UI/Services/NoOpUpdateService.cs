using EasyPDF.Application.Interfaces;

namespace EasyPDF.UI.Services;

internal sealed class NoOpUpdateService : IUpdateService
{
    public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default) =>
        Task.FromResult<UpdateInfo?>(null);

    public bool CanInstall => false;

    public Task DownloadUpdateAsync(UpdateInfo update, IProgress<int>? progress = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public void ApplyUpdateAndRestart() { }
}
