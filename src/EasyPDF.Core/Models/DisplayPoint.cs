namespace EasyPDF.Core.Models;

/// <summary>Ponto em coordenadas de display (pixels na tela), independente de plataforma.</summary>
public readonly record struct DisplayPoint(double X, double Y);
