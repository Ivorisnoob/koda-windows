using Koda.Core;
using Koda.Core.Parsing;

namespace Koda.Core.Tests;

public class SearchTests
{
    [Fact]
    public void AllPageReadsTheTopCardAsAnArtistWithItsSongs()
    {
        var results = Pages.Search(Fixtures.Load("search-all.json"));

        var top = Assert.IsType<MusicItem.ArtistRecord>(results.Top);
        Assert.Equal("The Weeknd", top.Value.Name);
        Assert.StartsWith("UC", top.Value.Id);
        Assert.Equal("238M monthly audience", top.Value.Subtitle);
        Assert.Equal(3, results.TopSongs.Length);
        Assert.All(results.TopSongs, s => Assert.NotEqual("", s.ArtistLine));
    }

    [Fact]
    public void AllPageSortsItemSectionRowsByKind()
    {
        var results = Pages.Search(Fixtures.Load("search-all.json"));

        // The 29 item-section rows carry 5 ATV songs, 3 videos (OMV + UGC), 3 album links,
        // 6 playlist links, 3 real artists and 3 "Profile" user channels; the 3 podcast
        // rows link non-music pages and are skipped. The card's 3 songs ride topSongs.
        Assert.Equal(5, results.Songs.Length);
        Assert.Equal(3, results.Videos.Length);
        Assert.Equal(3, results.Albums.Length);
        Assert.Equal(3, results.Artists.Length); // the 3 "Profile" user channels are filtered out
        Assert.Equal(6, results.Playlists.Length);
        Assert.All(results.Songs, s => Assert.False(s.IsVideo));
        Assert.All(results.Videos, s => Assert.True(s.IsVideo));
        Assert.All(results.Albums, a => Assert.StartsWith("MPRE", a.Id));
        // One of the section rows links "The Idol Episode 4" album.
        Assert.Contains(results.Albums, a => a.Title.Contains("The Idol Episode 4"));
    }

    [Fact]
    public void SongsPageReadsOneShelfOfSongsAndTheContinuation()
    {
        var results = Pages.Search(Fixtures.Load("search-songs.json"));

        Assert.Null(results.Top);
        Assert.Equal(20, results.Songs.Length);
        Assert.Equal("Blinding Lights", results.Songs[0].Title);
        Assert.Equal("J7p4bzqLvCw", results.Songs[0].Id);
        Assert.All(results.Songs, s => Assert.False(s.IsVideo)); // the Songs chip returns ATV tracks
        Assert.Empty(results.Videos); // nothing but ATV rows in the Songs shelf
        Assert.Empty(results.Albums);
        Assert.Empty(results.Artists);
        Assert.Empty(results.Playlists);
        var token = Pages.SearchContinuation(Fixtures.Load("search-songs.json"));
        Assert.NotNull(token);
        Assert.Equal(588, token!.Length);
    }

    [Fact]
    public void DedupeKeepsFirstEncounter()
    {
        var songs = new[]
        {
            new Song("A", "One", "x", [], null, null, null, false, null),
            new Song("B", "Two", "x", [], null, null, null, false, null),
            new Song("A", "One", "x", [], null, null, null, false, null),
        };
        var deduped = Pages.DedupeById(songs, s => s.Id);
        Assert.Equal(2, deduped.Length);
        Assert.Equal("A", deduped[0].Id);
        Assert.Equal("B", deduped[1].Id);
    }
}
