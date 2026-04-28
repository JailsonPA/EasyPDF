namespace EasyPDF.Application.Interfaces;

public interface IFileDropTarget
{
    Task DropFileAsync(string filePath);
}
