namespace EasyPDF.Core.Models;

public abstract record PdfLinkDestination
{
    /// <summary>Navigate to another page in the same document (0-based index).</summary>
    public sealed record Internal(int PageIndex) : PdfLinkDestination;
    /// <summary>Open a URI in the system browser.</summary>
    public sealed record External(string Uri) : PdfLinkDestination;
}

/// <summary>
/// A single hyperlink on a PDF page.
/// <see cref="Area"/> is in PDF point coordinates (origin at top-left, Y increases downward).
/// </summary>
public readonly record struct PdfLink(PdfRect Area, PdfLinkDestination Destination);
