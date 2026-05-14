using EasyPDF.Core.Models;

namespace EasyPDF.Core.Interfaces;

public interface IPageCache
{
    RenderedPage? Get(string key);
    void Set(string key, RenderedPage page);
    void Clear();
}
