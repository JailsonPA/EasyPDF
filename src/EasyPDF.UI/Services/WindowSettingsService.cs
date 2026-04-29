using EasyPDF.Infrastructure.Storage;
using System.IO;
using System.Text.Json;

namespace EasyPDF.UI.Services;

/// <summary>Persists window placement (size, position, state) across sessions.</summary>
internal static class WindowSettingsService
{
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public static WindowPlacement? Load()
    {
        try
        {
            string path = AppDataPaths.WindowFile;
            if (!File.Exists(path)) return null;
            using var s = File.OpenRead(path);
            return JsonSerializer.Deserialize<WindowPlacement>(s, _opts);
        }
        catch { return null; }
    }

    public static async void SaveAsync(WindowPlacement placement)
    {
        try
        {
            await using var s = File.Create(AppDataPaths.WindowFile);
            await JsonSerializer.SerializeAsync(s, placement, _opts);
        }
        catch { /* non-critical */ }
    }
}

internal sealed class WindowPlacement
{
    public double Left   { get; set; }
    public double Top    { get; set; }
    public double Width  { get; set; }
    public double Height { get; set; }
    public string State  { get; set; } = "Normal";
}
