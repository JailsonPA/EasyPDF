using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using System.Text.Json;

namespace EasyPDF.Infrastructure.Storage;

public sealed class JsonBookmarkRepository : IBookmarkRepository
{
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _filePath;

    public JsonBookmarkRepository(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<IReadOnlyList<Bookmark>> GetAllAsync(string documentPath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            return all.Where(b => b.DocumentPath.Equals(documentPath, StringComparison.OrdinalIgnoreCase))
                      .OrderBy(b => b.PageIndex)
                      .ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task AddAsync(Bookmark bookmark, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = await LoadAsync(ct);
            list.Add(bookmark);
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
            list.RemoveAll(b => b.Id == id);
            await SaveAsync(list, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateAsync(Bookmark bookmark, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = await LoadAsync(ct);
            int idx = list.FindIndex(b => b.Id == bookmark.Id);
            if (idx >= 0) list[idx] = bookmark;
            await SaveAsync(list, ct);
        }
        finally { _lock.Release(); }
    }

    private async Task<List<Bookmark>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return [];
        await using var s = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<Bookmark>>(s, _opts, ct) ?? [];
    }

    private async Task SaveAsync(List<Bookmark> list, CancellationToken ct)
    {
        await using var s = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(s, list, _opts, ct);
    }
}
