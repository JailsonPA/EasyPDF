using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using MuPDFCore;
using MuPDFCore.StructuredText;

namespace EasyPDF.Infrastructure.Pdf;

public sealed class MuPdfTextExtractionService : ITextExtractionService
{
    private readonly MuPdfDocumentService _docService;
    private readonly MuPdfDispatcher _dispatcher;

    public MuPdfTextExtractionService(MuPdfDocumentService docService, MuPdfDispatcher dispatcher)
    {
        _docService = docService;
        _dispatcher = dispatcher;
    }

    public async Task<TextSelection?> ExtractSelectionAsync(
        int pageIndex, PdfPoint start, PdfPoint end, CancellationToken ct = default)
    {
        if (!_docService.IsOpen) return null;
        return await _dispatcher.RunAsync(() => ExtractCore(pageIndex, start, end), ct);
    }

    private TextSelection? ExtractCore(int pageIndex, PdfPoint start, PdfPoint end)
    {
        return _docService.UseDocument(doc =>
        {
            try
            {
                using var stp = doc.GetStructuredTextPage(pageIndex, false, StructuredTextFlags.None);

                var startAddr = stp.GetClosestHitAddress(new PointF(start.X, start.Y), false);
                var endAddr   = stp.GetClosestHitAddress(new PointF(end.X, end.Y),   false);
                if (startAddr is null || endAddr is null) return null;

                // Guarantee that the span runs in document order (block → line → char).
                var (a, b) = AddressOrder(startAddr.Value, endAddr.Value) <= 0
                    ? (startAddr.Value, endAddr.Value)
                    : (endAddr.Value,   startAddr.Value);

                var span = new MuPDFStructuredTextAddressSpan(a, b);
                string text = stp.GetText(span);
                if (string.IsNullOrWhiteSpace(text)) return null;

                var quads = stp.GetHighlightQuads(span, false)
                    .Select(q =>
                    {
                        float x = Math.Min(q.UpperLeft.X, q.LowerLeft.X);
                        float y = Math.Min(q.UpperLeft.Y, q.UpperRight.Y);
                        float w = Math.Max(q.UpperRight.X, q.LowerRight.X) - x;
                        float h = Math.Max(q.LowerLeft.Y, q.LowerRight.Y) - y;
                        return new PdfRect(x, y, w, h);
                    })
                    .ToList();

                return new TextSelection(text, quads);
            }
            catch { return null; }
        });
    }

    private static int AddressOrder(MuPDFStructuredTextAddress a, MuPDFStructuredTextAddress b)
    {
        if (a.BlockIndex    != b.BlockIndex)    return a.BlockIndex.CompareTo(b.BlockIndex);
        if (a.LineIndex     != b.LineIndex)     return a.LineIndex.CompareTo(b.LineIndex);
        return a.CharacterIndex.CompareTo(b.CharacterIndex);
    }
}
