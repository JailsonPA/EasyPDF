using EasyPDF.Core.Models;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace EasyPDF.UI.Views;

public partial class PropertiesWindow : Window
{
    public PropertiesWindow(PdfDocument doc)
    {
        InitializeComponent();
        Populate(doc);
    }

    private void Populate(PdfDocument doc)
    {
        var fi = new FileInfo(doc.FilePath);

        NameValue.Text        = doc.FileName;
        LocationValue.Text    = Path.GetDirectoryName(doc.FilePath) ?? doc.FilePath;
        SizeValue.Text        = FormatSize(doc.FileSizeBytes);
        PdfVersionValue.Text  = doc.PdfVersion;

        PagesValue.Text       = doc.PageCount.ToString();
        PageSizeValue.Text    = FormatPageSize(doc.Pages);

        EncryptionValue.Text  = doc.IsEncrypted  ? "Password protected" : "None";
        RestrictionsValue.Text = doc.IsRestricted ? "Restricted"         : "None";

        LastModifiedValue.Text = fi.Exists ? fi.LastWriteTime.ToString("yyyy-MM-dd  HH:mm") : "—";
        FileCreatedValue.Text  = fi.Exists ? fi.CreationTime .ToString("yyyy-MM-dd  HH:mm") : "—";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} bytes";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB  ({bytes:N0} bytes)";
        return $"{bytes / (1024.0 * 1024.0):F1} MB  ({bytes:N0} bytes)";
    }

    private static string FormatPageSize(IReadOnlyList<PdfPageInfo> pages)
    {
        if (pages.Count == 0) return "—";
        double w = pages[0].WidthPt;
        double h = pages[0].HeightPt;
        string named = DetectPaperSize((int)w, (int)h);
        string suffix = string.IsNullOrEmpty(named) ? "" : $"  ({named})";
        return $"{w:F0} × {h:F0} pt{suffix}";
    }

    private static string DetectPaperSize(int w, int h)
    {
        static bool Near(int a, int b) => Math.Abs(a - b) <= 5;

        if (Near(w, 595)  && Near(h, 842))  return "A4";
        if (Near(w, 842)  && Near(h, 595))  return "A4 landscape";
        if (Near(w, 612)  && Near(h, 792))  return "US Letter";
        if (Near(w, 792)  && Near(h, 612))  return "US Letter landscape";
        if (Near(w, 612)  && Near(h, 1008)) return "US Legal";
        if (Near(w, 842)  && Near(h, 1190)) return "A3";
        if (Near(w, 1190) && Near(h, 842))  return "A3 landscape";
        if (Near(w, 420)  && Near(h, 595))  return "A5";
        if (Near(w, 595)  && Near(h, 420))  return "A5 landscape";
        return "";
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
