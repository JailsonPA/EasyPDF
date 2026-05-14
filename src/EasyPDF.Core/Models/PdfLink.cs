namespace EasyPDF.Core.Models;

public abstract record PdfLinkDestination
{
    public sealed record Internal(int PageIndex) : PdfLinkDestination;
    public sealed record External(string Uri) : PdfLinkDestination;
}

/// <summary>
/// A single hyperlink on a PDF page.
/// <see cref="Area"/> is in PDF point coordinates (origin at top-left, Y increases downward).
/// </summary>
public readonly record struct PdfLink(PdfRect Area, PdfLinkDestination Destination);
