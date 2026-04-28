using EasyPDF.Core.Models;
using EasyPDF.Infrastructure.Storage;
using Xunit;

namespace EasyPDF.Tests.Storage;

/// <summary>
/// Each test gets an isolated temp file via TempFile, deleted on dispose
/// even if the test throws — prevents cross-test pollution.
/// </summary>
public sealed class JsonRecentFilesRepositoryTests : IDisposable
{
    private readonly TempFile _file = new();

    private JsonRecentFilesRepository Repo() => new(_file.Path);

    private static RecentFile MakeFile(string path, string name = "doc.pdf") =>
        new(path, name, 10, 1024, DateTime.UtcNow);

    public void Dispose() => _file.Dispose();

    // ─── GetAllAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_WhenFileAbsent_ReturnsEmpty()
    {
        File.Delete(_file.Path); // simulate first-run

        var result = await Repo().GetAllAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAll_AfterAdd_ReturnsSingleEntry()
    {
        var repo = Repo();
        await repo.AddOrUpdateAsync(MakeFile("C:/a.pdf"));

        var result = await repo.GetAllAsync();

        Assert.Single(result);
        Assert.Equal("C:/a.pdf", result[0].FilePath);
    }

    // ─── AddOrUpdateAsync — ordering ─────────────────────────────────────────

    [Fact]
    public async Task AddOrUpdate_NewEntries_AreInsertedAtFront()
    {
        var repo = Repo();
        await repo.AddOrUpdateAsync(MakeFile("C:/first.pdf"));
        await repo.AddOrUpdateAsync(MakeFile("C:/second.pdf"));

        var result = await repo.GetAllAsync();

        Assert.Equal("C:/second.pdf", result[0].FilePath); // most recent first
        Assert.Equal("C:/first.pdf",  result[1].FilePath);
    }

    [Fact]
    public async Task AddOrUpdate_ExistingPath_MovesToFront()
    {
        var repo = Repo();
        await repo.AddOrUpdateAsync(MakeFile("C:/a.pdf"));
        await repo.AddOrUpdateAsync(MakeFile("C:/b.pdf"));

        // Re-open a.pdf — it should jump back to position 0.
        await repo.AddOrUpdateAsync(MakeFile("C:/a.pdf"));

        var result = await repo.GetAllAsync();

        Assert.Equal(2, result.Count);               // no duplicate
        Assert.Equal("C:/a.pdf", result[0].FilePath); // moved to front
        Assert.Equal("C:/b.pdf", result[1].FilePath);
    }

    [Fact]
    public async Task AddOrUpdate_PathComparison_IsCaseInsensitive()
    {
        var repo = Repo();
        await repo.AddOrUpdateAsync(MakeFile("C:/Docs/File.pdf"));
        await repo.AddOrUpdateAsync(MakeFile("C:/docs/file.pdf")); // same path, different case

        var result = await repo.GetAllAsync();

        Assert.Single(result); // treated as same entry
    }

    // ─── AddOrUpdateAsync — capacity cap ─────────────────────────────────────

    [Fact]
    public async Task AddOrUpdate_ExceedingMaxEntries_RemovesOldest()
    {
        var repo = Repo();

        for (int i = 1; i <= 21; i++)
            await repo.AddOrUpdateAsync(MakeFile($"C:/file{i}.pdf", $"file{i}.pdf"));

        var result = await repo.GetAllAsync();

        Assert.Equal(20, result.Count);
        Assert.Equal("C:/file21.pdf", result[0].FilePath);  // most recent
        Assert.DoesNotContain(result, r => r.FilePath == "C:/file1.pdf"); // oldest dropped
    }

    // ─── RemoveAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_ExistingEntry_IsDeleted()
    {
        var repo = Repo();
        await repo.AddOrUpdateAsync(MakeFile("C:/a.pdf"));
        await repo.AddOrUpdateAsync(MakeFile("C:/b.pdf"));

        await repo.RemoveAsync("C:/a.pdf");

        var result = await repo.GetAllAsync();
        Assert.Single(result);
        Assert.Equal("C:/b.pdf", result[0].FilePath);
    }

    [Fact]
    public async Task Remove_CaseInsensitive_DeletesCorrectEntry()
    {
        var repo = Repo();
        await repo.AddOrUpdateAsync(MakeFile("C:/Docs/Report.pdf"));

        await repo.RemoveAsync("C:/docs/report.pdf"); // different casing

        Assert.Empty(await repo.GetAllAsync());
    }

    [Fact]
    public async Task Remove_NonExistentPath_DoesNotThrow()
    {
        var repo = Repo();
        await repo.AddOrUpdateAsync(MakeFile("C:/a.pdf"));

        await repo.RemoveAsync("C:/nonexistent.pdf");

        Assert.Single(await repo.GetAllAsync()); // original entry untouched
    }

    // ─── ClearAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_RemovesAllEntries()
    {
        var repo = Repo();
        await repo.AddOrUpdateAsync(MakeFile("C:/a.pdf"));
        await repo.AddOrUpdateAsync(MakeFile("C:/b.pdf"));

        await repo.ClearAsync();

        Assert.Empty(await repo.GetAllAsync());
    }

    // ─── JSON round-trip (persistence) ───────────────────────────────────────

    [Fact]
    public async Task Persistence_DataSurvivesNewInstance()
    {
        await Repo().AddOrUpdateAsync(MakeFile("C:/persist.pdf", "persist.pdf"));

        // Create a new repo instance pointing at the same file.
        var result = await Repo().GetAllAsync();

        Assert.Single(result);
        Assert.Equal("C:/persist.pdf", result[0].FilePath);
        Assert.Equal("persist.pdf",    result[0].FileName);
    }
}

/// <summary>Wraps a temp file path and deletes it on dispose.</summary>
internal sealed class TempFile : IDisposable
{
    public string Path { get; } = System.IO.Path.GetTempFileName();
    public void Dispose() { try { File.Delete(Path); } catch { } }
}
