namespace EasyPDF.Infrastructure.Storage;

public static class AppDataPaths
{
    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyPDF");

    static AppDataPaths() => Directory.CreateDirectory(Root);

    public static string BookmarksFile    => Path.Combine(Root, "bookmarks.json");
    public static string AnnotationsFile  => Path.Combine(Root, "annotations.json");
    public static string AnnotationsDb    => Path.Combine(Root, "annotations.db");
    public static string RecentFilesFile  => Path.Combine(Root, "recent.json");
    public static string SettingsFile     => Path.Combine(Root, "settings.json");
    public static string WindowFile       => Path.Combine(Root, "window.json");
    public static string LogsDirectory    => Path.Combine(Root, "logs");
}
