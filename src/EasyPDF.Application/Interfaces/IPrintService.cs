using EasyPDF.Core.Models;

namespace EasyPDF.Application.Interfaces;

public interface IPrintService
{
    Task PrintAsync(string documentTitle, IReadOnlyList<PdfPageInfo> pages, CancellationToken ct = default);
}
