using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

// Both EasyPDF.Core.Models and PdfSharp.Pdf export a `PdfDocument` type. Within this
// file we only ever manipulate the PdfSharp one (our domain PdfDocument is unrelated
// to the file-writing layer), so alias it for unambiguous references.
using PdfDocument = PdfSharp.Pdf.PdfDocument;

namespace EasyPDF.Infrastructure.Pdf;

/// <summary>
/// Writes EasyPDF annotations into a PDF as native PDF annotation objects
/// (ISO 32000 / PDF 1.7). Unlike <see cref="MuPdfExportService"/> which rasterizes
/// every page as JPEG, this writer keeps the original page content intact — text
/// remains selectable, vectors stay vectors — and only appends to each page's
/// `/Annots` array.
///
/// Coordinate conversion: our internal quads are in "origin-zero, Y-down" space
/// (relative to the CropBox, top-left origin). PDF native space is "Y-up" with
/// origin at the MediaBox lower-left. We translate by the CropBox offset and flip Y
/// using the CropBox top edge.
/// </summary>
public sealed class PdfSharpAnnotationWriter : IPdfAnnotationWriter
{
    public Task WriteAsync(
        string sourcePath,
        string destPath,
        IReadOnlyList<Annotation> annotations,
        CancellationToken ct = default)
    {
        // PdfSharp is fully synchronous; offload to thread pool so the UI thread stays free.
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            using var doc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);

            foreach (var ann in annotations)
            {
                ct.ThrowIfCancellationRequested();
                if (ann.PageIndex < 0 || ann.PageIndex >= doc.PageCount) continue;

                var page = doc.Pages[ann.PageIndex];
                var crop = page.CropBox;
                double cropX0 = crop.X1;   // lower-left X in PDF user space
                double cropY2 = crop.Y2;   // upper-right Y == TOP of crop in PDF Y-up coords

                switch (ann.Type)
                {
                    case AnnotationType.Highlight:
                        AddQuadAnnotation(doc, page, ann, cropX0, cropY2, "/Highlight", opacity: 0.6);
                        break;
                    case AnnotationType.Underline:
                        AddQuadAnnotation(doc, page, ann, cropX0, cropY2, "/Underline", opacity: 1.0);
                        break;
                    case AnnotationType.Note:
                        AddTextAnnotation(doc, page, ann, cropX0, cropY2);
                        break;
                    case AnnotationType.Ink:
                        AddInkAnnotation(doc, page, ann, cropX0, cropY2);
                        break;
                }
            }

            doc.Save(destPath);
        }, ct);
    }

    private static void AddQuadAnnotation(
        PdfDocument doc, PdfPage page, Annotation ann,
        double cropX0, double cropY2, string subtype, double opacity)
    {
        if (ann.Quads.Count == 0) return;

        var (r, g, b) = ColorToFloats(ann.Color);

        var quadPoints = new PdfArray(doc);
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var q in ann.Quads)
        {
            if (q.Width <= 0 || q.Height <= 0) continue;

            double xL = q.X + cropX0;
            double xR = q.X + q.Width + cropX0;
            double yT = cropY2 - q.Y;
            double yB = cropY2 - (q.Y + q.Height);

            // PDF spec order: TL, TR, BL, BR (Adobe convention).
            AddReal(quadPoints, xL); AddReal(quadPoints, yT);
            AddReal(quadPoints, xR); AddReal(quadPoints, yT);
            AddReal(quadPoints, xL); AddReal(quadPoints, yB);
            AddReal(quadPoints, xR); AddReal(quadPoints, yB);

            if (xL < minX) minX = xL;
            if (xR > maxX) maxX = xR;
            if (yB < minY) minY = yB;
            if (yT > maxY) maxY = yT;
        }

        if (quadPoints.Elements.Count == 0) return;  // all quads were degenerate

        var annot = new PdfDictionary(doc);
        annot.Elements.SetName("/Type", "/Annot");
        annot.Elements.SetName("/Subtype", subtype);
        annot.Elements["/QuadPoints"] = quadPoints;
        annot.Elements["/Rect"] = MakeRect(doc, minX, minY, maxX, maxY);
        annot.Elements["/C"] = MakeColor(doc, r, g, b);
        if (opacity < 1.0) annot.Elements.SetReal("/CA", opacity);
        annot.Elements.SetString("/T", "EasyPDF");
        annot.Elements.SetString("/Contents", "");
        annot.Elements.SetDateTime("/M", ann.CreatedAt.ToLocalTime());

        AddToPageAnnotsArray(doc, page, annot);
    }

    private static void AddTextAnnotation(
        PdfDocument doc, PdfPage page, Annotation ann, double cropX0, double cropY2)
    {
        if (ann.Quads.Count == 0) return;
        var q = ann.Quads[0];

        double x = q.X + cropX0;
        double yT = cropY2 - q.Y;
        double yB = cropY2 - (q.Y + q.Height);

        var annot = new PdfDictionary(doc);
        annot.Elements.SetName("/Type", "/Annot");
        annot.Elements.SetName("/Subtype", "/Text");
        annot.Elements.SetName("/Name", "/Note");
        annot.Elements["/Rect"] = MakeRect(doc, x, yB, x + q.Width, yT);
        annot.Elements.SetString("/Contents", ann.NoteContent ?? string.Empty);
        annot.Elements.SetString("/T", "EasyPDF");
        annot.Elements.SetDateTime("/M", ann.CreatedAt.ToLocalTime());

        AddToPageAnnotsArray(doc, page, annot);
    }

    private static void AddInkAnnotation(
        PdfDocument doc, PdfPage page, Annotation ann, double cropX0, double cropY2)
    {
        if (ann.InkPoints is null || ann.InkPoints.Count < 2) return;

        var (r, g, b) = HexToFloats(ann.StrokeColor ?? "#FF2563EB");

        // /InkList is an array of arrays — each inner array is one continuous stroke.
        // We store all points as a single stroke (EasyPDF treats each Ink annotation as
        // one continuous gesture).
        var stroke = new PdfArray(doc);
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var p in ann.InkPoints)
        {
            double px = p.X + cropX0;
            double py = cropY2 - p.Y;
            AddReal(stroke, px);
            AddReal(stroke, py);
            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;
        }

        var inkList = new PdfArray(doc);
        inkList.Elements.Add(stroke);

        var annot = new PdfDictionary(doc);
        annot.Elements.SetName("/Type", "/Annot");
        annot.Elements.SetName("/Subtype", "/Ink");
        annot.Elements["/InkList"] = inkList;
        // Pad the bounding rect by stroke thickness so the rendered stroke isn't clipped.
        double pad = ann.InkThickness;
        annot.Elements["/Rect"] = MakeRect(doc, minX - pad, minY - pad, maxX + pad, maxY + pad);
        annot.Elements["/C"] = MakeColor(doc, r, g, b);

        var bs = new PdfDictionary(doc);
        bs.Elements.SetName("/Type", "/Border");
        bs.Elements.SetReal("/W", ann.InkThickness);
        annot.Elements["/BS"] = bs;

        annot.Elements.SetString("/T", "EasyPDF");
        annot.Elements.SetDateTime("/M", ann.CreatedAt.ToLocalTime());

        AddToPageAnnotsArray(doc, page, annot);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static void AddToPageAnnotsArray(PdfDocument doc, PdfPage page, PdfDictionary annot)
    {
        // The annotation must be an indirect object so it can be referenced from /Annots.
        doc.Internals.AddObject(annot);

        var annots = page.Elements.GetArray("/Annots");
        if (annots is null)
        {
            annots = new PdfArray(doc);
            page.Elements["/Annots"] = annots;
        }
        annots.Elements.Add(annot.Reference!);
    }

    private static PdfArray MakeRect(PdfDocument doc, double x1, double y1, double x2, double y2)
    {
        var rect = new PdfArray(doc);
        AddReal(rect, x1); AddReal(rect, y1); AddReal(rect, x2); AddReal(rect, y2);
        return rect;
    }

    private static PdfArray MakeColor(PdfDocument doc, double r, double g, double b)
    {
        var arr = new PdfArray(doc);
        AddReal(arr, r); AddReal(arr, g); AddReal(arr, b);
        return arr;
    }

    private static void AddReal(PdfArray arr, double v) =>
        arr.Elements.Add(new PdfReal(v));

    private static (double R, double G, double B) ColorToFloats(AnnotationColor c) => c switch
    {
        AnnotationColor.Green => (0.43, 0.86, 0.59),
        AnnotationColor.Pink  => (0.94, 0.69, 0.82),
        AnnotationColor.Blue  => (0.40, 0.66, 0.98),
        AnnotationColor.Red   => (0.92, 0.42, 0.42),
        _                     => (0.98, 0.88, 0.40),  // yellow
    };

    private static (double R, double G, double B) HexToFloats(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];  // drop alpha prefix (ARGB → RGB)
        if (hex.Length != 6) return (0.0, 0.0, 0.0);
        try
        {
            return (
                Convert.ToByte(hex[0..2], 16) / 255.0,
                Convert.ToByte(hex[2..4], 16) / 255.0,
                Convert.ToByte(hex[4..6], 16) / 255.0);
        }
        catch
        {
            return (0.0, 0.0, 0.0);
        }
    }
}
