using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Koda.Core;

/// <summary>A service lyrics can come from.</summary>
public enum LyricsSource
{
    /// <summary>Word-by-word, with duets and backing vocals.</summary>
    YouLyPlus,
    /// <summary>Word-by-word, from several catalogues.</summary>
    BetterLyrics,
    /// <summary>Line-by-line and plain lyrics, community-made.</summary>
    Lrclib,
}

/// <summary>
/// Lyrics from several services at once, keeping the best: word-timed from
/// YouLyPlus or BetterLyrics (Apple Music's timings), else line-timed or plain
/// from LRCLIB.
///
/// The services answer at different speeds and come and go. All of them are
/// asked together; a word-timed answer is taken as soon as every service ranked
/// above it has answered, and otherwise the best answer by the deadline.
/// Ported from the KodaCore LyricsClient.swift contract.
/// </summary>
public sealed class LyricsClient : IDisposable
{
    private static readonly string[] YouLyPlusMirrors =
    [
        "https://lyricsplus.binimum.org/",
        "https://lyricsplus.prjktla.my.id/",
        "https://lyricsplus.prjktla.workers.dev/",
        "https://lyricsplus.atomix.one/",
        "https://lyricsplus-seven.vercel.app/",
    ];

    private static readonly string[] BetterLyricsEndpoints =
    [
        "https://lyrics-api.boidu.dev/getLyrics",
        "https://lyrics-api.boidu.dev/qq/getLyrics",
        "https://lyrics-api.boidu.dev/kugou/getLyrics",
    ];

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(9);

    /// <summary>Lyrics already found this session, so reopening the panel or replaying a song is instant.</summary>
    private static readonly ConcurrentDictionary<string, Lyrics> Cache = new(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <param name="http">The session to use; a private one when null.</param>
    public LyricsClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 6,
        }) { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>Lyrics for <paramref name="song"/> from the given services, ranked in the order given.</summary>
    public async Task<Lyrics?> LyricsFor(
        Song song,
        IReadOnlyList<LyricsSource>? sources = null,
        CancellationToken cancellation = default)
    {
        var wanted = sources ?? [LyricsSource.YouLyPlus, LyricsSource.BetterLyrics, LyricsSource.Lrclib];
        if (wanted.Count == 0)
        {
            return null;
        }

        var key = song.Id + "|" + string.Join(",", wanted);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var found = await Best(song, wanted, cancellation).ConfigureAwait(false);
        if (found is not null)
        {
            Cache[key] = found;
            if (Cache.Count > 60)
            {
                // The oldest answer goes; the cache is a convenience, not a store.
                foreach (var entry in Cache.Take(Cache.Count - 60))
                {
                    Cache.TryRemove(entry.Key, out _);
                }
            }
        }

        return found;
    }

    /// <summary>Forget lyrics found this session, so the next look asks the services again.</summary>
    public static void ClearCache() => Cache.Clear();

    // MARK: Choosing

    private async Task<Lyrics?> Best(Song song, IReadOnlyList<LyricsSource> sources, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(Deadline);

        var running = new List<Task<(int Rank, Lyrics? Lyrics)>>(sources.Count);
        for (var rank = 0; rank < sources.Count; rank++)
        {
            var source = sources[rank];
            running.Add(Ask(rank, source, song, deadline.Token));
        }

        var answers = new Dictionary<int, Lyrics>();
        var pending = new HashSet<int>(Enumerable.Range(0, sources.Count));
        try
        {
            while (pending.Count > 0)
            {
                var waiting = running.Where(task => !task.IsCompleted).ToList();
                if (waiting.Count == 0)
                {
                    break;
                }

                var finished = await Task.WhenAny(waiting).WaitAsync(deadline.Token).ConfigureAwait(false);
                var (rank, lyrics) = await finished.ConfigureAwait(false);
                pending.Remove(rank);
                if (lyrics is not null)
                {
                    answers[rank] = lyrics;
                }

                // Word timing from the best-ranked service still in the running wins outright.
                var top = answers
                    .Where(answer => answer.Value.Quality == 2)
                    .OrderBy(answer => answer.Key)
                    .FirstOrDefault();
                if (top.Value is not null && !pending.Any(rank => rank < top.Key))
                {
                    return top.Value;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The deadline passed: the best answer so far is the answer.
        }

        var best = answers
            .OrderByDescending(answer => answer.Value.Quality)
            .ThenBy(answer => answer.Key)
            .FirstOrDefault();
        return best.Value;
    }

    private async Task<(int Rank, Lyrics? Lyrics)> Ask(int rank, LyricsSource source, Song song, CancellationToken cancellation)
    {
        Lyrics? lyrics = source switch
        {
            LyricsSource.YouLyPlus => await YouLyPlus(song, cancellation).ConfigureAwait(false),
            LyricsSource.BetterLyrics => await BetterLyrics(song, cancellation).ConfigureAwait(false),
            LyricsSource.Lrclib => await Lrclib(song, cancellation).ConfigureAwait(false),
            _ => null,
        };

        return (rank, lyrics);
    }

    // MARK: YouLyPlus

    private async Task<Lyrics?> YouLyPlus(Song song, CancellationToken cancellation)
    {
        var artist = song.ArtistLine;
        if (artist.Length == 0)
        {
            return null;
        }

        var parameters = new Dictionary<string, string>
        {
            ["title"] = song.Title,
            ["artist"] = artist,
        };
        if (song.Album is { } album && album.Title.Length > 0)
        {
            parameters["album"] = album.Title;
        }

        if (song.Duration is { } duration && duration > 0)
        {
            parameters["duration"] = ((int)Math.Round(duration)).ToString();
        }

        var found = await Race(
            YouLyPlusMirrors.Select(mirror => mirror + "v2/lyrics/get").ToList(),
            parameters,
            data =>
            {
                var lines = LyricsFormats.ParseYouLyPlus(Json.Parse(Encoding.UTF8.GetString(data)));
                return lines is null ? null : Lyrics.Synced(lines);
            },
            cancellation).ConfigureAwait(false);
        if (found is not null)
        {
            return found;
        }

        return await Race(
            YouLyPlusMirrors.Select(mirror => mirror + "v1/ttml/get").ToList(),
            parameters,
            DecodeWrapped,
            cancellation).ConfigureAwait(false);
    }

    /// <summary>The first mirror to answer with lyrics; the others are cancelled.</summary>
    private async Task<Lyrics?> Race(
        IReadOnlyList<string> urls,
        IReadOnlyDictionary<string, string> parameters,
        Func<byte[], Lyrics?> decode,
        CancellationToken cancellation)
    {
        using var winner = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var tasks = urls.Select(async url =>
        {
            var data = await Fetch(url, parameters, winner.Token).ConfigureAwait(false);
            return data is null ? null : decode(data);
        }).ToList();

        var completed = await Task.WhenAny(tasks).WaitAsync(winner.Token).ConfigureAwait(false);
        var result = await completed.ConfigureAwait(false);
        if (result is not null)
        {
            winner.Cancel();
            return result;
        }

        // The first answer was nothing; wait for the rest.
        foreach (var task in tasks)
        {
            var lyrics = await task.ConfigureAwait(false);
            if (lyrics is not null)
            {
                winner.Cancel();
                return lyrics;
            }
        }

        return null;
    }

    // MARK: BetterLyrics

    private async Task<Lyrics?> BetterLyrics(Song song, CancellationToken cancellation)
    {
        var artist = song.ArtistLine;
        if (artist.Length == 0)
        {
            return null;
        }

        var parameters = new Dictionary<string, string>
        {
            ["s"] = song.Title,
            ["a"] = artist,
        };
        if (song.Album is { } album && album.Title.Length > 0)
        {
            parameters["al"] = album.Title;
        }

        if (song.Duration is { } duration && duration > 0)
        {
            parameters["d"] = ((int)Math.Round(duration)).ToString();
        }

        foreach (var endpoint in BetterLyricsEndpoints)
        {
            cancellation.ThrowIfCancellationRequested();
            var data = await Fetch(endpoint, parameters, cancellation).ConfigureAwait(false);
            if (data is not null && DecodeWrapped(data) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>TTML or LRC as the body, or inside a JSON object under a key such as <c>ttml</c> or <c>lyrics</c>.</summary>
    internal static Lyrics? DecodeWrapped(byte[] data)
    {
        var body = Encoding.UTF8.GetString(data).Trim();
        if (body.Length == 0)
        {
            return null;
        }

        if (body.StartsWith('<'))
        {
            return LyricsFormats.ParseAny(body);
        }

        Json json;
        try
        {
            json = Json.Parse(body);
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var key in new[] { "ttml", "lyrics", "lrc", "content", "text" })
        {
            if (json[key].NonEmpty is { } text && LyricsFormats.ParseAny(text) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // MARK: LRCLIB

    private async Task<Lyrics?> Lrclib(Song song, CancellationToken cancellation)
    {
        var exact = new Dictionary<string, string>
        {
            ["artist_name"] = song.ArtistLine,
            ["track_name"] = song.Title,
        };
        if (song.Album is { } album)
        {
            exact["album_name"] = album.Title;
        }

        if (song.Duration is { } duration)
        {
            exact["duration"] = ((int)Math.Round(duration)).ToString();
        }

        if (await Fetch("https://lrclib.net/api/get", exact, cancellation).ConfigureAwait(false) is { } data &&
            LyricsOf(Json.Parse(Encoding.UTF8.GetString(data))) is { } exactLyrics)
        {
            return exactLyrics;
        }

        var search = new Dictionary<string, string>
        {
            ["artist_name"] = song.ArtistLine,
            ["track_name"] = song.Title,
        };
        if (await Fetch("https://lrclib.net/api/search", search, cancellation).ConfigureAwait(false) is not { } searchData)
        {
            return null;
        }

        Json found;
        try
        {
            found = Json.Parse(Encoding.UTF8.GetString(searchData));
        }
        catch (Exception)
        {
            return null;
        }

        // A different cut of the song (live, extended) is timed for a different recording.
        var candidates = found.Array
            .Where(item => song.Duration is not { } wanted || item["duration"].Double is not { } other ||
                Math.Abs(other - wanted) <= 10)
            .ToList();
        var best = candidates.FirstOrDefault(item => item["syncedLyrics"].NonEmpty is not null);
        if (best.Raw.ValueKind == System.Text.Json.JsonValueKind.Undefined && candidates.Count > 0)
        {
            best = candidates[0];
        }

        return best.Raw.ValueKind == System.Text.Json.JsonValueKind.Undefined ? null : LyricsOf(best);
    }

    internal static Lyrics? LyricsOf(Json item)
    {
        if (item["syncedLyrics"].NonEmpty is { } synced)
        {
            var lines = Lyrics.ParseLrc(synced);
            if (lines.Count > 0)
            {
                return Lyrics.Synced(lines);
            }
        }

        if (item["plainLyrics"].NonEmpty is { } plain)
        {
            return Lyrics.Unsynced(plain);
        }

        return null;
    }

    // MARK: Network

    private async Task<byte[]?> Fetch(string url, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellation)
    {
        var query = string.Join("&", parameters
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        var separator = url.Contains('?') ? '&' : '?';
        var full = query.Length == 0 ? url : $"{url}{separator}{query}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, full);
            request.Headers.TryAddWithoutValidation("User-Agent", "Koda for Windows (https://github.com/Ivorisnoob/Koda-Windows)");
            request.Headers.TryAddWithoutValidation("Accept", "application/json, application/xml, text/plain, */*");
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
            return data.Length == 0 ? null : data;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}

