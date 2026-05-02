using System.Runtime.InteropServices;

namespace EasyPDF.UI;

internal static class NativeMethods
{
    internal const int SW_RESTORE      = 9;
    internal const int WM_NCHITTEST   = 0x0084;
    internal const int WM_NCLBUTTONDOWN = 0x00A1;
    internal const int WM_NCLBUTTONUP   = 0x00A2;
    internal const int HTMAXBUTTON    = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // Notifies the Windows shell that file associations changed so Explorer
    // refreshes icons and "Open with" lists without needing a logoff/reboot.
    [DllImport("shell32.dll")]
    internal static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
