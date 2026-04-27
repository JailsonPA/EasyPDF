using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface IRecentFilesRepository
{
    Task<IReadOnlyList<RecentFile>> GetAllAsync(CancellationToken ct = default);
    Task AddOrUpdateAsync(RecentFile file, CancellationToken ct = default);
    Task RemoveAsync(string filePath, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
