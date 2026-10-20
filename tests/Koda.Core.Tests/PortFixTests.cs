using System.Globalization;
using System.Net;
using Koda.Core.Playback;
using Koda.Core.Storage;

namespace Koda.Core.Tests;

/// <summary>Regressions for port mistakes found against KodaCore (see docs\porting-mistakes.md).</summary>
public class PortFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Fixtures.Scratch, $"audio-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void NumericStringsParseTheSameUnderAnyCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // "," decimal, "." grouping
            var json = Json.Parse("""{"d": "262571.5", "n": "42"}""");
            Assert.Equal(262571.5, json["d"].Double);
            Assert.Equal(42, json["n"].Int);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void SongsCompareByValueArtistsIncluded()
    {
        static Song Parse() => new("id", "t", "A", [new ArtistRef("UC1", "A")], null, 1, null, false, null);
        Assert.Equal(Parse(), Parse());
        Assert.Equal(Parse().GetHashCode(), Parse().GetHashCode());
        Assert.NotEqual(Parse(), Parse() with { Artists = [new ArtistRef("UC2", "A")] });
    }

    [Fact]
    public void AnotherFormatOfTheSongTakesItsLength()
    {
        var store = new AudioStore(_dir);
        store.Prepare(Stream(length: 100), null);
        store.Write(new byte[100], 0, "v");
        Assert.True(store.GetEntry("v")!.IsComplete);

        store.Prepare(Stream(length: 80), null); // a different itag: other length
        var entry = store.GetEntry("v")!;
        Assert.Equal(80, entry.ContentLength);
        Assert.Equal(0, entry.Bytes);
        Assert.Equal((0L, 80L), store.FirstGap("v"));
    }

    [Fact]
    public async Task ASupersededRequestIsCancelledAndBytesLandWhereTheyBelong()
    {
        const long length = 4 * 1024 * 1024;
        var handler = new RangeHandler(length, blockFirst: true);
        var store = new AudioStore(_dir);
        using var loader = CachingLoader.Create(Stream(length), null, store, handler)!;

        // The player asks for the head, then seeks away and gives that read up (didCancel).
        using var gaveUp = new CancellationTokenSource();
        var head = loader.ReadAsync(0, new byte[16], gaveUp.Token);
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gaveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => head.WaitAsync(TimeSpan.FromSeconds(5)));

        // Far past the running request's reach: it must be replaced, not joined by a second one.
        var tail = new byte[16];
        Assert.Equal(16, await loader.ReadAsync(3 * 1024 * 1024, tail).WaitAsync(TimeSpan.FromSeconds(10)));
        await handler.FirstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the superseded request was cancelled
        Assert.Equal(RangeHandler.Expected(3 * 1024 * 1024, 16), tail);

        // And the head, asked again, is fetched to its own offset.
        var again = new byte[16];
        Assert.Equal(16, await loader.ReadAsync(0, again).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(RangeHandler.Expected(0, 16), again);
    }

    [Fact]
    public async Task DisposeFailsReadsStillWaiting()
    {
        var handler = new RangeHandler(1_000_000, blockFirst: true);
        var store = new AudioStore(_dir);
        var loader = CachingLoader.Create(Stream(1_000_000), null, store, handler)!;
        var read = loader.ReadAsync(0, new byte[16]);
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        loader.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static ResolvedStream Stream(long length) =>
        new("v", new Uri("https://rr1---sn-test.googlevideo.com/videoplayback"), "ua", 140, length, 200, null, null);

    /// <summary>Serves a deterministic byte pattern for any Range; optionally hangs the first request until cancelled.</summary>
    private sealed class RangeHandler(long length, bool blockFirst) : HttpMessageHandler
    {
        private int _calls;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static byte[] Expected(long offset, int count)
        {
            var bytes = new byte[count];
            for (var i = 0; i < count; i++) bytes[i] = (byte)((offset + i) % 251);
            return bytes;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (Interlocked.Increment(ref _calls) == 1 && blockFirst)
            {
                FirstStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellation);
                }
                catch (OperationCanceledException)
                {
                    FirstCancelled.TrySetResult();
                    throw;
                }
            }

            var range = request.Headers.Range!.Ranges.Single();
            var from = range.From!.Value;
            var to = Math.Min(range.To ?? length - 1, length - 1);
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Expected(from, (int)(to - from + 1))),
            };
        }
    }
}
