namespace EasyPDF.Application.Interfaces;

public interface IExportService
{
    /// <summary>Renders <paramref name="pageIndex"/> at <paramref name="dpi"/> and saves
    /// to <paramref name="filePath"/>. File format is inferred from the extension (.png / .jpg).</summary>
    Task ExportPageAsync(int pageIndex, string filePath, int dpi = 150, CancellationToken ct = default);
}
