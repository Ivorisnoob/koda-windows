using System.Net;
using System.Net.Http.Headers;
using Koda.Core.Storage;

namespace Koda.Core.Tests;

/// <summary>
/// Downloads: fetched a chunk at a time into the audio store, kept through relaunches, and let
/// go when the video can never play.
/// </summary>
public class DownloadsTests
{
    private const int Length = (1 << 20) + 4096; // just over one chunk, so two requests

    private static Song Song(string id) => new(id, $"Song {id}", "Artist", [], null, 120, null, false, null);

    private static ResolvedStream Stream(string id) =>
        new(id, new Uri($"https://example.invalid/{id}"), "test-agent", 140, Length, 120, null, null);

    [Fact]
    public async Task ASongIsFetchedWholeAndKept()
    {
        var store = new AudioStore(Path.Combine(Fixtures.Scratch, $"downloads-{Guid.NewGuid():N}"));
        var server = new RangedServer();
        using var downloads = new Downloads(store, (id, _) => Task.FromResult(Stream(id)), _ => Task.CompletedTask, server);
        var song = Song("song0000001");

        downloads.Download([song]);
        await WaitAsync(() => downloads.StateOf(song).Phase == Downloads.Phase.Downloaded);

        Assert.Equal(Downloads.Phase.Downloaded, downloads.StateOf(song).Phase);
        Assert.True(store.GetEntry(song.Id)!.IsComplete);
        Assert.True(store.GetEntry(song.Id)!.IsDownload);
        Assert.Equal(2, server.Requests);
        Assert.NotNull(store.CompleteStream(song.Id));
        Assert.Equal([song], downloads.Songs);
    }

    [Fact]
    public async Task ACollectionReportsTheShareThatIsHere()
    {
        var store = new AudioStore(Path.Combine(Fixtures.Scratch, $"downloads-share-{Guid.NewGuid():N}"));
        using var downloads = new Downloads(store, (id, _) => Task.FromResult(Stream(id)), _ => Task.CompletedTask, new RangedServer());
        var here = Song("song0000002");
        var notAsked = Song("song0000003");

        downloads.Download([here]);
        await WaitAsync(() => downloads.StateOf(here).Phase == Downloads.Phase.Downloaded);

        var state = downloads.StateOf([here, notAsked]);
        Assert.Equal(Downloads.Phase.None, state.Phase); // one of two, nothing on its way
        Assert.Equal(Downloads.Phase.Downloaded, downloads.StateOf([here]).Phase);
    }

    [Fact]
    public async Task AnUnplayableVideoIsLetGo()
    {
        var store = new AudioStore(Path.Combine(Fixtures.Scratch, $"downloads-gone-{Guid.NewGuid():N}"));
        using var downloads = new Downloads(store,
            (_, _) => Task.FromException<ResolvedStream>(MusicException.Unplayable("removed")),
            _ => Task.CompletedTask, new RangedServer());
        var song = Song("song0000004");

        downloads.Download([song]);
        await WaitAsync(() => downloads.Songs.Length == 0);

        Assert.Empty(downloads.Songs);
        Assert.Equal(Downloads.Phase.None, downloads.StateOf(song).Phase);
    }

    [Fact]
    public async Task AnUnfinishedDownloadCarriesOnAtTheNextLaunch()
    {
        var directory = Path.Combine(Fixtures.Scratch, $"downloads-later-{Guid.NewGuid():N}");
        var song = Song("song0000005");
        var first = new AudioStore(directory);
        first.MarkDownloaded([song], DateTimeOffset.UtcNow);
        first.Flush();

        var store = new AudioStore(directory);
        using var downloads = new Downloads(store, (id, _) => Task.FromResult(Stream(id)), _ => Task.CompletedTask, new RangedServer());
        Assert.Equal(Downloads.Phase.Waiting, downloads.StateOf(song).Phase);

        downloads.Start();
        await WaitAsync(() => downloads.StateOf(song).Phase == Downloads.Phase.Downloaded);
        Assert.Equal(Downloads.Phase.Downloaded, downloads.StateOf(song).Phase);
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 250 && !done(); i++) await Task.Delay(20);
    }

    /// <summary>googlevideo, answering ranges of a file of <see cref="Length"/> bytes.</summary>
    private sealed class RangedServer : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Interlocked.Increment(ref _requests);
            var range = request.Headers.GetValues("Range").Single()["bytes=".Length..].Split('-');
            var from = long.Parse(range[0]);
            var to = Math.Min(long.Parse(range[1]), Length - 1);
            var body = new byte[to - from + 1];
            for (var i = 0; i < body.Length; i++) body[i] = (byte)((from + i) % 251);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, Length);
            return Task.FromResult(response);
        }
    }
}
