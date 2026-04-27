namespace EasyPDF.Core.Models;

/// <summary>
/// A rectangle in PDF point coordinates (origin at bottom-left of page).
/// </summary>
public readonly record struct PdfRect(double X, double Y, double Width, double Height);
