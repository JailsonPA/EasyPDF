using Microsoft.Win32;
using System.IO;

namespace EasyPDF.UI.Services;

internal static class WindowsFileAssociationService
{
    private const string ProgId     = "EasyPDF.Document";
    private const string AppExeKey  = @"Software\Classes\Applications\EasyPDF.exe";
    private const string LastExeKey = @"Software\EasyPDF";

    internal static void EnsureRegistered()
    {
        try
        {
            string exe = GetExePath();

            using (var meta = Registry.CurrentUser.OpenSubKey(LastExeKey))
            {
                if (meta?.GetValue("RegisteredExe") is string last && last == exe)
                    return;
            }

            RegisterProgId(exe);
            RegisterApplication(exe);
            PersistRegisteredPath(exe);

            const int SHCNE_ASSOCCHANGED = 0x08000000;
            NativeMethods.SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Non-critical — app works fine without shell integration.
        }
    }

    private static void RegisterProgId(string exe)
    {
        using var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}");
        progId.SetValue("", "PDF Document");

        using var icon = progId.CreateSubKey("DefaultIcon");
        icon.SetValue("", $"\"{exe}\",0");

        using var open = progId.CreateSubKey(@"shell\open");
        open.SetValue("FriendlyAppName", "EasyPDF");

        using var cmd = open.CreateSubKey("command");
        cmd.SetValue("", $"\"{exe}\" \"%1\"");

        using var pdfWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids");
        pdfWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    private static void RegisterApplication(string exe)
    {
        using var appKey = Registry.CurrentUser.CreateSubKey(AppExeKey);
        appKey.SetValue("FriendlyTypeName", "PDF Document");

        using var appOpen = appKey.CreateSubKey(@"shell\open");
        appOpen.SetValue("", "Open with EasyPDF");

        using var appCmd = appOpen.CreateSubKey("command");
        appCmd.SetValue("", $"\"{exe}\" \"%1\"");

        using var types = appKey.CreateSubKey("SupportedTypes");
        types.SetValue(".pdf", "");
    }

    private static void PersistRegisteredPath(string exe)
    {
        using var meta = Registry.CurrentUser.CreateSubKey(LastExeKey);
        meta.SetValue("RegisteredExe", exe);
    }

    private static string GetExePath() =>
        Environment.ProcessPath
        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, "EasyPDF.exe");
}
