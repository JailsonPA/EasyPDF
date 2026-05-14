using EasyPDF.Core.Models;

namespace EasyPDF.Core.Rendering;

/// <summary>
/// Pixel-level compositing for highlight and underline annotations onto a rendered
/// BGRA page bitmap. Single source of truth shared between the on-screen viewer and
/// the "Export Annotated PDF" pipeline so both produce the exact same visual result.
///
/// Mutates the pixel buffer in place. Callers are responsible for cloning if the
/// original needs to be preserved.
///
/// Highlight algorithm (Adobe-style "marca-texto"):
///   1. Pre-blend each pixel onto white paper, using its existing alpha. This makes
///      MuPDF's transparent backgrounds (default for BGRA) behave as if they were
///      already white when classifying text vs. background.
///   2. Compute luminance of the pre-blended pixel.
///   3. lum &lt; 180 → preserve original pre-blended color (text + anti-alias edges).
///   4. lum 180–255 → linear ramp of tint intensity (smooth boundary, no aliasing).
///   5. Force alpha=255 inside the rect (avoids transparent "seam" against the page).
///
/// Underline algorithm: solid colored line at ~92% down the text quad (near baseline),
/// thickness scales with physicalScale so the line stays ~2pt thick regardless of zoom.
/// </summary>
public static class AnnotationBaker
{
    // Highlight tint colors. Kept here as the single source of truth.
    // (R, G, B, alpha) where alpha is the tint strength on a fully-white background.
    private static readonly (float R, float G, float B, float Alpha) YellowTint = (250f, 224f, 102f, 0.70f);
    private static readonly (float R, float G, float B, float Alpha) GreenTint  = (110f, 220f, 150f, 0.65f);
    private static readonly (float R, float G, float B, float Alpha) PinkTint   = (240f, 175f, 210f, 0.70f);
    private static readonly (float R, float G, float B, float Alpha) BlueTint   = (130f, 185f, 240f, 0.65f);
    private static readonly (float R, float G, float B, float Alpha) RedTint    = (245f, 145f, 145f, 0.65f);

    // Underline colors (solid; no blend).
    private static readonly (byte R, byte G, byte B) YellowUnderline = (204, 153, 0);
    private static readonly (byte R, byte G, byte B) GreenUnderline  = (0, 102, 0);
    private static readonly (byte R, byte G, byte B) PinkUnderline   = (204, 0, 102);
    private static readonly (byte R, byte G, byte B) BlueUnderline   = (0, 70, 180);
    private static readonly (byte R, byte G, byte B) RedUnderline    = (180, 0, 0);

    // Luminance threshold below which a pixel is considered text and left untouched.
    // Set high enough (180) that colored text (dark navy, dark green, etc.) and its
    // anti-aliased edges fall under it and keep their original color.
    private const float TextPreserveLumThreshold = 180f;

    // Vertical position of the underline within the text quad (0 = top, 1 = bottom).
    // 0.92 sits just below the baseline for typical Latin fonts — same convention
    // used by Adobe/Acrobat.
    private const double UnderlineYRatio = 0.92;

    /// <summary>
    /// Applies a highlight annotation directly to the BGRA pixel buffer.
    /// </summary>
    public static void BakeHighlight(byte[] pixels, RenderedPage page, Annotation ann, double physicalScale)
    {
        var tint = ann.Color switch
        {
            AnnotationColor.Green => GreenTint,
            AnnotationColor.Pink  => PinkTint,
            AnnotationColor.Blue  => BlueTint,
            AnnotationColor.Red   => RedTint,
            _                     => YellowTint,
        };

        foreach (var quad in ann.Quads)
        {
            int x0 = Math.Max(0, (int)(quad.X * physicalScale));
            int y0 = Math.Max(0, (int)(quad.Y * physicalScale));
            int x1 = Math.Min(page.Width,  (int)Math.Ceiling((quad.X + quad.Width)  * physicalScale));
            int y1 = Math.Min(page.Height, (int)Math.Ceiling((quad.Y + quad.Height) * physicalScale));

            int rectByteWidth = (x1 - x0) * 4;
            if (rectByteWidth <= 0 || y0 >= y1) continue;

            for (int py = y0; py < y1; py++)
            {
                if (py < 0 || py >= page.Height) continue;
                Span<byte> rowSpan = pixels.AsSpan(py * page.Stride + x0 * 4, rectByteWidth);

                for (int x = 0; x < rowSpan.Length; x += 4)
                {
                    float srcB = rowSpan[x];
                    float srcG = rowSpan[x + 1];
                    float srcR = rowSpan[x + 2];
                    float srcA = rowSpan[x + 3];

                    // 1. Pre-blend onto white paper so transparent areas (MuPDF's default
                    //    BGRA background) become white before luminance classification.
                    float a = srcA / 255f;
                    float invA = 1f - a;
                    float baseB = srcB * a + 255f * invA;
                    float baseG = srcG * a + 255f * invA;
                    float baseR = srcR * a + 255f * invA;

                    float lum = 0.299f * baseR + 0.587f * baseG + 0.114f * baseB;

                    if (lum < TextPreserveLumThreshold)
                    {
                        // Text + anti-alias edges — keep original color, force opaque so
                        // there's no transparent hole inside the highlight rect.
                        rowSpan[x]     = (byte)baseB;
                        rowSpan[x + 1] = (byte)baseG;
                        rowSpan[x + 2] = (byte)baseR;
                        rowSpan[x + 3] = 255;
                        continue;
                    }

                    // Smooth ramp from preserve threshold to full tint at white.
                    float pixelAlpha = lum < 255f
                        ? tint.Alpha * ((lum - TextPreserveLumThreshold) / (255f - TextPreserveLumThreshold))
                        : tint.Alpha;

                    float inv = 1f - pixelAlpha;
                    rowSpan[x]     = (byte)(baseB * inv + tint.B * pixelAlpha);
                    rowSpan[x + 1] = (byte)(baseG * inv + tint.G * pixelAlpha);
                    rowSpan[x + 2] = (byte)(baseR * inv + tint.R * pixelAlpha);
                    rowSpan[x + 3] = 255;
                }
            }
        }
    }

    /// <summary>
    /// Applies an underline annotation directly to the BGRA pixel buffer.
    /// </summary>
    public static void BakeUnderline(byte[] pixels, RenderedPage page, Annotation ann, double physicalScale)
    {
        var (uR, uG, uB) = ann.Color switch
        {
            AnnotationColor.Green => GreenUnderline,
            AnnotationColor.Pink  => PinkUnderline,
            AnnotationColor.Blue  => BlueUnderline,
            AnnotationColor.Red   => RedUnderline,
            _                     => YellowUnderline,
        };

        // Scale thickness with physicalScale so the underline stays roughly 2pt thick
        // regardless of zoom level / export DPI.
        int thicknessPx = Math.Max(1, (int)Math.Round(2 * physicalScale));

        foreach (var quad in ann.Quads)
        {
            int x0 = Math.Max(0, (int)(quad.X * physicalScale));
            int x1 = Math.Min(page.Width, (int)Math.Ceiling((quad.X + quad.Width) * physicalScale));
            int yLine = (int)((quad.Y + quad.Height * UnderlineYRatio) * physicalScale);

            int rectByteWidth = (x1 - x0) * 4;
            if (rectByteWidth <= 0) continue;

            for (int py = yLine; py < yLine + thicknessPx; py++)
            {
                if (py < 0 || py >= page.Height) continue;
                Span<byte> rowSpan = pixels.AsSpan(py * page.Stride + x0 * 4, rectByteWidth);
                for (int x = 0; x < rowSpan.Length; x += 4)
                {
                    rowSpan[x]     = uB;
                    rowSpan[x + 1] = uG;
                    rowSpan[x + 2] = uR;
                    rowSpan[x + 3] = 255;
                }
            }
        }
    }
}
