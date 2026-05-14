using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyPDF.Infrastructure.Storage;

/// <summary>
/// SQLite-backed annotation store. Reads/writes a single annotation in O(log N) instead of
/// rewriting an entire JSON file (the JSON repo's worst case was O(total annotations across
/// every document) per click). Indexed columns for id and document_path; the variable-width
/// payload (quads, ink points, optional fields) is stored as a JSON blob.
/// </summary>
public sealed class SqliteAnnotationRepository : IAnnotationRepository, IDisposable
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _connectionString;

    public SqliteAnnotationRepository(string dbPath, string? legacyJsonPathForMigration = null)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        InitializeSchema();

        if (legacyJsonPathForMigration is not null && File.Exists(legacyJsonPathForMigration))
            TryImportFromJson(legacyJsonPathForMigration);
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        using (var cmd = conn.CreateCommand())
        {
            // WAL gives concurrent readers + faster writes than the default rollback journal.
            cmd.CommandText =
                """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS annotations (
                    id            TEXT PRIMARY KEY,
                    document_path TEXT NOT NULL,
                    page_index    INTEGER NOT NULL,
                    created_at    TEXT NOT NULL,
                    payload       TEXT NOT NULL,
                    content_hash  TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_annotations_doc
                    ON annotations(document_path COLLATE NOCASE);
                """;
            cmd.ExecuteNonQuery();
        }

        // Older v1 databases were created without `content_hash`. Add it idempotently.
        EnsureContentHashColumn(conn);

        using (var idx = conn.CreateCommand())
        {
            idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_annotations_hash ON annotations(content_hash)";
            idx.ExecuteNonQuery();
        }
    }

    private static void EnsureContentHashColumn(SqliteConnection conn)
    {
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(annotations)";
        using var reader = pragma.ExecuteReader();
        while (reader.Read())
        {
            // column 1 ("name") in PRAGMA table_info output
            if (string.Equals(reader.GetString(1), "content_hash", StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();

        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE annotations ADD COLUMN content_hash TEXT";
        alter.ExecuteNonQuery();
    }

    private void TryImportFromJson(string jsonPath)
    {
        try
        {
            using var conn = OpenConnection();
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM annotations LIMIT 1";
                if ((long)(check.ExecuteScalar() ?? 0L) > 0) return; // already populated; don't re-import
            }

            // Read + close immediately. Don't keep the stream open in function scope —
            // a `using var stream` at this level would still be holding the file when we
            // later try `File.Move(jsonPath, ...)`, and on Windows that move would fail
            // silently (caught + swallowed below), leaving an orphan legacy file.
            List<Annotation>? legacy;
            using (var stream = File.OpenRead(jsonPath))
            {
                legacy = JsonSerializer.Deserialize<List<Annotation>>(stream, _jsonOpts);
            }
            if (legacy is null || legacy.Count == 0) return;

            using var tx = conn.BeginTransaction();
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT OR REPLACE INTO annotations (id, document_path, page_index, created_at, payload) " +
                "VALUES ($id, $doc, $page, $created, $payload)";
            var pId      = insert.Parameters.Add("$id",      SqliteType.Text);
            var pDoc     = insert.Parameters.Add("$doc",     SqliteType.Text);
            var pPage    = insert.Parameters.Add("$page",    SqliteType.Integer);
            var pCreated = insert.Parameters.Add("$created", SqliteType.Text);
            var pPayload = insert.Parameters.Add("$payload", SqliteType.Text);

            foreach (var ann in legacy)
            {
                pId.Value      = ann.Id.ToString();
                pDoc.Value     = ann.DocumentPath;
                pPage.Value    = ann.PageIndex;
                pCreated.Value = ann.CreatedAt.ToString("O"); // round-trippable ISO 8601
                pPayload.Value = JsonSerializer.Serialize(ann, _jsonOpts);
                insert.ExecuteNonQuery();
            }
            tx.Commit();

            // Rename the legacy file rather than deleting — leaves a recoverable backup.
            try { File.Move(jsonPath, jsonPath + ".migrated", overwrite: true); }
            catch { /* non-critical */ }
        }
        catch
        {
            // Migration is best-effort; if the legacy file is corrupt the user simply starts
            // with an empty store rather than crashing on every launch.
        }
    }

    public async Task<IReadOnlyList<Annotation>> GetAllAsync(string documentPath, string? contentHash = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();

            // Fast path: lookup by current path.
            var byPath = await QueryAsync(conn,
                "SELECT payload FROM annotations WHERE document_path = $doc COLLATE NOCASE ORDER BY page_index, created_at",
                ("$doc", documentPath), ct);

            if (byPath.Count > 0)
            {
                // Heal: if any existing rows lack a hash, populate it now so future renames are safe.
                if (contentHash is not null)
                    await BackfillContentHashAsync(conn, documentPath, contentHash, ct);
                return byPath;
            }

            // Fallback: path didn't match (file was renamed/moved). Try by content hash.
            if (contentHash is null) return byPath;

            var byHash = await QueryAsync(conn,
                "SELECT payload FROM annotations WHERE content_hash = $hash ORDER BY page_index, created_at",
                ("$hash", contentHash), ct);

            if (byHash.Count == 0) return byHash;

            // Heal: relocate rows to the new path so the fast path works next time.
            await ExecuteAsync(conn,
                "UPDATE annotations SET document_path = $doc WHERE content_hash = $hash",
                ct, ("$doc", documentPath), ("$hash", contentHash));

            return byHash;
        }
        finally { _lock.Release(); }
    }

    private static async Task<List<Annotation>> QueryAsync(
        SqliteConnection conn, string sql, (string Name, object Value) param, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(param.Name, param.Value);

        var results = new List<Annotation>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var ann = JsonSerializer.Deserialize<Annotation>(reader.GetString(0), _jsonOpts);
            if (ann is not null) results.Add(ann);
        }
        return results;
    }

    private static async Task ExecuteAsync(
        SqliteConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BackfillContentHashAsync(
        SqliteConnection conn, string documentPath, string contentHash, CancellationToken ct)
    {
        await ExecuteAsync(conn,
            "UPDATE annotations SET content_hash = $hash WHERE document_path = $doc COLLATE NOCASE AND content_hash IS NULL",
            ct, ("$hash", contentHash), ("$doc", documentPath));
    }

    public async Task AddAsync(Annotation annotation, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT OR REPLACE INTO annotations (id, document_path, page_index, created_at, payload, content_hash) " +
                "VALUES ($id, $doc, $page, $created, $payload, $hash)";
            cmd.Parameters.AddWithValue("$id",      annotation.Id.ToString());
            cmd.Parameters.AddWithValue("$doc",     annotation.DocumentPath);
            cmd.Parameters.AddWithValue("$page",    annotation.PageIndex);
            cmd.Parameters.AddWithValue("$created", annotation.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(annotation, _jsonOpts));
            cmd.Parameters.AddWithValue("$hash",    (object?)annotation.ContentHash ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _lock.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM annotations WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _lock.Release(); }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void Dispose() => _lock.Dispose();
}
