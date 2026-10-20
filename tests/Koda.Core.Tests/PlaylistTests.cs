using Koda.Core;
using Koda.Core.Parsing;

namespace Koda.Core.Tests;

public class PlaylistTests
{
    [Fact]
    public void PlaylistPageReadsHeaderShelfAndContinuation()
    {
        var page = Pages.Playlist(Fixtures.Load("playlist.json"), "PLfCLUMeAUh5c");

        Assert.NotNull(page);
        Assert.Equal("Chill EDM Hits", page!.Playlist.Title);
        Assert.Null(page.Author); // the signed-out playlist carries no strapline
        Assert.Equal("144 songs • 6+ hours", page.DetailLine);
        Assert.Equal(100, page.Songs.Length); // the shelf's 101st entry is its continuation, not a track
        Assert.Equal("Faded", page.Songs[0].Title);
        Assert.Equal("60ItHLz5WEA", page.Songs[0].Id);
        Assert.NotNull(page.Continuation);
    }

    [Fact]
    public void ContinuationPageReadsAppendedItems()
    {
        var songs = Pages.PlaylistSongs(Fixtures.Load("playlist-continuation.json"));

        Assert.Equal(44, songs.Length);
        // No new token rides appended items; the shelf's own token already did its job.
        Assert.Null(Pages.PlaylistContinuation(Fixtures.Load("playlist-continuation.json")));
    }

    [Fact]
    public void EmptyRootParsesAsNoPage()
    {
        Assert.Null(Pages.Playlist(Json.Parse("{}"), "VLx"));
        Assert.Null(Pages.Album(Json.Parse("{}"), "MPREb_x"));
        Assert.Empty(Pages.PlaylistSongs(Json.Parse("{}")));
        Assert.Null(Pages.PlaylistContinuation(Json.Parse("{}")));
    }
}
