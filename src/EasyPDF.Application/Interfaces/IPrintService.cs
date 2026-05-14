using EasyPDF.Core.Models;

namespace EasyPDF.Application.Interfaces;

public interface IPrintService
{
    /// <summary>
    /// Opens a print preview window, then the native printer dialog, then prints.
    /// </summary>
    /// <param name="annotations">Annotations baked onto each page (when user opts in).</param>
    /// <param name="currentPageIndex">Zero-based page the user is viewing — used for the
    /// "Current page only" range option in the preview.</param>
    Task PrintAsync(
        string documentTitle,
        IReadOnlyList<PdfPageInfo> pages,
        IReadOnlyList<Annotation> annotations,
        int currentPageIndex,
        CancellationToken ct = default);
}
