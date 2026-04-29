namespace EasyPDF.Core.Models;

public sealed record PdfPageInfo(
    int Index,
    double WidthPt,   // width in PDF points
    double HeightPt   // height in PDF points
)
{
    public double AspectRatio => HeightPt / WidthPt;
    public IReadOnlyList<PdfLink> Links { get; init; } = [];
}
