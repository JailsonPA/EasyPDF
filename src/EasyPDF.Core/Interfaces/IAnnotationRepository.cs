using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface IAnnotationRepository
{
    /// <summary>
    /// Returns annotations for the document. Tries <paramref name="documentPath"/> first.
    /// If empty AND <paramref name="contentHash"/> is provided, falls back to a hash lookup —
    /// when annotations are found by hash with a different stored path, the path is silently
    /// updated to <paramref name="documentPath"/> so subsequent lookups hit the fast path.
    /// </summary>
    Task<IReadOnlyList<Annotation>> GetAllAsync(string documentPath, string? contentHash = null, CancellationToken ct = default);
    Task AddAsync(Annotation annotation, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
}
