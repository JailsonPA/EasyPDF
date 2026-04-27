using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface IPdfRenderService : IAsyncDisposable
{
    /// <summary>Renders a full page at the given scale (1.0 = 72 DPI).
    /// <paramref name="dpiScale"/> multiplies the physical pixel count so the
    /// bitmap is sharp on high-DPI screens without changing logical display size.</summary>
    Task<RenderedPage> RenderPageAsync(
        int pageIndex,
        double scale,
        double dpiScale = 1.0,
        CancellationToken ct = default);

    /// <summary>Renders a thumbnail fitting within <paramref name="maxWidth"/> logical pixels.
    /// <paramref name="dpiScale"/> multiplies the physical pixel count for high-DPI sharpness.</summary>
    Task<RenderedPage> RenderThumbnailAsync(
        int pageIndex,
        int maxWidth,
        double dpiScale = 1.0,
        CancellationToken ct = default);
}
