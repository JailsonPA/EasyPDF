using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;

namespace EasyPDF.Infrastructure.Pdf;

public sealed class PageCache : IPageCache
{
    private readonly int _maxPixels;
    private readonly ILogger<PageCache> _logger;

    private readonly Dictionary<string, CacheEntry> _store = new();
    private readonly LinkedList<string> _lruOrder = new(); // front = MRU, back = LRU
    private readonly object _lock = new();
    private int _currentPixels;

    public PageCache(ILogger<PageCache> logger, int maxMegabytes = 512)
    {
        _logger = logger;
        // 4 bytes per pixel (BGRA)
        _maxPixels = maxMegabytes * 1024 * 1024 / 4;
    }

    public RenderedPage? Get(string key)
    {
        lock (_lock)
        {
            if (!_store.TryGetValue(key, out var entry)) return null;
            // Promote to most-recently-used — O(1) because we store the node reference.
            _lruOrder.Remove(entry.LruNode);
            _lruOrder.AddFirst(entry.LruNode);
            return entry.Page;
        }
    }

    public void Set(string key, RenderedPage page)
    {
        int pixels = page.Width * page.Height;

        if (pixels > _maxPixels / 2)
        {
            _logger.LogDebug(
                "Page skipped by cache — {Pixels:N0} px exceeds half-capacity ({Limit:N0} px)",
                pixels, _maxPixels / 2);
            return;
        }

        lock (_lock)
        {
            if (_store.TryGetValue(key, out var existing))
            {
                _lruOrder.Remove(existing.LruNode);
                _currentPixels -= existing.Page.Width * existing.Page.Height;
                _store.Remove(key);
            }

            while (_currentPixels + pixels > _maxPixels && _lruOrder.Count > 0)
                EvictLast();

            var node = _lruOrder.AddFirst(key);
            _store[key] = new CacheEntry(page, node);
            _currentPixels += pixels;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _store.Clear();
            _lruOrder.Clear();
            _currentPixels = 0;
        }
    }

    private void EvictLast()
    {
        var tail = _lruOrder.Last;
        if (tail is not null) RemoveEntry(tail.Value);
    }

    private void RemoveEntry(string key)
    {
        if (_store.TryGetValue(key, out var entry))
        {
            _lruOrder.Remove(entry.LruNode);
            _currentPixels -= entry.Page.Width * entry.Page.Height;
            _store.Remove(key);
        }
    }

    private sealed class CacheEntry(RenderedPage page, LinkedListNode<string> lruNode)
    {
        public RenderedPage Page { get; } = page;
        public LinkedListNode<string> LruNode { get; } = lruNode;
    }
}
