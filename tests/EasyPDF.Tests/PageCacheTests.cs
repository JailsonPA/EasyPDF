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

    // ─── Pixel-tracking correctness ──────────────────────────────────────────

    [Fact]
    public void Clear_ResetsPixelTracking_AllowsRefillToFullCapacity()
    {
        // Fill to near capacity (6 × 200×200 = 240,000 px of 262,144 max),
        // clear, then verify full capacity is available by filling again.
        var cache = MakeCache(1);

        for (int i = 0; i < 6; i++)
            cache.Set($"pre{i}", MakePage(200, 200));

        cache.Clear();

        // If pixel tracking was not reset, these sets would trigger premature evictions.
        for (int i = 0; i < 6; i++)
            cache.Set($"post{i}", MakePage(200, 200));

        for (int i = 0; i < 6; i++)
            Assert.NotNull(cache.Get($"post{i}"));
    }

    [Fact]
    public void Set_NewPage_ThatRequiresMultipleEvictions_EvictsInLruOrder()
    {
        // 1 MB = 262,144 px.
        // Insert 5 × 100×100 = 50,000 px total (fits).
        // Insert one 400×300 = 120,000 px page: needs to evict enough LRU entries to fit.
        // 50,000 + 120,000 = 170,000 px — fits without eviction (< 262,144).
        // Instead use 6 × 200×200 = 240,000 px, then add 1 × 200×200 again → evicts k1.
        // For multi-eviction: fill 6 × 200×200 (240,000 px), then add 1 × 250×250 (62,500 px).
        // Total would be 302,500 > 262,144, so k1 evicted (40,000 freed → 262,500-40,000=222,500+62,500=262,500 still too big).
        // k2 also evicted → 262,500-80,000=182,500+62,500=245,000 px → fits.
        var cache = MakeCache(1);

        for (int i = 1; i <= 6; i++)
            cache.Set($"k{i}", MakePage(200, 200)); // 6 × 40,000 = 240,000 px

        cache.Set("big", MakePage(250, 250)); // 62,500 px → needs to evict k1 + k2

        Assert.Null(cache.Get("k1"));   // LRU — evicted first
        Assert.Null(cache.Get("k2"));   // second LRU — also evicted
        Assert.NotNull(cache.Get("k3")); // survived
        Assert.NotNull(cache.Get("big"));
    }

    [Fact]
    public void Set_ExistingKey_SmallerReplacement_FreesPixels_AllowingNewInsert()
    {
        // Replace a 200×200 page (40,000 px) with a 100×100 (10,000 px).
        // The freed 30,000 px must be subtracted from _currentPixels so that a
        // subsequent insert fits without triggering an unwanted eviction.
        var cache = MakeCache(1); // 262,144 px max

        for (int i = 1; i <= 6; i++)
            cache.Set($"k{i}", MakePage(200, 200)); // 6 × 40,000 = 240,000 px

        // Replace k1 (LRU, 40,000 px) with 100×100 (10,000 px) → frees 30,000 px → 210,000 px.
        // k1 is refreshed to MRU position by the replacement.
        cache.Set("k1", MakePage(100, 100));

        // 210,000 + 40,000 = 250,000 px < 262,144 — fits without eviction.
        cache.Set("new", MakePage(200, 200));

        Assert.NotNull(cache.Get("k1")); // refreshed and fits — not evicted
        Assert.NotNull(cache.Get("new"));
    }

    [Fact]
    public void Eviction_FreesPixels_AllowingSubsequentInserts()
    {
        // After k1 is evicted, its 40,000 px should be freed so a same-size page fits.
        var cache = MakeCache(1);

        for (int i = 1; i <= 6; i++)
            cache.Set($"k{i}", MakePage(200, 200)); // fills to 240,000 px

        cache.Set("k7", MakePage(200, 200)); // evicts k1, then fits k7 (240,000 px)
        cache.Set("k8", MakePage(200, 200)); // evicts k2, fits k8

        Assert.Null(cache.Get("k1"));
        Assert.Null(cache.Get("k2"));
        Assert.NotNull(cache.Get("k7"));
        Assert.NotNull(cache.Get("k8"));
    }
}
