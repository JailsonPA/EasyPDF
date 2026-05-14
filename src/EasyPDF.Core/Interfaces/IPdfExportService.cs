using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface IPdfExportService
{
    /// <summary>
    /// Renderiza todas as páginas com anotações baked e salva um novo PDF no caminho informado.
    /// O documento original não é modificado.
    /// </summary>
    Task ExportAsync(
        string outputPath,
        IReadOnlyList<Annotation> annotations,
        double exportDpi = 150.0,
        IProgress<int>? progress = null,
        CancellationToken ct = default);
}
