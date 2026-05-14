namespace EasyPDF.Core.Models;

public enum AnnotationType  { Highlight, Underline, Note, Ink, ImageStamp }
public enum AnnotationColor { Yellow, Green, Pink, Blue, Red }

public sealed record Annotation(
    Guid Id,
    string DocumentPath,
    int PageIndex,
    AnnotationType Type,
    AnnotationColor Color,
    IReadOnlyList<PdfRect> Quads,
    DateTime CreatedAt,
    // Note: texto livre da anotação (Note); também usado como caption de ImageStamp
    string? NoteContent = null,
    // Ink: lista de pontos em coordenadas PDF (pontos encadeados formam o traço)
    IReadOnlyList<PdfPoint>? InkPoints = null,
    // ImageStamp: PNG codificado em Base64
    string? ImageBase64 = null,
    // Ink: espessura do traço em pontos PDF
    double InkThickness = 2.0,
    // Ink/Note: cor como ARGB hex (ex: "#FFFF0000" = vermelho opaco)
    string? StrokeColor = null
)
{
    /// <summary>SHA-256 fingerprint of the source document — used to relocate annotations
    /// when the user renames or moves the PDF file.</summary>
    public string? ContentHash { get; init; }
}
