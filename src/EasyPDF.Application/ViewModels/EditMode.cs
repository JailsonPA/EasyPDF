namespace EasyPDF.Application.ViewModels;

public enum EditMode
{
    None,       // leitura normal
    Highlight,  // marcador de texto (via context menu)
    Underline,  // sublinhado (via context menu)
    Note,       // clicar na página cria uma nota de texto
    Ink,        // desenho livre
    Eraser,     // apaga traços ink próximos ao clique
    ImageStamp,
}
