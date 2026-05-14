using EasyPDF.Core.Models;
using EasyPDF.Core.Rendering;
using Xunit;

namespace EasyPDF.Tests.Rendering;

/// <summary>
/// Tests for the pure-pixel highlight/underline compositor. Synthetic 4×4 / 8×8 BGRA
/// buffers are constructed in code so each test owns its expected starting state and
/// can assert pixel-perfect outcomes without depending on MuPDF.
/// </summary>
public sealed class AnnotationBakerTests
{
    // ─── Test helpers ─────────────────────────────────────────────────────────

    private static (byte[] pixels, RenderedPage page) MakePage(int w, int h, byte fillR = 0, byte fillG = 0, byte fillB = 0, byte fillA = 0)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i]     = fillB;
            px[i + 1] = fillG;
            px[i + 2] = fillR;
            px[i + 3] = fillA;
        }
        return (px, new RenderedPage(px, w, h, w * 4, 32, 1.0));
    }

    private static Annotation MakeAnnotation(
        AnnotationType type, AnnotationColor color, params PdfRect[] quads) =>
        new(Guid.NewGuid(), "test.pdf", 0, type, color, quads, DateTime.UtcNow);

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] px, int x, int y, int stride)
    {
        int i = y * stride + x * 4;
        return (px[i], px[i + 1], px[i + 2], px[i + 3]);
    }

    // ─── Highlight: white background ──────────────────────────────────────────

    [Fact]
    public void Highlight_OnWhiteBackground_TintsAllPixelsYellow()
    {
        var (px, page) = MakePage(4, 4, fillR: 255, fillG: 255, fillB: 255, fillA: 255);
        var ann = MakeAnnotation(AnnotationType.Highlight, AnnotationColor.Yellow,
            new PdfRect(0, 0, 4, 4));

        AnnotationBaker.BakeHighlight(px, page, ann, physicalScale: 1.0);

        // Every pixel inside the rect should now be tinted (not pure white anymore).
        var (b, g, r, a) = Pixel(px, 2, 2, page.Stride);
        Assert.Equal(255, a);
        Assert.True(r > 200, $"R should still be yellow-ish (got {r})");
        Assert.True(g > 200, $"G should still be yellow-ish (got {g})");
        Assert.True(b < 200, $"B should be reduced toward yellow (got {b})");
    }

    [Fact]
    public void Highlight_PreservesBlackTextPixels()
    {
        var (px, page) = MakePage(4, 4, fillR: 255, fillG: 255, fillB: 255, fillA: 255);
        // Set pixel (2, 2) to pure black — simulating a text glyph
        int o = 2 * page.Stride + 2 * 4;
        px[o] = 0; px[o + 1] = 0; px[o + 2] = 0; px[o + 3] = 255;

        var ann = MakeAnnotation(AnnotationType.Highlight, AnnotationColor.Yellow,
            new PdfRect(0, 0, 4, 4));

        AnnotationBaker.BakeHighlight(px, page, ann, physicalScale: 1.0);

        // Text pixel stays black (luminance < 180 threshold preserves it).
        var (b, g, r, a) = Pixel(px, 2, 2, page.Stride);
        Assert.Equal(0, b);
        Assert.Equal(0, g);
        Assert.Equal(0, r);
        Assert.Equal(255, a);
    }

    // ─── Highlight: transparent background ────────────────────────────────────

    [Fact]
    public void Highlight_OnTransparentBackground_TintsToYellowAndForcesOpaque()
    {
        // Default fill is (0, 0, 0, 0) — fully transparent. Most PDFs render this way.
        var (px, page) = MakePage(4, 4);
        var ann = MakeAnnotation(AnnotationType.Highlight, AnnotationColor.Yellow,
            new PdfRect(0, 0, 4, 4));

        AnnotationBaker.BakeHighlight(px, page, ann, physicalScale: 1.0);

        var (b, g, r, a) = Pixel(px, 1, 1, page.Stride);
        Assert.Equal(255, a);  // forced opaque inside the rect
        Assert.True(r > 200 && g > 200, "should look yellow-on-white via pre-blend");
        Assert.True(b < 200, "blue channel reduced");
    }

    // ─── Highlight: outside the rect ──────────────────────────────────────────

    [Fact]
    public void Highlight_DoesNotTouchPixelsOutsideQuad()
    {
        var (px, page) = MakePage(8, 8, fillR: 255, fillG: 255, fillB: 255, fillA: 255);
        var ann = MakeAnnotation(AnnotationType.Highlight, AnnotationColor.Yellow,
            new PdfRect(2, 2, 4, 4));   // 2..6 × 2..6

        AnnotationBaker.BakeHighlight(px, page, ann, physicalScale: 1.0);

        // (0, 0) is outside the quad → must still be pure white.
        var (b, g, r, a) = Pixel(px, 0, 0, page.Stride);
        Assert.Equal((255, 255, 255, 255), (b, g, r, a));

        // (7, 7) is also outside.
        (b, g, r, a) = Pixel(px, 7, 7, page.Stride);
        Assert.Equal((255, 255, 255, 255), (b, g, r, a));

        // (3, 3) is inside → should be tinted.
        (b, g, r, a) = Pixel(px, 3, 3, page.Stride);
        Assert.True(b < 200, "(3,3) is inside the quad, should be tinted");
    }

    // ─── Highlight: color variants ────────────────────────────────────────────

    [Theory]
    [InlineData(AnnotationColor.Yellow)]
    [InlineData(AnnotationColor.Green)]
    [InlineData(AnnotationColor.Pink)]
    [InlineData(AnnotationColor.Blue)]
    [InlineData(AnnotationColor.Red)]
    public void Highlight_AllPaletteColors_TintWhitePixel(AnnotationColor color)
    {
        var (px, page) = MakePage(2, 2, fillR: 255, fillG: 255, fillB: 255, fillA: 255);
        var ann = MakeAnnotation(AnnotationType.Highlight, color, new PdfRect(0, 0, 2, 2));

        AnnotationBaker.BakeHighlight(px, page, ann, physicalScale: 1.0);

        var (b, g, r, a) = Pixel(px, 0, 0, page.Stride);
        Assert.Equal(255, a);
        // Every palette color shifts at least one channel away from pure white.
        bool changed = b != 255 || g != 255 || r != 255;
        Assert.True(changed, $"color {color} should change at least one channel");
    }

    [Fact]
    public void Highlight_DistinctColorsProduceDistinctOutputs()
    {
        // Run two colors on identical input and verify the resulting pixel differs —
        // catches any future regression where two palette entries collapse to the same tint.
        var (yellowPx, yellowPage) = MakePage(2, 2, fillR: 255, fillG: 255, fillB: 255, fillA: 255);
        var (greenPx, greenPage)   = MakePage(2, 2, fillR: 255, fillG: 255, fillB: 255, fillA: 255);

        AnnotationBaker.BakeHighlight(yellowPx, yellowPage,
            MakeAnnotation(AnnotationType.Highlight, AnnotationColor.Yellow, new PdfRect(0, 0, 2, 2)),
            physicalScale: 1.0);
        AnnotationBaker.BakeHighlight(greenPx, greenPage,
            MakeAnnotation(AnnotationType.Highlight, AnnotationColor.Green, new PdfRect(0, 0, 2, 2)),
            physicalScale: 1.0);

        Assert.NotEqual(yellowPx, greenPx);
    }

    // ─── Underline ────────────────────────────────────────────────────────────

    [Fact]
    public void Underline_PaintsSolidLineAtBaselineAndForcesOpaque()
    {
        // 10-pixel tall page; underline should land at Y * 0.92 = ~9 (last row).
        var (px, page) = MakePage(10, 10);  // transparent
        var ann = MakeAnnotation(AnnotationType.Underline, AnnotationColor.Yellow,
            new PdfRect(0, 0, 10, 10));

        AnnotationBaker.BakeUnderline(px, page, ann, physicalScale: 1.0);

        // Top of the page should remain untouched (transparent).
        var topPx = Pixel(px, 5, 0, page.Stride);
        Assert.Equal((0, 0, 0, 0), topPx);

        // Bottom row should have the underline color.
        var bottomPx = Pixel(px, 5, 9, page.Stride);
        Assert.Equal(255, bottomPx.A);
        // Yellow underline = (204, 153, 0) in RGB → (B, G, R) order = (0, 153, 204).
        Assert.Equal(0, bottomPx.B);
        Assert.Equal(153, bottomPx.G);
        Assert.Equal(204, bottomPx.R);
    }

    [Fact]
    public void Underline_DoesNotPaintOutsideHorizontalRange()
    {
        // Quad covers x=2..6 horizontally on a 10-wide page; underline column 0 should be untouched.
        var (px, page) = MakePage(10, 10);
        var ann = MakeAnnotation(AnnotationType.Underline, AnnotationColor.Yellow,
            new PdfRect(2, 0, 4, 10));

        AnnotationBaker.BakeUnderline(px, page, ann, physicalScale: 1.0);

        // x=0 (outside quad) at the underline Y should still be transparent.
        var px00 = Pixel(px, 0, 9, page.Stride);
        Assert.Equal((0, 0, 0, 0), px00);
        // x=3 (inside) should be painted.
        var px39 = Pixel(px, 3, 9, page.Stride);
        Assert.Equal(255, px39.A);
    }

    // ─── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Highlight_EmptyQuadList_DoesNothing()
    {
        var (px, page) = MakePage(4, 4, fillR: 255, fillG: 255, fillB: 255, fillA: 255);
        var snapshot = (byte[])px.Clone();

        var ann = MakeAnnotation(AnnotationType.Highlight, AnnotationColor.Yellow /* no quads */);
        AnnotationBaker.BakeHighlight(px, page, ann, physicalScale: 1.0);

        Assert.Equal(snapshot, px);
    }

    [Fact]
    public void Highlight_QuadLargerThanPage_ClampsToBounds_NoOverflow()
    {
        var (px, page) = MakePage(4, 4, fillR: 255, fillG: 255, fillB: 255, fillA: 255);
        // Quad extends well past the page — should clamp to (0..4, 0..4) internally.
        var ann = MakeAnnotation(AnnotationType.Highlight, AnnotationColor.Yellow,
            new PdfRect(-5, -5, 100, 100));

        var ex = Record.Exception(() =>
            AnnotationBaker.BakeHighlight(px, page, ann, physicalScale: 1.0));

        Assert.Null(ex);  // must not crash on overflow / negative origins
    }
}
