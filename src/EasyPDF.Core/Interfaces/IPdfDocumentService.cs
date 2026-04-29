using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface IPdfDocumentService : IAsyncDisposable
{
    PdfDocument? CurrentDocument { get; }
    bool IsOpen { get; }

    Task<PdfDocument> OpenAsync(string filePath, string? password = null, CancellationToken ct = default);
    void Close();
}
