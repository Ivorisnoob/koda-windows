using Koda.Core.Playback;

namespace Koda.Core.Tests;

/// <summary>
/// The rule that keeps a broken stream from being read as the end of a song:
/// the difference between "the track finished" and "the bytes stopped coming".
/// </summary>
public class TrackEndTests
{
    [Fact]
    public void TheRealEndIsNotEarly()
    {
        // The player sits at the last second of a 262-second song.
        Assert.False(TrackEnd.LooksEarly(261.4, 262.6));
    }

    [Fact]
    public void AStreamThatDiedMidSongIsEarly()
    {
        // Forty seconds into a 262-second song: that is not an ending.
        Assert.True(TrackEnd.LooksEarly(40, 262.6));
    }

    [Fact]
    public void AnUnknownLengthIsNeverEarly()
    {
        // No length to compare against: the player is the only witness.
        Assert.False(TrackEnd.LooksEarly(40, 0));
    }

    [Fact]
    public void APlayerThatReadsZeroAtTheEndIsStillBelieved()
    {
        // Some players report the position as zero once the source is closed.
        // That must not be read as a song cut short, or the song would loop.
        Assert.False(TrackEnd.LooksEarly(0, 262.6));
    }

    [Fact]
    public void TheToleranceCoversTheTwoLengthsDisagreeing()
    {
        // The resolved stream and the song's own length differ by a second or two.
        Assert.False(TrackEnd.LooksEarly(260.9, 262.6));
        Assert.True(TrackEnd.LooksEarly(259.5, 262.6));
    }
}
