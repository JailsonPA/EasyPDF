using EasyPDF.Core.Models;

namespace EasyPDF.Application.ViewModels;

/// <summary>
/// Representa um traço de desenho livre sobre a página.
/// Os pontos estão em coordenadas de display (já multiplicados por Scale).
/// </summary>
public sealed class InkStrokeViewModel
{
    public Guid Id { get; }
    public IReadOnlyList<DisplayPoint> Points { get; }
    public double Thickness { get; }
    public string ColorHex { get; }

    public InkStrokeViewModel(Guid id, IReadOnlyList<DisplayPoint> points, double thickness, string colorHex)
    {
        Id = id;
        Points = points;
        Thickness = thickness;
        ColorHex = colorHex;
    }
}
