using CommunityToolkit.Mvvm.ComponentModel;

namespace EasyPDF.Application.ViewModels;

/// <summary>
/// Representa uma anotação de texto livre visível sobre a página.
/// Coordenadas X/Y são em pixels da tela (já multiplicadas por Scale).
/// </summary>
public sealed partial class NoteAnnotationViewModel : ObservableObject
{
    public Guid Id { get; }

    // Posição no canvas da página (em pixels display)
    public double X { get; }
    public double Y { get; }

    [ObservableProperty]
    private string _content;

    [ObservableProperty]
    private bool _isExpanded;

    public string ColorHex { get; }

    public NoteAnnotationViewModel(Guid id, double x, double y, string content, string colorHex = "#FFFDE68A")
    {
        Id = id;
        X = x;
        Y = y;
        _content = content;
        ColorHex = colorHex;
    }
}
