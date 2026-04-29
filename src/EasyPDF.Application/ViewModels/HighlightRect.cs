namespace EasyPDF.Application.ViewModels;

/// <summary>
/// A single search-match rectangle already converted to WPF logical pixel coordinates,
/// ready to be placed on the Canvas overlay inside PdfPageControl.
/// </summary>
public sealed record HighlightRect(double X, double Y, double Width, double Height, bool IsActive);
