using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using EasyPDF.Core.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace EasyPDF.Infrastructure.Pdf;

/// <summary>
/// Exporta o PDF atual com anotações baked em pixels para um novo arquivo PDF.
/// Estratégia: cada página é renderizada em alta resolução pelo MuPDF, as anotações
/// são compostas nos pixels, e as imagens resultantes são empacotadas num novo PDF
/// via PDFsharp. O documento original NÃO é modificado em nenhum momento.
/// </summary>
public sealed class MuPdfExportService : IPdfExportService
{
    private readonly MuPdfDocumentService _docService;
    private readonly MuPdfDispatcher _dispatcher;

    public MuPdfExportService(MuPdfDocumentService docService, MuPdfDispatcher dispatcher)
    {
        _docService = docService;
        _dispatcher = dispatcher;
    }

    public async Task ExportAsync(
        string outputPath,
        IReadOnlyList<Annotation> annotations,
        double exportDpi = 150.0,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (!_docService.IsOpen)
            throw new InvalidOperationException("Nenhum documento aberto para exportar.");

        double scale = exportDpi / 72.0;
        int pageCount = _docService.UseDocument(d => d.Pages.Count);

        // Stream pages: render → bake → encode → append → drop. The raw BGRA buffer
        // (~24 MB/page at 150 DPI) is released between iterations so peak memory stays
        // bounded by one page worth of pixels plus the running PdfDocument (PNG-compressed,
        // typically ~5× smaller than BGRA).
        using var pdfDoc = new PdfSharp.Pdf.PdfDocument();
        pdfDoc.Info.Creator = "EasyPDF";

        for (int i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            int pageIdx = i;
            var rendered = await _dispatcher.RunAsync(() =>
                _docService.UseDocument(doc =>
                {
                    var bounds  = doc.Pages[pageIdx].Bounds;
                    int w       = Math.Max(1, (int)Math.Ceiling((bounds.X1 - bounds.X0) * scale));
                    int h       = Math.Max(1, (int)Math.Ceiling((bounds.Y1 - bounds.Y0) * scale));
                    var pixels  = doc.Render(pageIdx, scale, MuPDFCore.PixelFormats.BGRA, true);
                    int stride  = w * 4;
                    if (pixels.Length != w * h * 4 && pixels.Length > 0)
                        h = pixels.Length / (w * 4);
                    return new RenderedPage(pixels, w, h, stride, 32, 1.0);
                }), ct);

            // Bake todas as anotações da página nos pixels
            var pageAnns = annotations.Where(a => a.PageIndex == i).ToList();
            var baked    = BakeAllAnnotations(rendered, pageAnns, scale);

            // Encode + append in background; raw BGRA buffer is freed when this scope exits.
            await Task.Run(() => AppendPageToPdf(pdfDoc, baked, exportDpi), ct);

            progress?.Report((i + 1) * 100 / pageCount);
        }

        await Task.Run(() => pdfDoc.Save(outputPath), ct);
    }

    private static void AppendPageToPdf(PdfSharp.Pdf.PdfDocument doc, RenderedPage rendered, double dpi)
    {
        // JPEG q90 instead of PNG: PdfSharp embeds JPEG bytes directly with /DCTDecode
        // (no encode→decode→re-encode round-trip), the resulting PDF is ~5× smaller, and
        // q90 is visually indistinguishable from lossless for screen viewing of annotated PDFs.
        var jpegBytes = EncodeJpeg(rendered.PixelData, rendered.Width, rendered.Height, rendered.Stride, dpi, dpi, quality: 90);

        using var ms = new MemoryStream(jpegBytes);

        double ptW = rendered.Width  / dpi * 72.0;
        double ptH = rendered.Height / dpi * 72.0;

        var page    = doc.AddPage();
        page.Width  = XUnit.FromPoint(ptW);
        page.Height = XUnit.FromPoint(ptH);

        using var gfx = XGraphics.FromPdfPage(page);
        using var img = XImage.FromStream(ms);
        gfx.DrawImage(img, 0, 0, ptW, ptH);
    }

    // ─── Composição de anotações nos pixels ─────────────────────────────────

    private static RenderedPage BakeAllAnnotations(
        RenderedPage page, IReadOnlyList<Annotation> annotations, double physicalScale)
    {
        if (annotations.Count == 0) return page;

        var pixels = (byte[])page.PixelData.Clone();

        foreach (var ann in annotations)
        {
            switch (ann.Type)
            {
                case AnnotationType.Highlight:
                    AnnotationBaker.BakeHighlight(pixels, page, ann, physicalScale);
                    break;
                case AnnotationType.Underline:
                    AnnotationBaker.BakeUnderline(pixels, page, ann, physicalScale);
                    break;
                case AnnotationType.Ink when ann.InkPoints?.Count > 1:
                    BakeInk(pixels, page, ann, physicalScale);
                    break;
                case AnnotationType.Note when ann.Quads.Count > 0:
                    BakeNoteBadge(pixels, page, ann, physicalScale);
                    break;
            }
        }

        return page with { PixelData = pixels };
    }

    private static void BakeInk(byte[] px, RenderedPage p, Annotation ann, double s)
    {
        var (r, g, b) = ParseHexColor(ann.StrokeColor ?? "#FF2563EB");
        int thick = Math.Max(1, (int)(ann.InkThickness * s / 2));
        var pts = ann.InkPoints!;
        for (int k = 0; k < pts.Count - 1; k++)
            DrawLine(px, p, (int)(pts[k].X*s), (int)(pts[k].Y*s),
                           (int)(pts[k+1].X*s),(int)(pts[k+1].Y*s), r, g, b, thick);
    }

    private static void BakeNoteBadge(byte[] px, RenderedPage p, Annotation ann, double s)
    {
        var q    = ann.Quads[0];
        int cx   = (int)((q.X + q.Width  / 2) * s);
        int cy   = (int)((q.Y + q.Height / 2) * s);
        int size = Math.Max(6, (int)(14 * s / 2));
        int x0   = Math.Max(0, cx - size / 2);
        int y0   = Math.Max(0, cy - size / 2);
        int x1   = Math.Min(p.Width,  cx + size / 2);
        int y1   = Math.Min(p.Height, cy + size / 2);
        for (int y = y0; y < y1; y++)
        { int row = y * p.Stride;
          for (int x = x0; x < x1; x++)
          { int i = row + x * 4; px[i] = 0; px[i+1] = 220; px[i+2] = 255; px[i+3] = 200; } }
    }

    // ─── Utilitários de pixel ─────────────────────────────────────────────────

    private static (byte r, byte g, byte b) ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        if (hex.Length == 6)
            return (Convert.ToByte(hex[0..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16));
        return (37, 99, 235);
    }

    private static void DrawLine(byte[] px, RenderedPage p,
        int x0, int y0, int x1, int y1, byte r, byte g, byte b, int thick)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            PaintDot(px, p, x0, y0, r, g, b, thick);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private static void PaintDot(byte[] px, RenderedPage p,
        int cx, int cy, byte r, byte g, byte b, int radius)
    {
        // Circular clip instead of square — rasterizes a round brush so thick ink strokes
        // look smooth rather than blocky at line joints.
        int rSq = radius * radius;
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            if (dx * dx + dy * dy > rSq) continue;
            int x = cx + dx, y = cy + dy;
            if (x < 0 || y < 0 || x >= p.Width || y >= p.Height) continue;
            int i = y * p.Stride + x * 4;
            px[i] = b; px[i+1] = g; px[i+2] = r; px[i+3] = 255;
        }
    }

    // ─── Encode JPEG ──────────────────────────────────────────────────────────

    private static byte[] EncodeJpeg(byte[] pixels, int w, int h, int stride, double dpiX, double dpiY, int quality)
    {
        // BGRA (MuPDF) == Format32bppArgb no GDI+ em little-endian. The JPEG encoder discards
        // the alpha channel automatically — pages from MuPDF are fully opaque so this is safe.
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        bmp.SetResolution((float)dpiX, (float)dpiY);

        var locked = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (locked.Stride == stride)
            {
                Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length);
            }
            else
            {
                for (int row = 0; row < h; row++)
                    Marshal.Copy(pixels, row * stride,
                        locked.Scan0 + row * locked.Stride, stride);
            }
        }
        finally { bmp.UnlockBits(locked); }

        var jpegEncoder = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
        using var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);

        using var ms = new MemoryStream();
        bmp.Save(ms, jpegEncoder, encParams);
        return ms.ToArray();
    }
}
