using Koda.Core;
using Koda.Core.Parsing;

namespace Koda.Core.Tests;

public class AlbumTests
{
    // The album id is Koda's own handle for the response (it is what the browse call asked for,
    // echoed back into every track row by Pages.Album); a clearly test-only value keeps that contract visible.
    private const string AlbumId = "MPREb_testid0000000";

    [Fact]
    public void AlbumPageReadsHeaderAndTracks()
    {
        var page = Pages.Album(Fixtures.Load("album.json"), AlbumId);

        Assert.NotNull(page);
        Assert.Equal(AlbumId, page!.Album.Id);
        Assert.Equal("After Hours", page.Album.Title);
        Assert.Equal("The Weeknd", page.Album.ArtistLine);
        Assert.Equal(2020, page.Album.Year);
        Assert.Equal(ReleaseKind.Album, page.Album.Kind);
        Assert.Equal("14 songs • 56 minutes", page.DetailLine);
        Assert.Equal("OLAK5uy_l4UqNJCpAF3kNaV37LHdRc_A07MmVdiSU", page.AudioPlaylistId);
        Assert.StartsWith("After Hours is the fourth studio album", page.Description);

        // Signed out, every track of this fixture is the music video.
        Assert.Equal(14, page.Songs.Length);
        Assert.All(page.Songs, s => Assert.True(s.IsVideo));
        Assert.Equal("Alone Again", page.Songs[0].Title);
        Assert.Equal("JH398xAYpZA", page.Songs[0].Id);
        // Rows carry no artist column; the strapline fills the byline.
        Assert.All(page.Songs, s => Assert.Equal("The Weeknd", s.ArtistLine));
        Assert.All(page.Songs, s => Assert.Equal(AlbumId, s.Album!.Id));
    }

    [Fact]
    public void FooterShelvesAreNotTracks()
    {
        var page = Pages.Album(Fixtures.Load("album.json"), AlbumId);
        Assert.NotNull(page);
        // 14 tracks in, no "Other versions" rows leaked in as songs.
        Assert.Equal(14, page!.Songs.Length);
        Assert.All(page.Songs, s => Assert.Equal(AlbumId, s.Album!.Id));
    }

    [Fact]
    public void WithSongVersionsSwapsVideosForTheAudioPlaylistTracks()
    {
        var album = Pages.Album(Fixtures.Load("album.json"), AlbumId)!;
        var audio = Pages.PlaylistSongs(Fixtures.Load("album-audio.json"));
        Assert.Equal(14, audio.Length);

        var songs = Pages.WithSongVersions(album.Songs, audio);

        Assert.Equal(14, songs.Length);
        // "Alone Again": the album row is the video JH398xAYpZA; the audio playlist's track is i8r4zJC1i70.
        Assert.Equal("i8r4zJC1i70", songs[0].Id);
        Assert.False(songs[0].IsVideo);
        Assert.Equal("Alone Again", songs[0].Title);
        // The matched tracks are unique: no audio song serves two album rows.
        Assert.Equal(14, songs.Select(s => s.Id).Distinct().Count());
        Assert.DoesNotContain(songs, s => s.Id == "JH398xAYpZA");
    }

    [Fact]
    public void WithSongVersionsLeavesPlainTracksAlone()
    {
        var album = new[]
        {
            new Song("AAA", "Song One", "x", [], null, null, null, false, null),
        };
        var songs = Pages.WithSongVersions(album, []);
        Assert.Equal("AAA", songs[0].Id);
        Assert.True(songs[0].IsVideo == false);
    }
}
