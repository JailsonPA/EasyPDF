using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using System.Text.Json;

namespace EasyPDF.Infrastructure.Storage;

public sealed class JsonAnnotationRepository : IAnnotationRepository
{
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<Annotation>> GetAllAsync(string documentPath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            return all.Where(a => a.DocumentPath.Equals(documentPath, StringComparison.OrdinalIgnoreCase))
                      .OrderBy(a => a.PageIndex)
                      .ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task AddAsync(Annotation annotation, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = await LoadAsync(ct);
            list.Add(annotation);
            await SaveAsync(list, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = await LoadAsync(ct);
            list.RemoveAll(a => a.Id == id);
            await SaveAsync(list, ct);
        }
        finally { _lock.Release(); }
    }

    private static async Task<List<Annotation>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(AppDataPaths.AnnotationsFile)) return [];
        await using var s = File.OpenRead(AppDataPaths.AnnotationsFile);
        return await JsonSerializer.DeserializeAsync<List<Annotation>>(s, _opts, ct) ?? [];
    }

    private static async Task SaveAsync(List<Annotation> list, CancellationToken ct)
    {
        await using var s = File.Create(AppDataPaths.AnnotationsFile);
        await JsonSerializer.SerializeAsync(s, list, _opts, ct);
    }
}
