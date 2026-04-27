using EasyPDF.Core.Interfaces;
using EasyPDF.Core.Models;
using Microsoft.Extensions.Logging;

namespace EasyPDF.Infrastructure.Pdf;

/// <summary>
/// Thread-safe LRU cache for rendered PDF pages bounded by total pixel count.
///
/// Implementation: Dictionary + doubly-linked list.
///   Get  — O(1): dictionary lookup + move node to front.
///   Set  — O(1) amortised: insert at front, evict from back if over capacity.
///   Evict — O(1): remove the tail node (least recently used).
///
/// Previously used ConcurrentDictionary + LINQ OrderBy under the global lock,
/// which was O(n log n) per eviction and materialised all cache entries just to
/// find the oldest one. Under load this caused multi-millisecond stalls during
/// rendering as every eviction blocked all concurrent render threads.
///
/// All mutations of _currentPixels are inside _lock, fixing the prior data race
/// where Invalidate/Clear used Interlocked without the lock while Set/Evict used
/// plain arithmetic inside it.
/// </summary>
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

        // A single page that would consume more than half the cache is not worth
        // caching — it would evict every other page immediately. Log so it's visible
        // during profiling; the renderer simply re-renders on each scroll visit.
        if (pixels > _maxPixels / 2)
        {
            _logger.LogDebug(
                "Page skipped by cache — {Pixels:N0} px exceeds half-capacity ({Limit:N0} px)",
                pixels, _maxPixels / 2);
            return;
        }

        lock (_lock)
        {
            // Refresh existing entry so its position is reset to MRU.
            if (_store.TryGetValue(key, out var existing))
            {
                _lruOrder.Remove(existing.LruNode);
                _currentPixels -= existing.Page.Width * existing.Page.Height;
                _store.Remove(key);
            }

            // Evict least-recently-used entries until there is room.
            while (_currentPixels + pixels > _maxPixels && _lruOrder.Count > 0)
                EvictLast();

            var node = _lruOrder.AddFirst(key);
            _store[key] = new CacheEntry(page, node);
            _currentPixels += pixels;
        }
    }

    public void Invalidate(string documentPath)
    {
        lock (_lock)
        {
            foreach (var key in _store.Keys
                .Where(k => k.StartsWith(documentPath, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                RemoveEntry(key);
            }
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

    // Must be called under _lock.
    private void EvictLast()
    {
        var tail = _lruOrder.Last;
        if (tail is not null) RemoveEntry(tail.Value);
    }

    // Must be called under _lock.
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
