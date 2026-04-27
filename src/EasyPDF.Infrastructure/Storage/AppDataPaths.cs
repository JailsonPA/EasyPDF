namespace EasyPDF.Infrastructure.Storage;

public static class AppDataPaths
{
    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyPDF");

    public static string BookmarksFile => Ensure(Path.Combine(Root, "bookmarks.json"));
    public static string AnnotationsFile => Ensure(Path.Combine(Root, "annotations.json"));
    public static string RecentFilesFile => Ensure(Path.Combine(Root, "recent.json"));
    public static string SettingsFile => Ensure(Path.Combine(Root, "settings.json"));

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
