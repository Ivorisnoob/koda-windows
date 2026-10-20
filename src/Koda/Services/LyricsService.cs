using Koda.Core;

namespace Koda.Services;

/// <summary>
/// The lyrics of a song, from one place: the panel asks for them, and the player asks
/// for them as soon as a song starts, so the words are usually already there by the time
/// anyone opens the panel. The client keeps what it found this session, so the second
/// ask is instant.
/// </summary>
public static class LyricsService
{
    private static readonly LyricsClient Client = new();

    /// <summary>The lyrics of a song, or null when no service has them. The services the
    /// listener turned off are not asked.</summary>
    public static Task<Lyrics?> For(Song song) => Client.LyricsFor(song, Settings.Current.LyricsSources);

    /// <summary>Ask for the lyrics before anyone wants them.</summary>
    public static void Prefetch(Song song)
    {
        _ = For(song);
    }

    /// <summary>Forget the lyrics found this session, so the next look asks the services again.</summary>
    public static void Forget() => LyricsClient.ClearCache();
}
