using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public enum TextSelectionUnit { Word, Line }

public interface ITextExtractionService
{
    Task<TextSelection?> ExtractSelectionAsync(
        int pageIndex,
        PdfPoint start,
        PdfPoint end,
        CancellationToken ct = default);

    Task<TextSelection?> ExtractAtPointAsync(
        int pageIndex,
        PdfPoint point,
        TextSelectionUnit unit,
        CancellationToken ct = default);
}
