namespace EasyPDF.Core.Models;

public enum AnnotationType { Highlight, Note, Underline }

public sealed record Annotation(
    Guid Id,
    string DocumentPath,
    int PageIndex,
    AnnotationType Type,
    PdfRect Bounds,
    string? Text,
    string Color,       // hex e.g. "#FFFF00"
    DateTime CreatedAt
);
