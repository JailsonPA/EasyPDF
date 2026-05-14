using EasyPDF.Core.Models;
using EasyPDF.Infrastructure.Storage;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace EasyPDF.Tests.Storage;

/// <summary>
/// Tests for the SQLite-backed annotation store. Each test gets a unique temp .db so
/// they're isolated and parallelizable. WAL sidecar files are cleaned up too.
///
/// Covers: round-trip, INSERT-OR-REPLACE behavior (used by Change Color / Edit Note),
/// case-insensitive path lookup, content-hash fallback on rename + path healing,
/// document isolation, and legacy JSON migration.
/// </summary>
public sealed class SqliteAnnotationRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly List<IDisposable> _toDispose = new();

    public SqliteAnnotationRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"easypdf-test-{Guid.NewGuid()}.db");
    }

    public void Dispose()
    {
        foreach (var d in _toDispose) d.Dispose();
        // SQLite holds pool connections; force them released so the file can be deleted.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        TryDeleteQuietly(_dbPath);
        TryDeleteQuietly(_dbPath + "-wal");
        TryDeleteQuietly(_dbPath + "-shm");
    }

    private static void TryDeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup; OS will reap the temp file eventually */ }
    }

    private SqliteAnnotationRepository MakeRepo(string? legacyJsonPath = null)
    {
        var repo = new SqliteAnnotationRepository(_dbPath, legacyJsonPath);
        _toDispose.Add(repo);
        return repo;
    }

    private static Annotation MakeAnnotation(
        string docPath = "test.pdf",
        int page = 0,
        AnnotationType type = AnnotationType.Highlight,
        AnnotationColor color = AnnotationColor.Yellow,
        string? contentHash = null,
        Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            docPath,
            page,
            type,
            color,
            [new PdfRect(10, 20, 100, 30)],
            DateTime.UtcNow)
        { ContentHash = contentHash };

    // ─── Basic round-trip ─────────────────────────────────────────────────────

    [Fact]
    public async Task Add_Then_GetAll_ReturnsAnnotation()
    {
        var repo = MakeRepo();
        var ann = MakeAnnotation();

        await repo.AddAsync(ann);
        var result = await repo.GetAllAsync(ann.DocumentPath);

        Assert.Single(result);
        Assert.Equal(ann.Id, result[0].Id);
        Assert.Equal(ann.Color, result[0].Color);
        Assert.Equal(ann.Type, result[0].Type);
    }

    [Fact]
    public async Task GetAll_OnEmptyDatabase_ReturnsEmpty()
    {
        var repo = MakeRepo();
        var result = await repo.GetAllAsync("nothing.pdf");
        Assert.Empty(result);
    }

    [Fact]
    public async Task Remove_DeletesById()
    {
        var repo = MakeRepo();
        var a = MakeAnnotation();
        var b = MakeAnnotation();

        await repo.AddAsync(a);
        await repo.AddAsync(b);
        await repo.RemoveAsync(a.Id);

        var result = await repo.GetAllAsync("test.pdf");
        Assert.Single(result);
        Assert.Equal(b.Id, result[0].Id);
    }

    [Fact]
    public async Task Remove_NonExistentId_DoesNotThrow()
    {
        var repo = MakeRepo();
        var ex = await Record.ExceptionAsync(() => repo.RemoveAsync(Guid.NewGuid()));
        Assert.Null(ex);
    }

    // ─── INSERT OR REPLACE semantics (used by ChangeColor / EditNote) ─────────

    [Fact]
    public async Task Add_ExistingId_ReplacesPayloadInPlace()
    {
        var repo = MakeRepo();
        var id = Guid.NewGuid();
        var original = MakeAnnotation(id: id, color: AnnotationColor.Yellow);
        var updated  = original with { Color = AnnotationColor.Green };

        await repo.AddAsync(original);
        await repo.AddAsync(updated);

        var result = await repo.GetAllAsync("test.pdf");
        Assert.Single(result);
        Assert.Equal(AnnotationColor.Green, result[0].Color);
    }

    // ─── Lookup semantics ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_PathLookupIsCaseInsensitive()
    {
        var repo = MakeRepo();
        var ann = MakeAnnotation(docPath: "C:/docs/Report.pdf");

        await repo.AddAsync(ann);
        var result = await repo.GetAllAsync("c:/docs/report.PDF");

        Assert.Single(result);
    }

    [Fact]
    public async Task GetAll_DifferentDocuments_AreIsolated()
    {
        var repo = MakeRepo();
        var docA = MakeAnnotation(docPath: "a.pdf");
        var docB = MakeAnnotation(docPath: "b.pdf");

        await repo.AddAsync(docA);
        await repo.AddAsync(docB);

        var resultA = await repo.GetAllAsync("a.pdf");
        var resultB = await repo.GetAllAsync("b.pdf");

        Assert.Single(resultA);
        Assert.Single(resultB);
        Assert.Equal(docA.Id, resultA[0].Id);
        Assert.Equal(docB.Id, resultB[0].Id);
    }

    [Fact]
    public async Task GetAll_NoMatchAtAll_ReturnsEmpty()
    {
        var repo = MakeRepo();
        await repo.AddAsync(MakeAnnotation(docPath: "/some/path.pdf", contentHash: "abc"));

        // Different path AND different hash → nothing
        var result = await repo.GetAllAsync("/other.pdf", contentHash: "xyz");
        Assert.Empty(result);
    }

    // ─── Hash fallback on rename + path healing ──────────────────────────────

    [Fact]
    public async Task GetAll_PathMissButHashHits_FindsAnnotationsByHash()
    {
        var repo = MakeRepo();
        var ann = MakeAnnotation(docPath: "/old/path.pdf", contentHash: "abc123");

        await repo.AddAsync(ann);

        // User renamed/moved the file. Path doesn't match — hash does.
        var result = await repo.GetAllAsync("/new/path.pdf", contentHash: "abc123");

        Assert.Single(result);
        Assert.Equal(ann.Id, result[0].Id);
    }

    [Fact]
    public async Task GetAll_HashFallback_HealsPathForSubsequentLookups()
    {
        var repo = MakeRepo();
        var ann = MakeAnnotation(docPath: "/old/path.pdf", contentHash: "abc123");

        await repo.AddAsync(ann);
        // First call triggers the fallback + heal
        await repo.GetAllAsync("/new/path.pdf", contentHash: "abc123");
        // Second call uses just the new path — no hash. Must still find the row
        // because the previous call rewrote document_path on disk.
        var result = await repo.GetAllAsync("/new/path.pdf");

        Assert.Single(result);
    }

    // ─── Annotation payload fidelity ─────────────────────────────────────────

    [Fact]
    public async Task Add_InkAnnotation_RoundTripsAllFields()
    {
        var repo = MakeRepo();
        var ann = new Annotation(
            Guid.NewGuid(),
            "ink.pdf",
            0,
            AnnotationType.Ink,
            AnnotationColor.Blue,
            [new PdfRect(10, 20, 100, 50)],
            DateTime.UtcNow,
            InkPoints: [new PdfPoint(10, 20), new PdfPoint(50, 40), new PdfPoint(100, 70)],
            InkThickness: 3.5,
            StrokeColor: "#FFE11D48");

        await repo.AddAsync(ann);
        var result = await repo.GetAllAsync("ink.pdf");

        Assert.Single(result);
        var loaded = result[0];
        Assert.Equal(AnnotationType.Ink, loaded.Type);
        Assert.Equal(3, loaded.InkPoints?.Count);
        Assert.Equal(3.5, loaded.InkThickness);
        Assert.Equal("#FFE11D48", loaded.StrokeColor);
    }

    [Fact]
    public async Task Add_NoteAnnotation_PreservesNoteContent()
    {
        var repo = MakeRepo();
        var ann = new Annotation(
            Guid.NewGuid(),
            "note.pdf",
            2,
            AnnotationType.Note,
            AnnotationColor.Yellow,
            [new PdfRect(50, 50, 120, 80)],
            DateTime.UtcNow,
            NoteContent: "Important: review before Friday");

        await repo.AddAsync(ann);
        var result = await repo.GetAllAsync("note.pdf");

        Assert.Single(result);
        Assert.Equal("Important: review before Friday", result[0].NoteContent);
    }

    // ─── Legacy JSON migration ───────────────────────────────────────────────

    [Fact]
    public async Task LegacyJsonMigration_PopulatesEmptyDbAndRenamesFile()
    {
        var legacyPath = Path.Combine(Path.GetTempPath(), $"easypdf-legacy-{Guid.NewGuid()}.json");
        var legacyAnn = MakeAnnotation(docPath: "legacy.pdf");
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new[] { legacyAnn }, opts));

        try
        {
            var repo = MakeRepo(legacyJsonPath: legacyPath);
            var result = await repo.GetAllAsync("legacy.pdf");

            Assert.Single(result);
            Assert.Equal(legacyAnn.Id, result[0].Id);

            // Source file renamed (preserved as .migrated backup, not deleted).
            Assert.False(File.Exists(legacyPath));
            Assert.True(File.Exists(legacyPath + ".migrated"));
        }
        finally
        {
            TryDeleteQuietly(legacyPath);
            TryDeleteQuietly(legacyPath + ".migrated");
        }
    }

    [Fact]
    public async Task LegacyJsonMigration_SkipsWhenDatabaseAlreadyPopulated()
    {
        // Seed the DB so migration sees a non-empty table and skips.
        {
            var first = MakeRepo();
            await first.AddAsync(MakeAnnotation(docPath: "preexisting.pdf"));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var legacyPath = Path.Combine(Path.GetTempPath(), $"easypdf-legacy-{Guid.NewGuid()}.json");
        var legacyAnn = MakeAnnotation(docPath: "legacy.pdf");
        var opts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new[] { legacyAnn }, opts));

        try
        {
            var repo = MakeRepo(legacyJsonPath: legacyPath);
            var legacyResult = await repo.GetAllAsync("legacy.pdf");

            Assert.Empty(legacyResult);              // not imported
            Assert.True(File.Exists(legacyPath));    // not renamed
        }
        finally
        {
            TryDeleteQuietly(legacyPath);
            TryDeleteQuietly(legacyPath + ".migrated");
        }
    }

    [Fact]
    public async Task LegacyJsonMigration_MissingFile_IsNoOp()
    {
        var missingLegacy = Path.Combine(Path.GetTempPath(), $"easypdf-nonexistent-{Guid.NewGuid()}.json");
        var repo = MakeRepo(legacyJsonPath: missingLegacy);

        var result = await repo.GetAllAsync("anything.pdf");
        Assert.Empty(result);
    }
}
