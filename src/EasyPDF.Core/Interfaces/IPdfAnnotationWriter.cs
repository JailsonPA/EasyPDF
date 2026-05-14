using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface IPdfAnnotationWriter
{
    /// <summary>
    /// Reads <paramref name="sourcePath"/> as a PDF, writes the given annotations as
    /// native PDF annotation objects (`/Highlight`, `/Underline`, `/Text`, `/Ink`) on
    /// the appropriate pages, and saves the result to <paramref name="destPath"/>.
    ///
    /// Original page content (selectable text, vectors, images) is preserved — only
    /// the page's `/Annots` array is augmented. The source file is never modified.
    /// </summary>
    Task WriteAsync(
        string sourcePath,
        string destPath,
        IReadOnlyList<Annotation> annotations,
        CancellationToken ct = default);
}
