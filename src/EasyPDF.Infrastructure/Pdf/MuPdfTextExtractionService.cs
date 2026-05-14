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

    public async Task<TextSelection?> ExtractAtPointAsync(
        int pageIndex, PdfPoint point, TextSelectionUnit unit, CancellationToken ct = default)
    {
        if (!_docService.IsOpen) return null;
        return await _dispatcher.RunAsync(() => ExtractAtPointCore(pageIndex, point, unit), ct);
    }

    private TextSelection? ExtractCore(int pageIndex, PdfPoint start, PdfPoint end)
    {
        return _docService.UseDocument(doc =>
        {
            try
            {
                var bounds = doc.Pages[pageIndex].Bounds;
                float ox = bounds.X0;
                float oy = bounds.Y0;

                using var stp = doc.GetStructuredTextPage(pageIndex, false, StructuredTextFlags.None);

                var startAddr = stp.GetClosestHitAddress(new PointF((float)start.X + ox, (float)start.Y + oy), false);
                var endAddr = stp.GetClosestHitAddress(new PointF((float)end.X + ox, (float)end.Y + oy), true)
                           ?? stp.GetClosestHitAddress(new PointF((float)end.X + ox, (float)end.Y + oy), false);
                if (startAddr is null || endAddr is null) return null;

                var (a, b) = AddressOrder(startAddr.Value, endAddr.Value) <= 0
                    ? (startAddr.Value, endAddr.Value)
                    : (endAddr.Value, startAddr.Value);

                var span = new MuPDFStructuredTextAddressSpan(a, b);
                string text = stp.GetText(span);
                if (string.IsNullOrWhiteSpace(text)) return null;

                return new TextSelection(text, QuadsFromSpan(stp, span, ox, oy));
            }
            catch { return null; }
        });
    }

    private TextSelection? ExtractAtPointCore(int pageIndex, PdfPoint point, TextSelectionUnit unit)
    {
        return _docService.UseDocument(doc =>
        {
            try
            {
                var bounds = doc.Pages[pageIndex].Bounds;
                float ox = bounds.X0;
                float oy = bounds.Y0;

                using var stp = doc.GetStructuredTextPage(pageIndex, false, StructuredTextFlags.None);

                var hitAddr = stp.GetClosestHitAddress(new PointF((float)point.X + ox, (float)point.Y + oy), false);
                if (hitAddr is null) return null;

                var hit = hitAddr.Value;
                MuPDFStructuredTextAddress spanStart, spanEnd;

                if (unit == TextSelectionUnit.Line)
                {
                    var line = stp[hit.BlockIndex][hit.LineIndex];
                    spanStart = new MuPDFStructuredTextAddress(hit.BlockIndex, hit.LineIndex, 0);
                    spanEnd = new MuPDFStructuredTextAddress(hit.BlockIndex, hit.LineIndex, line.Count - 1);
                }
                else
                {
                    var line = stp[hit.BlockIndex][hit.LineIndex];
                    int ci = Math.Clamp(hit.CharacterIndex, 0, line.Count - 1);

                    int wordStart = ci;
                    while (wordStart > 0 && !IsWordBoundary(line[wordStart - 1].Character))
                        wordStart--;

                    int wordEnd = ci;
                    while (wordEnd < line.Count - 1 && !IsWordBoundary(line[wordEnd + 1].Character))
                        wordEnd++;

                    spanStart = new MuPDFStructuredTextAddress(hit.BlockIndex, hit.LineIndex, wordStart);
                    spanEnd = new MuPDFStructuredTextAddress(hit.BlockIndex, hit.LineIndex, wordEnd);
                }

                var span = new MuPDFStructuredTextAddressSpan(spanStart, spanEnd);
                string text = stp.GetText(span);
                if (string.IsNullOrWhiteSpace(text)) return null;

                return new TextSelection(text, QuadsFromSpan(stp, span, ox, oy));
            }
            catch { return null; }
        });
    }

    private static List<PdfRect> QuadsFromSpan(MuPDFStructuredTextPage stp, MuPDFStructuredTextAddressSpan span, double originX, double originY)
    {
        var raw = stp.GetHighlightQuads(span, false)
            .Select(q =>
            {
                double x = Math.Min(q.UpperLeft.X, q.LowerLeft.X);
                double y = Math.Min(q.UpperLeft.Y, q.UpperRight.Y);
                double w = Math.Max(q.UpperRight.X, q.LowerRight.X) - x;
                double h = Math.Max(q.LowerLeft.Y, q.LowerRight.Y) - y;
                return new PdfRect(x - originX, y - originY, w, h);
            })
            .OrderBy(r => r.Y)
            .ThenBy(r => r.X)
            .ToList();

        return MergeLineRects(raw);
    }

    private static List<PdfRect> MergeLineRects(List<PdfRect> rects)
    {
        if (rects.Count <= 1) return rects;

        var merged = new List<PdfRect>();
        var cur = rects[0];

        for (int i = 1; i < rects.Count; i++)
        {
            var r = rects[i];
            double curMidY = cur.Y + cur.Height * 0.5;
            double rMidY = r.Y + r.Height * 0.5;
            bool sameLine = Math.Abs(curMidY - rMidY) < Math.Max(cur.Height, r.Height) * 0.5;

            if (sameLine)
            {
                double x0 = Math.Min(cur.X, r.X);
                double y0 = Math.Min(cur.Y, r.Y);
                double x1 = Math.Max(cur.X + cur.Width, r.X + r.Width);
                double y1 = Math.Max(cur.Y + cur.Height, r.Y + r.Height);
                cur = new PdfRect(x0, y0, x1 - x0, y1 - y0);
            }
            else
            {
                merged.Add(cur);
                cur = r;
            }
        }

        merged.Add(cur);
        return merged;
    }

    private static bool IsWordBoundary(string ch) =>
        ch.Length == 0 || ch[0] is ' ' or '\t' or '\n' or '\r'
            or ',' or '.' or '!' or '?' or ';' or ':'
            or '(' or ')' or '[' or ']' or '{' or '}'
            or '"' or '\'' or '/' or '\\' or '-' or '–' or '—';

    private static int AddressOrder(MuPDFStructuredTextAddress a, MuPDFStructuredTextAddress b)
    {
        if (a.BlockIndex != b.BlockIndex) return a.BlockIndex.CompareTo(b.BlockIndex);
        if (a.LineIndex != b.LineIndex) return a.LineIndex.CompareTo(b.LineIndex);
        return a.CharacterIndex.CompareTo(b.CharacterIndex);
    }
}