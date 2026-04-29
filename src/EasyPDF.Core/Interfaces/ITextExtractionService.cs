using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface ITextExtractionService
{
    /// <summary>
    /// Extracts the text and bounding quads for the region dragged from
    /// <paramref name="start"/> to <paramref name="end"/> on the given page.
    /// Returns null when no text is found or the document is closed.
    /// </summary>
    Task<TextSelection?> ExtractSelectionAsync(
        int pageIndex,
        PdfPoint start,
        PdfPoint end,
        CancellationToken ct = default);
}
