using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using MuPDFCore;

namespace EasyPDF.Infrastructure.Pdf;

/// <summary>
/// Renders PDF pages asynchronously using MuPDFCore.
///
/// All native MuPDF calls (bounds lookup, doc.Render) are routed through
/// MuPdfDispatcher so they always execute on a 32 MB stack thread. Using
/// Task.Run instead would assign them to the default thread-pool (1 MB stack),
/// causing STATUS_STACK_BUFFER_OVERRUN (0xC0000409) crashes for complex PDFs.
///
/// Concurrency:
///   All renders (page + thumbnail) are serialised through a single SemaphoreSlim(1,1).
///   MuPDF's fz_context is not thread-safe — concurrent doc.Render() calls on the same
///   context cause SEHException. The dispatcher pool (6 large-stack threads) handles
///   queuing; only one render executes natively at a time.
///
/// Thread-safety: every native call enters UseDocument(), which holds the document
/// read lock for the duration, preventing Close() from disposing MuPDFDocument
/// while a render is executing.
/// </summary>
public sealed class MuPdfRenderService : IPdfRenderService
{
    private readonly MuPdfDocumentService _docService;
    private readonly IPageCache _cache;
    private readonly ILogger<MuPdfRenderService> _logger;
    private readonly MuPdfDispatcher _dispatcher;

    // MuPDF's fz_context is not thread-safe: concurrent calls to doc.Render() on the same
    // context cause SEHException / "Cannot render page". A single mutex serialises all native
    // render calls while still routing them through the 32 MB dispatcher stack.
    private readonly SemaphoreSlim _renderMutex = new(1, 1);

    public MuPdfRenderService(
        MuPdfDocumentService docService,
        IPageCache cache,
        ILogger<MuPdfRenderService> logger,
        MuPdfDispatcher dispatcher)
    {
        _docService = docService;
        _cache = cache;
        _logger = logger;
        _dispatcher = dispatcher;
    }

    public async Task<RenderedPage> RenderPageAsync(
        int pageIndex,
        double scale,
        double dpiScale = 1.0,
        CancellationToken ct = default)
    {
        EnsureOpen();
        string cacheKey = $"page:{pageIndex}:{scale:F3}:{dpiScale:F3}";
        if (_cache.Get(cacheKey) is { } cached) return cached;

        await _renderMutex.WaitAsync(ct);
        try
        {
            if (_cache.Get(cacheKey) is { } hit) return hit;

            // Route through dispatcher — guarantees 32 MB stack for doc.Render().
            // physicalScale renders at the true physical-pixel density so that
            // BitmapSource (created at 96*dpiScale DPI) displays without upscaling.
            double physicalScale = scale * dpiScale;
            return await _dispatcher.RunAsync(() => RenderCore(pageIndex, physicalScale, dpiScale, cacheKey), ct);
        }
        finally { _renderMutex.Release(); }
    }

    public async Task<RenderedPage> RenderThumbnailAsync(
        int pageIndex,
        int maxWidth,
        double dpiScale = 1.0,
        CancellationToken ct = default)
    {
        EnsureOpen();
        string cacheKey = $"thumb:{pageIndex}:{maxWidth}:{dpiScale:F3}";
        if (_cache.Get(cacheKey) is { } cached) return cached;

        await _renderMutex.WaitAsync(ct);
        try
        {
            if (_cache.Get(cacheKey) is { } hit) return hit;

            // Combine bounds lookup + render in one dispatcher call so both
            // native calls happen on the same 32 MB stack thread.
            return await _dispatcher.RunAsync(() =>
            {
                // maxWidth is in logical pixels; multiply by dpiScale for physical pixels.
                double thumbScale = _docService.UseDocument(doc =>
                    maxWidth * dpiScale / (doc.Pages[pageIndex].Bounds.X1 - doc.Pages[pageIndex].Bounds.X0));

                return RenderCore(pageIndex, thumbScale, dpiScale, cacheKey);
            }, ct);
        }
        finally { _renderMutex.Release(); }
    }

    private RenderedPage RenderCore(int pageIndex, double physicalScale, double dpiScale, string cacheKey)
    {
        // UseDocument holds the read lock for the entire render, preventing Close()
        // from disposing MuPDFDocument while native rendering is executing.
        return _docService.UseDocument(doc =>
        {
            var bounds = doc.Pages[pageIndex].Bounds;

            // MuPDF uses ceiling internally — must match or stride will be off by 1 row.
            int width  = Math.Max(1, (int)Math.Ceiling((bounds.X1 - bounds.X0) * physicalScale));
            int height = Math.Max(1, (int)Math.Ceiling((bounds.Y1 - bounds.Y0) * physicalScale));

            byte[] pixels;
            try
            {
                pixels = doc.Render(pageIndex, physicalScale, PixelFormats.BGRA, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MuPDFCore render failed — page {Page}, scale {Scale}",
                    pageIndex, physicalScale);
                throw;
            }

            // Guard: if our dimension math diverges from MuPDF's, recalculate from actual byte count.
            int expectedBytes = width * height * 4;
            if (pixels.Length != expectedBytes && pixels.Length > 0)
            {
                height = pixels.Length / (width * 4);
                if (height < 1) height = 1;
            }

            int stride = width * 4;
            var rendered = new RenderedPage(pixels, width, height, stride, 32, dpiScale);
            _cache.Set(cacheKey, rendered);
            return rendered;
        });
    }

    private void EnsureOpen()
    {
        if (!_docService.IsOpen)
            throw new InvalidOperationException("No PDF document is currently open.");
    }

    public ValueTask DisposeAsync()
    {
        _renderMutex.Dispose();
        return ValueTask.CompletedTask;
    }
}
