namespace EasyPDF.Core.Models;

public sealed record TextSelection(string Text, IReadOnlyList<PdfRect> Quads);
