using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyPDF.Infrastructure.Storage;

/// <summary>
/// File-backed user preferences. Writes <see cref="UserPreferences"/> as JSON
/// to a single file (defaults to %APPDATA%\EasyPDF\settings.json).
///
/// JsonStringEnumConverter is configured with allowIntegerValues=true (the default)
/// so the legacy schema written by the old theme service (numeric enum values like
/// <c>{ "Theme": 1 }</c>) still loads correctly. Missing fields in the JSON take
/// the record's declared defaults.
/// </summary>
public sealed class JsonPreferencesRepository : IPreferencesRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _lock = new();
    private UserPreferences? _cached;

    public JsonPreferencesRepository(string path)
    {
        _path = path;
    }

    public UserPreferences Get()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;

            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    _cached = JsonSerializer.Deserialize<UserPreferences>(json, JsonOpts) ?? new UserPreferences();
                }
                else
                {
                    _cached = new UserPreferences();
                }
            }
            catch
            {
                // Corrupt or unreadable file — start fresh. Don't crash startup over a settings file.
                _cached = new UserPreferences();
            }
            return _cached;
        }
    }

    public async Task SaveAsync(UserPreferences prefs, CancellationToken ct = default)
    {
        lock (_lock) { _cached = prefs; }

        try
        {
            var json = JsonSerializer.Serialize(prefs, JsonOpts);
            await File.WriteAllTextAsync(_path, json, ct).ConfigureAwait(false);
        }
        catch
        {
            // Persistence is best-effort. The in-memory cache still has the change so the
            // running session honors the new prefs even if disk write fails.
        }
    }
}
