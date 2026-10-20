using Koda.Core;

namespace Koda.Core.Tests;

/// <summary>
/// The Android import: what a Koda for Android backup gives the Windows app.
/// The fixture is a recorded backup zip - the shape the phone writes.
/// </summary>
public class AndroidBackupTests
{
    private static AndroidBackup Read() => AndroidBackup.Read(Fixtures.Bytes("android-backup.zip"));

    [Fact]
    public void LikedSongsComeFromTheProfileInUse()
    {
        var backup = Read();

        // The profile holds three songs; one is a local file, which has no YouTube id.
        Assert.Equal(2, backup.Liked.Length);
        Assert.Contains(backup.Liked, s => s.Id == "dQw4w9WgXcQ");
        Assert.Contains(backup.Liked, s => s.Id == "J7p4bzqLvCw");
    }

    [Fact]
    public void PlaysComeBackInTimeOrder()
    {
        var backup = Read();

        // Four entries: one is a local file, one has no usable id.
        Assert.Equal(3, backup.Plays.Length);
        Assert.True(backup.Plays[0].Date <= backup.Plays[1].Date);
        Assert.True(backup.Plays[1].Date <= backup.Plays[2].Date);
    }

    [Fact]
    public void SearchesAreSplitOnThePipe()
    {
        var backup = Read();

        Assert.Equal(["daft punk", "the weeknd"], backup.Searches);
    }

    [Fact]
    public void PlaylistsMadeOnThePhoneComeAcross()
    {
        var backup = Read();

        var playlist = Assert.Single(backup.Playlists);
        Assert.Equal("Gym", playlist.Name);
        Assert.Single(playlist.Songs);
    }

    [Fact]
    public void SavedAlbumsAndPlaylistsAreKeptApart()
    {
        var backup = Read();

        // An MPREb id is an album; anything else was saved as a playlist.
        Assert.Single(backup.Albums);
        Assert.Equal("Discovery", backup.Albums[0].Title);
        Assert.Single(backup.SavedPlaylists);
        Assert.Equal("Road Trip", backup.SavedPlaylists[0].Title);
    }

    [Fact]
    public void ANewerBackupIsRefusedWithItsOwnReason()
    {
        // The manifest version decides: rebuild the zip with one past what this build knows.
        var rebuilt = RebuildWithManifest(Fixtures.Bytes("android-backup.zip"), "\"formatVersion\": 2", "\"formatVersion\": 99");

        var error = Assert.Throws<AndroidBackup.ReadException>(() => AndroidBackup.Read(rebuilt));

        Assert.True(error.NewerVersion);
    }

    [Fact]
    public void SomethingThatIsNotABackupIsRefused()
    {
        Assert.Throws<AndroidBackup.ReadException>(() => AndroidBackup.Read([1, 2, 3]));
    }

    /// <summary>The same zip with one file's text swapped.</summary>
    private static byte[] RebuildWithManifest(byte[] data, string find, string replace)
    {
        using var source = new System.IO.Compression.ZipArchive(new MemoryStream(data), System.IO.Compression.ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var target = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var copy = target.CreateEntry(entry.FullName);
                using var writer = new StreamWriter(copy.Open());
                using var reader = new StreamReader(entry.Open());
                writer.Write(reader.ReadToEnd().Replace(find, replace));
            }
        }

        return output.ToArray();
    }
}
