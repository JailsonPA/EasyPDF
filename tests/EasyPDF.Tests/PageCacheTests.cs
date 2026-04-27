using EasyPDF.Core.Models;
using EasyPDF.Infrastructure.Pdf;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EasyPDF.Tests;

public sealed class PageCacheTests
{
    // 1 MB cache = 1,048,576 bytes / 4 bytes per pixel = 262,144 pixels capacity
    private static PageCache MakeCache(int maxMegabytes = 1) =>
        new(NullLogger<PageCache>.Instance, maxMegabytes);

    private static RenderedPage MakePage(int width, int height) =>
        new(new byte[width * height * 4], width, height, width * 4, 32);

    [Fact]
    public void Get_MissingKey_ReturnsNull()
    {
        Assert.Null(MakeCache().Get("nope"));
    }

    [Fact]
    public void Set_Then_Get_ReturnsSamePage()
    {
        var cache = MakeCache();
        var page = MakePage(100, 100);

        cache.Set("k", page);

        Assert.Same(page, cache.Get("k"));
    }

    [Fact]
    public void LRU_Eviction_RemovesLeastRecentlyUsed()
    {
        // 1 MB = 262,144 px. 200×200 = 40,000 px each.
        // 6 pages = 240,000 px (fits); 7th triggers eviction of k1 (LRU).
        var cache = MakeCache(1);

        for (int i = 1; i <= 6; i++)
            cache.Set($"k{i}", MakePage(200, 200));

        cache.Set("k7", MakePage(200, 200));

        Assert.Null(cache.Get("k1"));     // evicted — was LRU
        Assert.NotNull(cache.Get("k7")); // just added
    }

    [Fact]
    public void Get_PromotesToMRU_SurvivesEviction()
    {
        // Fill 6 pages; access k1 (→ MRU); insert k7 → should evict k2 (new LRU), not k1.
        var cache = MakeCache(1);

        for (int i = 1; i <= 6; i++)
            cache.Set($"k{i}", MakePage(200, 200));

        cache.Get("k1"); // promote k1 to MRU; k2 becomes LRU

        cache.Set("k7", MakePage(200, 200)); // evicts k2

        Assert.Null(cache.Get("k2"));     // evicted
        Assert.NotNull(cache.Get("k1")); // promoted, survived
    }

    [Fact]
    public void Set_ExistingKey_RefreshesPosition_AndSurvivesEviction()
    {
        // Fill 6; re-set k1 (becomes MRU); add k7 → evicts k2 (new LRU), not k1.
        var cache = MakeCache(1);

        for (int i = 1; i <= 6; i++)
            cache.Set($"k{i}", MakePage(200, 200));

        cache.Set("k1", MakePage(200, 200)); // refresh → k1 is MRU, k2 is LRU

        cache.Set("k7", MakePage(200, 200)); // evicts k2

        Assert.Null(cache.Get("k2"));
        Assert.NotNull(cache.Get("k1"));
    }

    [Fact]
    public void Set_PageExceedingHalfCapacity_IsNotStored()
    {
        // 1 MB capacity = 262,144 px; half = 131,072 px. 400×400 = 160,000 px > half.
        var cache = MakeCache(1);

        cache.Set("big", MakePage(400, 400));

        Assert.Null(cache.Get("big"));
    }

    [Fact]
    public void Set_PageBelowHalfCapacity_IsStored()
    {
        // 362×362 = 131,044 px < 131,072 px (half capacity).
        var cache = MakeCache(1);

        cache.Set("fits", MakePage(362, 362));

        Assert.NotNull(cache.Get("fits"));
    }

    [Fact]
    public void Invalidate_RemovesMatchingKeys_KeepsOthers()
    {
        var cache = MakeCache();
        cache.Set("/docs/a.pdf:0:1.0", MakePage(10, 10));
        cache.Set("/docs/a.pdf:1:1.0", MakePage(10, 10));
        cache.Set("/other/b.pdf:0:1.0", MakePage(10, 10));

        cache.Invalidate("/docs/a.pdf");

        Assert.Null(cache.Get("/docs/a.pdf:0:1.0"));
        Assert.Null(cache.Get("/docs/a.pdf:1:1.0"));
        Assert.NotNull(cache.Get("/other/b.pdf:0:1.0")); // untouched
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = MakeCache();
        cache.Set("a", MakePage(10, 10));
        cache.Set("b", MakePage(10, 10));

        cache.Clear();

        Assert.Null(cache.Get("a"));
        Assert.Null(cache.Get("b"));
    }
}
