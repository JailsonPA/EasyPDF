using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface IAnnotationRepository
{
    Task<IReadOnlyList<Annotation>> GetAllAsync(string documentPath, CancellationToken ct = default);
    Task AddAsync(Annotation annotation, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
}
