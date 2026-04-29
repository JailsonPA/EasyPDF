namespace EasyPDF.Core.Models;

/// <summary>Text and bounding quads returned by a drag-to-select operation.</summary>
public sealed record TextSelection(string Text, IReadOnlyList<PdfRect> Quads);
