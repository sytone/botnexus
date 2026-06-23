using BotNexus.Gateway.Abstractions.Concurrency;

namespace BotNexus.Gateway.Tests.Concurrency;

public sealed class BoundedLruCacheTests
{
    [Fact]
    public void Set_BeyondCapacity_EvictsLeastRecentlyUsed()
    {
        var cache = new BoundedLruCache<int, string>(capacity: 2);

        cache.Set(1, "a");
        cache.Set(2, "b");
        cache.Set(3, "c"); // evicts key 1 (LRU)

        cache.Count.ShouldBe(2);
        cache.TryGet(1, out _).ShouldBeFalse();
        cache.TryGet(2, out var b).ShouldBeTrue();
        b.ShouldBe("b");
        cache.TryGet(3, out var c).ShouldBeTrue();
        c.ShouldBe("c");
    }

    [Fact]
    public void TryGet_PromotesEntry_SoItSurvivesEviction()
    {
        var cache = new BoundedLruCache<int, string>(capacity: 2);

        cache.Set(1, "a");
        cache.Set(2, "b");
        // Touch key 1 so it becomes most-recently-used; key 2 is now the LRU.
        cache.TryGet(1, out _).ShouldBeTrue();
        cache.Set(3, "c"); // should evict key 2, not key 1

        cache.TryGet(1, out _).ShouldBeTrue();
        cache.TryGet(2, out _).ShouldBeFalse();
        cache.TryGet(3, out _).ShouldBeTrue();
    }

    [Fact]
    public void Set_ExistingKey_UpdatesValueAndPromotes_WithoutGrowing()
    {
        var cache = new BoundedLruCache<int, string>(capacity: 2);

        cache.Set(1, "a");
        cache.Set(2, "b");
        cache.Set(1, "a2"); // update + promote, count stays 2
        cache.Set(3, "c");  // evicts key 2 (LRU), key 1 survives

        cache.Count.ShouldBe(2);
        cache.TryGet(1, out var v).ShouldBeTrue();
        v.ShouldBe("a2");
        cache.TryGet(2, out _).ShouldBeFalse();
    }

    [Fact]
    public void Count_NeverExceedsCapacity_AcrossManyDistinctKeys()
    {
        var cache = new BoundedLruCache<int, int>(capacity: 50);

        for (var i = 0; i < 5000; i++)
        {
            cache.Set(i, i);
            cache.Count.ShouldBeLessThanOrEqualTo(50);
        }

        cache.Count.ShouldBe(50);
        // The most recent 50 keys are retained; older ones evicted.
        cache.TryGet(4999, out _).ShouldBeTrue();
        cache.TryGet(0, out _).ShouldBeFalse();
    }

    [Fact]
    public void Remove_DeletesEntry_AndReportsWhetherPresent()
    {
        var cache = new BoundedLruCache<int, string>(capacity: 4);
        cache.Set(1, "a");

        cache.Remove(1).ShouldBeTrue();
        cache.Remove(1).ShouldBeFalse();
        cache.TryGet(1, out _).ShouldBeFalse();
        cache.Count.ShouldBe(0);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = new BoundedLruCache<int, string>(capacity: 4);
        cache.Set(1, "a");
        cache.Set(2, "b");

        cache.Clear();

        cache.Count.ShouldBe(0);
        cache.TryGet(1, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryGet_Miss_ReturnsFalseAndDefault()
    {
        var cache = new BoundedLruCache<string, string>(capacity: 4);

        cache.TryGet("nope", out var value).ShouldBeFalse();
        value.ShouldBeNull();
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new BoundedLruCache<int, int>(0));
        Should.Throw<ArgumentOutOfRangeException>(() => new BoundedLruCache<int, int>(-1));
    }

    [Fact]
    public void HonorsCustomKeyComparer()
    {
        var cache = new BoundedLruCache<string, int>(capacity: 4, StringComparer.OrdinalIgnoreCase);

        cache.Set("Key", 1);
        cache.TryGet("KEY", out var v).ShouldBeTrue();
        v.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentSetAndGet_StaysBounded_AndDoesNotThrow()
    {
        var cache = new BoundedLruCache<int, int>(capacity: 100);

        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                cache.Set((w * 2000) + i, i);
                cache.TryGet((w * 2000) + i, out _);
            }
        }));

        await Task.WhenAll(writers);

        cache.Count.ShouldBeLessThanOrEqualTo(100);
    }
}
