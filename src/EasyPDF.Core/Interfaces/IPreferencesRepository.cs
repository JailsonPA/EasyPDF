using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface IPreferencesRepository
{
    /// Returns the cached preferences (loaded from disk on first call). Safe to call
    /// repeatedly; subsequent calls are O(1).
    UserPreferences Get();

    /// Persists the given preferences to disk and updates the in-memory cache.
    /// Errors are swallowed — preference loss is non-critical, but the in-memory
    /// cache is always updated so the current session sees the change.
    Task SaveAsync(UserPreferences prefs, CancellationToken ct = default);
}
