using System.Text.Json.Nodes;
using Koda.Core.InnerTube;
using Koda.Core.Parsing;

namespace Koda.Core;

/// <summary>
/// YouTube Music, signed out: search, pages, radio and streams.
/// Ported from the KodaCore YouTubeMusic facade. Shelves (home, explore,
/// charts, moods), the artist page, suggestions, the Library/Taste store,
/// the audio cache and the Android backup importer are later-phase work;
/// this phase proves the client layer, search, track parsing and streams.
/// </summary>
public sealed partial class YouTubeMusic : IDisposable
{
    private readonly InnerTubeClient _api;
    private readonly StreamResolver _streams;
    private readonly PageCache _pages;
    private readonly VisitorIdentity _identity;
    private readonly RateGate _gate;
    private readonly HttpClient _http;
    private readonly AccountSessions _accounts;
    private readonly PlayerSignatureTimestamp _signatures;
    private bool _disposed;

    public YouTubeMusic(ClientProfiles? clients = null, PageCache? pages = null, RateGate? gate = null,
        HttpClient? http = null, VisitorIdentity? identity = null, string? visitorStorePath = null,
        AccountSessions? accounts = null, string? signatureStorePath = null)
    {
        Clients = clients ?? ClientProfiles.Default;
        _http = http ?? NewSessionHttp();
        _identity = identity ?? new VisitorIdentity(Clients, _http, visitorStorePath);
        _identityOwned = identity is null;
        _gate = gate ?? new RateGate();
        _accounts = accounts ?? new AccountSessions();
        _api = new InnerTubeClient(Clients, _identity, _gate, _http, _accounts);
        _streams = new StreamResolver(Clients, _identity, _http);
        _pages = pages ?? new PageCache();
        _signatures = new PlayerSignatureTimestamp(_http, Clients.BrowserUserAgent, signatureStorePath);
    }

    public ClientProfiles Clients { get; }

    /// <summary>The session requests are signed with; the app signs in and out here.</summary>
    public AccountSessions Accounts => _accounts;

    /// <summary>What history <c>/player</c> calls carry; see PlaybackHistory.cs.</summary>
    internal PlayerSignatureTimestamp Signatures => _signatures;

    /// <summary>The 429 hold shared by every call of this instance; fan-out work checks
    /// <see cref="RateGate.IsHeld"/>, the shell subscribes <see cref="RateGate.Throttled"/>.</summary>
    public RateGate Gate => _gate;

    public StreamResolver Streams => _streams;

    private readonly bool _identityOwned;

    /// <summary>The request budget the shell shares: 20s, 6 connections, nothing cached by HTTP.</summary>
    public static HttpClient NewSessionHttp() => new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        MaxConnectionsPerServer = 6,
        UseCookies = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>A cached page, kept per session: signed in, pages carry the account's state
    /// (saved, subscribed), so one session's are never shown to another.</summary>
    private Task<T> Page<T>(string key, Func<Task<T>> load) =>
        _pages.Value(_accounts.CacheScope + "|" + key, load);

    // MARK: Browse

    public async Task<ShelfPage> HomeAsync(string? @params = null, CancellationToken cancellation = default)
    {
        var payload = new JsonObject { ["browseId"] = "FEmusic_home" };
        if (@params is not null) payload["params"] = @params;
        return Pages.Shelves(await _api.MusicAsync("browse", payload, cancellation));
    }

    public Task<ShelfPage> ExploreAsync(CancellationToken cancellation = default) =>
        Page("explore", async () =>
            Pages.Shelves(await _api.MusicAsync("browse", new JsonObject { ["browseId"] = "FEmusic_explore" }, cancellation)));

    /// <summary>YouTube Music's charts for a country by its two-letter code ("ZZ" is global),
    /// or for the region YouTube places the listener in when null.</summary>
    public Task<ShelfPage> ChartsAsync(string? country = null, CancellationToken cancellation = default) =>
        Page($"charts|{country}", async () =>
        {
            var payload = new JsonObject { ["browseId"] = "FEmusic_charts" };
            if (country is not null) payload["formData"] = new JsonObject { ["selectedValues"] = new JsonArray(country) };
            return Pages.Shelves(await _api.MusicAsync("browse", payload, cancellation));
        });

    /// <summary>One row of chart playlists, simplest first: the global charts, then the
    /// listener's region, then its genre or language charts. Artist charts are left out.</summary>
    public async Task<PlaylistSummary[]> ChartPlaylistsAsync(CancellationToken cancellation = default)
    {
        async Task<ShelfPage?> Try(string? country)
        {
            try
            {
                return await ChartsAsync(country, cancellation);
            }
            catch (MusicException)
            {
                return null;
            }
        }

        var pages = (await Task.WhenAll(Try("ZZ"), Try(null))).OfType<ShelfPage>().ToArray();
        if (pages.Length == 0) throw MusicException.Network("no charts");
        var seen = new HashSet<string>();
        return [.. pages.SelectMany(p => p.Shelves).SelectMany(s => s.Items)
            .OfType<MusicItem.PlaylistRecord>().Select(p => p.Value).Where(p => seen.Add(p.Id))];
    }

    public Task<ShelfPage> MoodAsync(Mood mood, CancellationToken cancellation = default) =>
        Page($"mood|{mood.BrowseId}|{mood.Params}", async () =>
        {
            var payload = new JsonObject { ["browseId"] = mood.BrowseId };
            if (mood.Params is not null) payload["params"] = mood.Params;
            return Pages.Shelves(await _api.MusicAsync("browse", payload, cancellation));
        });

    public Task<ArtistPage> ArtistAsync(string id, CancellationToken cancellation = default) =>
        Page($"artist|{id}", async () =>
            Pages.Artist(await _api.MusicAsync("browse", new JsonObject { ["browseId"] = id }, cancellation), id)
                ?? throw MusicException.BadResponse(200));

    // MARK: Search

    public async Task<string[]> SuggestionsAsync(string query, CancellationToken cancellation = default)
    {
        var root = await _api.MusicAsync("music/get_search_suggestions", new JsonObject { ["input"] = query }, cancellation);
        return [.. root.Objects("searchSuggestionRenderer").Select(s => s["suggestion"].Text()).Where(s => s.Length > 0)];
    }

    public async Task<SearchResults> SearchAsync(string query, SearchFilter filter = SearchFilter.All,
        CancellationToken cancellation = default)
    {
        var payload = new JsonObject { ["query"] = query };
        if (filter.Params() is { } p) payload["params"] = p;
        return Pages.Search(await _api.MusicAsync("search", payload, cancellation));
    }

    // MARK: Pages

    public Task<AlbumPage> AlbumAsync(string id, CancellationToken cancellation = default) =>
        Page($"album|{id}", async () => await FetchAlbumAsync(id, cancellation));

    private async Task<AlbumPage> FetchAlbumAsync(string id, CancellationToken cancellation)
    {
        var root = await _api.MusicAsync("browse", new JsonObject { ["browseId"] = id }, cancellation);
        var page = Pages.Album(root, id)
            ?? throw MusicException.BadResponse(200);
        // Signed out, tracks that have a music video are listed as the video.
        if (Array.Exists(page.Songs, s => s.IsVideo) && page.AudioPlaylistId is { } audioId)
        {
            try
            {
                var audio = await PlaylistSongsAsync("VL" + audioId, cancellation: cancellation);
                page = page with { Songs = Pages.WithSongVersions(page.Songs, audio) };
            }
            catch (MusicException)
            {
                // An audio playlist that refuses to load keeps the honest video ids.
            }
        }
        return page;
    }

    public Task<PlaylistPage> PlaylistAsync(string id, CancellationToken cancellation = default) =>
        Page($"playlist|{id}", async () => await FetchPlaylistAsync(id, cancellation));

    private async Task<PlaylistPage> FetchPlaylistAsync(string id, CancellationToken cancellation)
    {
        var browseId = id.StartsWith("VL") ? id : "VL" + id;
        var root = await _api.MusicAsync("browse", new JsonObject { ["browseId"] = browseId }, cancellation);
        return Pages.Playlist(root, id.StartsWith("VL") ? id[2..] : id)
            ?? throw MusicException.BadResponse(200);
    }

    /// <summary>The next page of a playlist's tracks.</summary>
    public async Task<QueuePage> PlaylistMoreAsync(string continuation, CancellationToken cancellation = default)
    {
        var root = await _api.MusicAsync("browse",
            new JsonObject { ["continuation"] = continuation }, cancellation);
        var next = Pages.PlaylistContinuation(root);
        var songs = Pages.PlaylistSongs(root);
        return new QueuePage(songs, next == continuation ? null : next);
    }

    /// <summary>Every track of a playlist, following continuations.</summary>
    public async Task<Song[]> PlaylistSongsAsync(string browseId, int limit = 1000, CancellationToken cancellation = default)
    {
        var root = await _api.MusicAsync("browse", new JsonObject { ["browseId"] = browseId }, cancellation);
        var songs = new List<Song>(Pages.PlaylistSongs(root));
        var token = Pages.PlaylistContinuation(root);
        var seen = new HashSet<string>();
        while (token is { } current && songs.Count < limit && seen.Add(current))
        {
            var page = await PlaylistMoreAsync(current, cancellation);
            songs.AddRange(page.Songs);
            token = page.Continuation;
        }
        return [.. songs];
    }

    // MARK: Radio

    /// <summary>The radio that follows <paramref name="videoId"/>, the seed itself first.</summary>
    public async Task<QueuePage> RadioAsync(string videoId, CancellationToken cancellation = default) =>
        Pages.Queue(await _api.MusicAsync("next", new JsonObject
        {
            ["videoId"] = videoId,
            ["playlistId"] = "RDAMVM" + videoId,
            ["isAudioOnly"] = true,
            ["tunerSettingValue"] = "AUTOMIX_SETTING_NORMAL",
        }, cancellation));

    public async Task<QueuePage> RadioMoreAsync(string videoId, string continuation,
        CancellationToken cancellation = default) =>
        Pages.Queue(await _api.MusicAsync("next", new JsonObject
        {
            ["videoId"] = videoId,
            ["playlistId"] = "RDAMVM" + videoId,
            ["isAudioOnly"] = true,
            ["continuation"] = continuation,
        }, cancellation));

    /// <summary>A playlist's own play queue (RDCLAK… mixes, artist mixes).</summary>
    public async Task<Song[]> WatchPlaylistAsync(string playlistId, string? videoId = null, string? @params = null,
        CancellationToken cancellation = default)
    {
        var payload = new JsonObject { ["playlistId"] = playlistId, ["isAudioOnly"] = true };
        if (videoId is not null) payload["videoId"] = videoId;
        if (@params is not null) payload["params"] = @params;
        return Pages.Queue(await _api.MusicAsync("next", payload, cancellation)).Songs;
    }

    // MARK: Streams

    public Task<ResolvedStream> StreamAsync(string videoId, CancellationToken cancellation = default) =>
        _streams.ResolveAsync(videoId, cancellation);

    /// <summary>Forget a stream googlevideo refused mid-play, so the next resolve starts fresh.</summary>
    public Task StreamFailedAsync(string videoId, CancellationToken cancellation = default) =>
        _streams.InvalidateAsync(videoId, refusedIdentity: true, cancellation);

    /// <summary>Forget fetched pages, for a pull to refresh that should really fetch again.</summary>
    public void ClearPageCache() => _pages.RemoveAll();

    /// <summary>A new anonymous identity, for a switch to or from an account. The token is the bot
    /// check's identity; one account's carried into another's requests is how a session gets flagged
    /// LOGIN_REQUIRED (Koda for Android drops it on every switch for that reason).</summary>
    public async Task RenewVisitorIdentityAsync(CancellationToken cancellation = default)
    {
        var current = await _identity.CachedAsync();
        await _identity.ReplaceAsync(current, cancellation);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _api.Dispose();
        _streams.Dispose();
        if (_identityOwned) _identity.Dispose();
        _http.Dispose();
    }
}
