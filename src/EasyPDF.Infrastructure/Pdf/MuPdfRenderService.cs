using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;
using MuPDFCore;

namespace EasyPDF.Infrastructure.Pdf;

public sealed class MuPdfRenderService : IPdfRenderService
{
    private readonly MuPdfDocumentService _docService;
    private readonly IPageCache _cache;
    private readonly ILogger<MuPdfRenderService> _logger;
    private readonly MuPdfDispatcher _dispatcher;

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

            
            return await _dispatcher.RunAsync(() =>
            {
                double thumbScale = _docService.UseDocument(doc =>
                    maxWidth * dpiScale / (doc.Pages[pageIndex].Bounds.X1 - doc.Pages[pageIndex].Bounds.X0));

                return RenderCore(pageIndex, thumbScale, dpiScale, cacheKey);
            }, ct);
        }
        finally { _renderMutex.Release(); }
    }

    // MuPDF's grayscale antialiasing reads as crisp once the per-PDF-point pixel count clears
    // ≈3.0 (~216 DPI). Below that, glyph edges look soft. So we always render MuPDF at AT LEAST
    // SharpRenderScale, then box-average the source down to the caller's requested physicalScale
    // (which is exact display-pixel resolution from PdfPageControl.TriggerRender). The displayed
    // bitmap stays 1:1 with the screen — no WPF resample, no Fant softening.
    private const double SharpRenderScale = 3.0;
    private const int    MaxSupersample   = 6;   // bounds memory at extreme low zoom

    private RenderedPage RenderCore(int pageIndex, double physicalScale, double dpiScale, string cacheKey)
    {
        return _docService.UseDocument(doc =>
        {
            var bounds = doc.Pages[pageIndex].Bounds;

            // Pick the smallest integer supersample that lifts MuPDF's render scale at or
            // above SharpRenderScale. supersample = 1 means "MuPDF already renders at high
            // enough DPI for this zoom — skip the downsample and return raw pixels."
            int supersample = physicalScale >= SharpRenderScale
                ? 1
                : Math.Min(MaxSupersample, (int)Math.Ceiling(SharpRenderScale / physicalScale));
            double sourceScale = physicalScale * supersample;

            byte[] sourcePixels;
            try
            {
                sourcePixels = doc.Render(pageIndex, sourceScale, PixelFormats.BGRA, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MuPDFCore render failed — page {Page}, scale {Scale}",
                    pageIndex, sourceScale);
                throw;
            }

            int srcWidth  = Math.Max(1, (int)Math.Ceiling((bounds.X1 - bounds.X0) * sourceScale));
            int srcHeight = sourcePixels.Length / Math.Max(1, srcWidth * 4);
            if (srcHeight < 1) srcHeight = 1;

            // Fast path: already at display resolution, no downsample needed.
            if (supersample == 1)
            {
                var direct = new RenderedPage(sourcePixels, srcWidth, srcHeight, srcWidth * 4, 32, dpiScale);
                _cache.Set(cacheKey, direct);
                return direct;
            }

            int dstWidth  = Math.Max(1, srcWidth  / supersample);
            int dstHeight = Math.Max(1, srcHeight / supersample);
            byte[] dstPixels = BoxDownsample(sourcePixels, srcWidth, srcHeight, supersample, dstWidth, dstHeight);

            var rendered = new RenderedPage(dstPixels, dstWidth, dstHeight, dstWidth * 4, 32, dpiScale);
            _cache.Set(cacheKey, rendered);
            return rendered;
        });
    }

    /// Integer-ratio box-average downsample of a BGRA buffer. For an N×N supersample factor
    /// each output pixel is the simple arithmetic mean of N×N source pixels. Compared to
    /// WPF's Fant/Lanczos filter this has a strictly narrower kernel: no negative lobes, no
    /// pixels outside the destination block contribute. For text rasterized by MuPDF that
    /// translates to noticeably crisper glyph edges at every common zoom.
    private static byte[] BoxDownsample(byte[] src, int srcWidth, int srcHeight, int factor,
                                       int dstWidth, int dstHeight)
    {
        int srcStride = srcWidth * 4;
        int dstStride = dstWidth * 4;
        var dst = new byte[dstHeight * dstStride];
        int blockSize = factor * factor;
        int half = blockSize >> 1;   // round-to-nearest on the integer divide

        for (int y = 0; y < dstHeight; y++)
        {
            int srcRowBase = y * factor * srcStride;
            int dstRow     = y * dstStride;

            for (int x = 0; x < dstWidth; x++)
            {
                int srcColBase = x * factor * 4;
                int dstX       = x * 4;

                int b = 0, g = 0, r = 0, a = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int srcRow = srcRowBase + dy * srcStride;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        int idx = srcRow + srcColBase + dx * 4;
                        b += src[idx];
                        g += src[idx + 1];
                        r += src[idx + 2];
                        a += src[idx + 3];
                    }
                }
                dst[dstRow + dstX]     = (byte)((b + half) / blockSize);
                dst[dstRow + dstX + 1] = (byte)((g + half) / blockSize);
                dst[dstRow + dstX + 2] = (byte)((r + half) / blockSize);
                dst[dstRow + dstX + 3] = (byte)((a + half) / blockSize);
            }
        }
        return dst;
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
