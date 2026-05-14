using EasyPDF.Core.Models;

namespace EasyPDF.Application.ViewModels;

public sealed record AnnotationHighlightRect(
    Guid Id,
    double X, double Y, double Width, double Height,
    AnnotationColor Color,
    bool IsUnderline)
{
    public string ColorName => Color.ToString();

    public double OverlayY => IsUnderline ? Y + Height * 0.82 : Y;
}
