namespace EasyPDF.Core.Models;

public sealed record RenderedPage(
    byte[] PixelData,
    int Width,
    int Height,
    int Stride,
    int BitsPerPixel,  // 32 = BGRA, 24 = BGR
    double DpiScale = 1.0
);
