namespace Koda.Core;

/// <summary>
/// What the listener plays most lately, from the library's counts: the seeds for Your Mix.
/// Each song scores its plays and listening time, weighed by how recently it was last
/// played: a song last played two weeks ago counts half as much as one from today. Liked
/// songs get a bonus. Artists score the sum of their songs. Ported from Taste.swift.
/// </summary>
public sealed record Taste(string[] Artists, Song[] Seeds)
{
    private static readonly double HalfLifeSeconds = 14 * 86_400;

    public bool IsEmpty => Seeds.Length == 0;

    public static Taste From(Library library, DateTimeOffset? now = null, int maxSeeds = 4)
    {
        var at = now ?? DateTimeOffset.Now;
        var liked = library.Liked.Select(s => s.Id).ToHashSet();
        var scored = new List<(SongStats Stats, double Score)>();
        foreach (var stats in library.SongStats.Values)
        {
            var age = Math.Max(0, (at - stats.LastPlayed).TotalSeconds);
            var decay = Math.Pow(0.5, age / HalfLifeSeconds);
            var bonus = liked.Contains(stats.Song.Id) ? 2 : 0;
            var score = (stats.Plays + stats.Seconds / 180 + bonus) * decay;
            if (score > 0) scored.Add((stats, score));
        }
        scored.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : string.CompareOrdinal(a.Stats.Song.Id, b.Stats.Song.Id));

        var artistScores = new Dictionary<string, double>();
        foreach (var entry in scored)
        {
            var lead = LeadArtist(entry.Stats.Song);
            artistScores[lead] = artistScores.GetValueOrDefault(lead) + entry.Score;
        }
        var artists = artistScores
            .OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key).Where(k => k.Length > 0).ToArray();

        var seeds = new List<Song>();
        foreach (var artist in artists)
        {
            if (seeds.Count >= maxSeeds) break;
            if (scored.FirstOrDefault(e => LeadArtist(e.Stats.Song) == artist) is { Stats: { } best }) seeds.Add(best.Song);
        }
        return new Taste(artists, [.. seeds]);
    }

    public static string LeadArtist(Song song) => song.Artists.FirstOrDefault()?.Name ?? song.ArtistLine;

    /// <summary>Radios woven together a song at a time, so no one artist's radio takes over;
    /// songs heard in more than one radio, and the seeds themselves, appear once.</summary>
    public static Song[] Weave(IReadOnlyList<Song[]> radios, IEnumerable<string>? excluding = null, int limit = 50)
    {
        var seen = new HashSet<string>(excluding ?? []);
        var output = new List<Song>();
        var longest = radios.Count == 0 ? 0 : radios.Max(r => r.Length);
        for (var index = 0; index < longest; index++)
        {
            foreach (var radio in radios)
            {
                if (index >= radio.Length) continue;
                if (seen.Add(radio[index].Id)) output.Add(radio[index]);
                if (output.Count == limit) return [.. output];
            }
        }
        return [.. output];
    }
}
