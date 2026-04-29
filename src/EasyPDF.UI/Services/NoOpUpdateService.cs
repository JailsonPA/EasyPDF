using EasyPDF.Application.Interfaces;

namespace EasyPDF.UI.Services;

internal sealed class NoOpUpdateService : IUpdateService
{
    public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default) =>
        Task.FromResult<UpdateInfo?>(null);
}
