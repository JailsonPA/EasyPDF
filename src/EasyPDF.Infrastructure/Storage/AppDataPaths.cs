namespace EasyPDF.Infrastructure.Storage;

public static class AppDataPaths
{
    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyPDF");

    // Static constructor runs exactly once per AppDomain — guarantees the directory
    // exists before any path property is accessed, without repeating the syscall.
    static AppDataPaths() => Directory.CreateDirectory(Root);

    public static string BookmarksFile  => Path.Combine(Root, "bookmarks.json");
    public static string RecentFilesFile => Path.Combine(Root, "recent.json");
    public static string SettingsFile    => Path.Combine(Root, "settings.json");
    public static string WindowFile      => Path.Combine(Root, "window.json");
    public static string LogsDirectory   => Path.Combine(Root, "logs");
}
