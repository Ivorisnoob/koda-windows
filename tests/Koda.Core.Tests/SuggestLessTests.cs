namespace Koda.Core.Tests;

/// <summary>
/// Suggest less: a song, or an artist, kept out of what Koda offers, kept with the library and
/// carried by a backup.
/// </summary>
public class SuggestLessTests
{
    private static Song Song(string id, string line, params ArtistRef[] artists) =>
        new(id, $"Song {id}", line, artists, null, 180, null, false, null);

    [Fact]
    public void AHiddenSongAndAHiddenArtistAreLeftOutOfWhatIsOffered()
    {
        var library = new Library(null);
        var artist = new ArtistRef("UCartist", "The Band");
        var byArtist = Song("aaa", "The Band", artist);
        var hidden = Song("bbb", "Someone");
        var kept = Song("ccc", "Someone Else", new ArtistRef("UCother", "Other"));

        library.Hide(hidden);
        library.Hide(artist);

        Assert.True(library.IsUnwanted(byArtist));
        Assert.True(library.IsUnwanted(hidden));
        Assert.False(library.IsUnwanted(kept));
        Assert.Equal([kept], library.Offerable([byArtist, hidden, kept]));
    }

    [Fact]
    public void AnArtistIsMatchedByChannelOrByNameWhenUnlinked()
    {
        var library = new Library(null);
        library.Hide(new ArtistRef(null, "Café Tacvba"));

        // No channel on either side: the names match, ignoring case and accents.
        Assert.True(library.IsUnwanted(Song("a", "cafe tacvba")));
        // A byline YouTube didn't split is matched whole.
        Assert.True(library.IsUnwanted(Song("b", "Café Tacvba")));
        Assert.False(library.IsUnwanted(Song("c", "Café Tacvba & Friends")));
    }

    [Fact]
    public void SuggestingAgainBringsThemBack()
    {
        var library = new Library(null);
        var artist = new ArtistRef("UC1", "A");
        var song = Song("x", "A", artist);
        library.Hide(song);
        library.Hide(artist);

        library.Unhide(song);
        library.Unhide(new ArtistRef("UC1", "renamed"));

        Assert.False(library.IsUnwanted(song));
        Assert.True(library.NotInterested.IsEmpty);
    }

    [Fact]
    public void ABackupCarriesWhatWasHiddenAndAnOlderOneHidesNothing()
    {
        var library = new Library(null);
        var song = Song("x", "A");
        library.Hide(song);
        var backup = library.BackupData();

        var other = new Library(null);
        other.Restore(backup);
        Assert.True(other.IsUnwanted(song));

        // A backup from before Suggest less: nothing is hidden, and nothing fails.
        var text = System.Text.Encoding.UTF8.GetString(backup)
            .Replace("\"notInterested\"", "\"ignoredByOlderVersions\"");
        var older = new Library(null);
        older.Restore(System.Text.Encoding.UTF8.GetBytes(text));
        Assert.False(older.IsUnwanted(song));
        Assert.True(older.NotInterested.IsEmpty);
    }

    [Fact]
    public void HidingTheSameSongTwiceKeepsOneEntry()
    {
        var library = new Library(null);
        var song = Song("x", "A");

        library.Hide(song);
        library.Hide(song);

        Assert.Single(library.NotInterested.Songs);
    }
}
