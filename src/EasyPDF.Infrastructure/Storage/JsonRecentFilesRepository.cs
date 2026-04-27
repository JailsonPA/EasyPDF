using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using System.Text.Json;

namespace EasyPDF.Infrastructure.Storage;

public sealed class JsonRecentFilesRepository : IRecentFilesRepository
{
    private const int MaxEntries = 20;
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<RecentFile>> GetAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return await LoadAsync(ct); }
        finally { _lock.Release(); }
    }

    public async Task AddOrUpdateAsync(RecentFile file, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = (await LoadAsync(ct)).ToList();
            list.RemoveAll(f => f.FilePath.Equals(file.FilePath, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, file);
            if (list.Count > MaxEntries)
                list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            await SaveAsync(list, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task RemoveAsync(string filePath, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = (await LoadAsync(ct)).ToList();
            list.RemoveAll(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            await SaveAsync(list, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { await SaveAsync([], ct); }
        finally { _lock.Release(); }
    }

    private static async Task<List<RecentFile>> LoadAsync(CancellationToken ct)
    {
        var path = AppDataPaths.RecentFilesFile;
        if (!File.Exists(path)) return [];
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<RecentFile>>(stream, _opts, ct) ?? [];
    }

    private static async Task SaveAsync(List<RecentFile> list, CancellationToken ct)
    {
        await using var stream = File.Create(AppDataPaths.RecentFilesFile);
        await JsonSerializer.SerializeAsync(stream, list, _opts, ct);
    }
}
