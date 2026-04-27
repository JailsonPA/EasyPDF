using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface IBookmarkRepository
{
    Task<IReadOnlyList<Bookmark>> GetAllAsync(string documentPath, CancellationToken ct = default);
    Task AddAsync(Bookmark bookmark, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
    Task UpdateAsync(Bookmark bookmark, CancellationToken ct = default);
}
