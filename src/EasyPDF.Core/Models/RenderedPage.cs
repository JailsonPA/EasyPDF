namespace EasyPDF.Core.Models;

/// <summary>
/// Raw pixel data for a rendered PDF page — WPF-agnostic so Core stays portable.
/// </summary>
public sealed record RenderedPage(
    byte[] PixelData,
    int Width,
    int Height,
    int Stride,
    int BitsPerPixel,  // 32 = BGRA, 24 = BGR
    double DpiScale = 1.0
);
