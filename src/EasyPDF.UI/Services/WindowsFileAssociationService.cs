using Microsoft.Win32;
using System.IO;

namespace EasyPDF.UI.Services;

/// <summary>
/// Registers EasyPDF in HKCU so it appears in Windows "Open with" for .pdf files.
/// Uses HKCU — no administrator rights required.
/// </summary>
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

            // Skip if already registered for the same exe path.
            using (var meta = Registry.CurrentUser.OpenSubKey(LastExeKey))
            {
                if (meta?.GetValue("RegisteredExe") is string last && last == exe)
                    return;
            }

            RegisterProgId(exe);
            RegisterApplication(exe);
            PersistRegisteredPath(exe);

            // Notify Explorer so "Open with" list refreshes immediately.
            const int SHCNE_ASSOCCHANGED = 0x08000000;
            NativeMethods.SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Non-critical — app works fine without shell integration.
        }
    }

    // ProgID used when the user sets EasyPDF as the default PDF viewer.
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

        // Link .pdf extension to this ProgID for the "Open with" list.
        using var pdfWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids");
        pdfWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    // Application registration: shows up in "Open with → Choose another app".
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
