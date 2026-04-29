namespace EasyPDF.Core.Models;

/// <summary>A point in MuPDF coordinates (top-left origin, Y increases downward), in PDF points.</summary>
public readonly record struct PdfPoint(float X, float Y);
