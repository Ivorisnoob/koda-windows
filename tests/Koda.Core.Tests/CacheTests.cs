using System.Diagnostics;
using Koda.Core;

namespace Koda.Core.Tests;

public class CacheTests
{
    [Fact]
    public async Task PageCacheAnswersFromTheLoaderOnce()
    {
        var cache = new PageCache();
        var loads = 0;
        var one = await cache.Value("key", () =>
        {
            loads++;
            return Task.FromResult("first");
        });
        var two = await cache.Value("key", () =>
        {
            loads++;
            return Task.FromResult("should-not-load");
        });

        Assert.Equal("first", one);
        Assert.Equal("first", two);
        Assert.Equal(1, loads);

        cache.RemoveAll();
        var reloaded = await cache.Value("key", () =>
        {
            loads++;
            return Task.FromResult("reload");
        });
        Assert.Equal("reload", reloaded);
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task PageCacheExpiryReloads()
    {
        var cache = new PageCache(TimeSpan.FromMilliseconds(50));
        Assert.Equal("v1", await cache.Value("k", () => Task.FromResult("v1")));
        await Task.Delay(120);
        Assert.Equal("v2", await cache.Value("k", () => Task.FromResult("v2")));
    }

    [Fact]
    public async Task FailuresAreNeverCached()
    {
        var cache = new PageCache();
        await Assert.ThrowsAsync<IOException>(() =>
            cache.Value<int>("k", () => Task.FromException<int>(new IOException())));
        Assert.Equal(7, await cache.Value("k", () => Task.FromResult(7)));
    }

    [Fact]
    public async Task DifferentKeysLoadSideBySide()
    {
        // The first port held one lock across every load: B waited for A to finish.
        var cache = new PageCache();
        var releaseA = new TaskCompletionSource<string>();
        var a = cache.Value("a", () => releaseA.Task);

        var stopwatch = Stopwatch.StartNew();
        var b = await cache.Value("b", () => Task.FromResult("b")).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("b", b);
        Assert.False(a.IsCompleted);

        releaseA.SetResult("a");
        Assert.Equal("a", await a);
    }

    [Fact]
    public async Task ALoaderMayAskTheCacheForAnotherPage()
    {
        var cache = new PageCache();
        var outer = await cache.Value("outer", async () =>
            "outer+" + await cache.Value("inner", () => Task.FromResult("inner")))
            .WaitAsync(TimeSpan.FromSeconds(2)); // deadlocked under the old global lock
        Assert.Equal("outer+inner", outer);
    }

    [Fact]
    public async Task CallersAfterTheSameKeyShareOneLoad()
    {
        var cache = new PageCache();
        var loads = 0;
        var release = new TaskCompletionSource<int>();
        Task<int> Load()
        {
            Interlocked.Increment(ref loads);
            return release.Task;
        }

        var first = cache.Value("k", Load);
        var second = cache.Value("k", Load);
        release.SetResult(5);
        Assert.Equal(5, await first);
        Assert.Equal(5, await second);
        Assert.Equal(1, loads);
    }
}
