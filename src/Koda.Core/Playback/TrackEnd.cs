namespace Koda.Core.Playback;

/// <summary>
/// Tells a track's real end from a stream that gave up in the middle of it.
///
/// A player that runs out of samples says "ended" either way: the song finished,
/// or the bytes stopped coming (a refused range, a stall, a dropped connection).
/// Only the place in the song tells the two apart, and the difference matters -
/// the first moves the queue on, the second must not, or the listener loses the
/// song they were in and another one starts from its first second.
/// </summary>
public static class TrackEnd
{
    /// <summary>How close to the end a player may still be and still count as ended.
    /// The resolved stream and the song's own length can differ by a second or two.</summary>
    public const double ToleranceSeconds = 2.0;

    /// <summary>
    /// Whether the player stopped while the song was still going: an end that came
    /// too early to be the song's end.
    /// </summary>
    /// <param name="positionSeconds">Where the player stopped.</param>
    /// <param name="durationSeconds">How long the song is; zero when it is not known.</param>
    public static bool LooksEarly(double positionSeconds, double durationSeconds)
    {
        // The length is unknown: the player is the only witness, so believe it.
        if (durationSeconds <= 0)
        {
            return false;
        }

        // Nothing was played: that is the start of a song, not a song cut short.
        if (positionSeconds <= 0.5)
        {
            return false;
        }

        // Still a long way from the end: the source gave up, the track did not end.
        return positionSeconds < durationSeconds - ToleranceSeconds;
    }
}
