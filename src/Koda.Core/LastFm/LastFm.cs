using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Koda.Core.LastFm;

/// <summary>
/// Scrobbling to Last.fm, as the Mac and the iPhone do it.
///
/// Off until the listener turns it on, and it never asks for a password: each listener
/// registers their own Last.fm API account and Koda signs in with Last.fm's desktop flow
/// (a token approved in the browser, then exchanged for a session). Credentials live in a
/// <see cref="ISecretStore"/> on this machine only; the queue and the cached profile in a file
/// that a library backup never carries, so restoring Koda elsewhere never turns scrobbling on
/// for another account.
///
/// A play is queued once it qualifies and sent one at a time, each removed only after Last.fm
/// acknowledges it, so nothing is lost offline or when the app is closed. Pausing listening
/// history pauses scrobbling too: nothing heard while paused is ever sent.
///
/// Ported from KodaCore LastFM.swift. The Swift class is @MainActor; this one takes a lock for
/// its state and runs one call at a time, raising <see cref="Changed"/> off the caller's thread.
/// </summary>
public sealed class LastFm : IDisposable
{
    /// <summary>Plays waiting at most this many; beyond it new ones are dropped and the listener told.</summary>
    public const int QueueLimit = 1000;

    /// <summary>How long Koda waits after a network failure or rate limit before trying again.</summary>
    internal TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class Credentials
    {
        public string ApiKey { get; set; } = "";
        public string Secret { get; set; } = "";
        public string Session { get; set; } = "";
        public string User { get; set; } = "";
        public string Token { get; set; } = "";
        public DateTimeOffset? TokenDate { get; set; }
    }

    private sealed class Stored
    {
        public bool Enabled { get; set; }
        public List<LastFmPlay> Queue { get; set; } = [];
        public LastFmProfile? Profile { get; set; }
        public List<LastFmTrack> Recent { get; set; } = [];
        public DateTimeOffset? RefreshedAt { get; set; }
        public DateTimeOffset? LastSent { get; set; }
    }

    private readonly object _lock = new();
    private readonly string _stateFile;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly LastFmListeningClock _clock = new();
    private Credentials _credentials = new();
    private List<LastFmPlay> _queue = [];
    private bool _enabled;
    private bool _paused;
    private bool _busy;
    private LastFmProfile? _profile;
    private List<LastFmTrack> _recent = [];
    private DateTimeOffset? _refreshedAt;
    private DateTimeOffset? _lastSent;
    private LastFmException? _error;
    private string? _notice;
    /// <summary>Bumped whenever what Koda may do changes (off, paused, another account), so a call
    /// started before then never writes its result.</summary>
    private int _generation;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    /// <summary>Anything the settings show changed.</summary>
    public event Action? Changed;

    /// <param name="handler">The transport; tests pass a recorded one.</param>
    public LastFm(string directory, ISecretStore secrets, HttpMessageHandler? handler = null)
    {
        _stateFile = Path.Combine(directory, "lastfm.json");
        _secrets = secrets;
        _http = new HttpClient(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Koda/1.0 (https://github.com/Ivorisnoob/Koda-Windows)");
        Load();
    }

    // MARK: State the settings read

    public bool IsEnabled { get { lock (_lock) return _enabled; } }
    /// <summary>A session exists: plays can be sent.</summary>
    public bool IsConnected { get { lock (_lock) return _credentials.Session.Length > 0; } }
    public string User { get { lock (_lock) return _credentials.User; } }
    /// <summary>The API key saved on this machine, to prefill the form.</summary>
    public string ApiKey { get { lock (_lock) return _credentials.ApiKey; } }
    public bool HasSecret { get { lock (_lock) return _credentials.Secret.Length > 0; } }
    /// <summary>A token waits for the listener to approve it in the browser.</summary>
    public bool IsSigningIn { get { lock (_lock) return _credentials.Token.Length > 0; } }
    public bool IsBusy { get { lock (_lock) return _busy; } }
    public LastFmPlay[] Queued { get { lock (_lock) return [.. _queue]; } }
    public LastFmProfile? Profile { get { lock (_lock) return _profile; } }
    public LastFmTrack[] Recent { get { lock (_lock) return [.. _recent]; } }
    public DateTimeOffset? RefreshedAt { get { lock (_lock) return _refreshedAt; } }
    public DateTimeOffset? LastSent { get { lock (_lock) return _lastSent; } }
    public LastFmException? Error { get { lock (_lock) return _error; } }
    /// <summary>Last.fm took a play but ignored it, with its reason.</summary>
    public string? Notice { get { lock (_lock) return _notice; } }

    /// <summary>Plays can be recorded and sent right now.</summary>
    public bool CanScrobble { get { lock (_lock) return CanScrobbleLocked; } }

    private bool CanScrobbleLocked => _enabled && _credentials.Session.Length > 0 && !_paused;

    /// <summary>Listening history is paused: nothing is recorded or sent until it isn't.</summary>
    public bool IsPaused
    {
        get { lock (_lock) return _paused; }
        set
        {
            lock (_lock)
            {
                if (_paused == value) return;
                _paused = value;
                if (value) InvalidateLocked();
            }

            if (!value) SyncSoon();
            RaiseChanged();
        }
    }

    // MARK: Settings

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            InvalidateLocked();
            _enabled = enabled;
            _retryAfter = DateTimeOffset.MinValue;
            if (!enabled)
            {
                // Off means off: unsent plays go, and nothing waits half signed in.
                _queue = [];
                _credentials.Token = "";
                _credentials.TokenDate = null;
                SaveCredentialsLocked();
            }

            _error = null;
            SaveLocked();
        }

        RaiseChanged();
        if (enabled) RefreshSoon();
    }

    /// <summary>Forget the account and the API keys on this machine.</summary>
    public void Disconnect()
    {
        lock (_lock)
        {
            InvalidateLocked();
            _credentials = new Credentials();
            _secrets.Write(null);
            _enabled = false;
            _queue = [];
            _profile = null;
            _recent = [];
            _refreshedAt = null;
            _lastSent = null;
            _error = null;
            _notice = null;
            SaveLocked();
        }

        RaiseChanged();
    }

    /// <summary>Step one of signing in: checks the keys, gets a token and returns the page where the
    /// listener approves it.</summary>
    public async Task<Uri> BeginSignInAsync(string apiKey, string secret)
    {
        var key = apiKey.Trim();
        var shared = secret.Trim();
        if (!LastFmProtocol.IsCredential(key) || !LastFmProtocol.IsCredential(shared))
        {
            var invalid = new LastFmException(LastFmException.Reason.InvalidCredentials);
            lock (_lock) _error = invalid;
            RaiseChanged();
            throw invalid;
        }

        if (!IsEnabled) SetEnabled(true);
        lock (_lock)
        {
            InvalidateLocked();
            _credentials.ApiKey = key;
            _credentials.Secret = shared;
            _credentials.Token = "";
            SaveCredentialsLocked();
        }

        return await RunAsync(async () =>
        {
            var response = await RequestAsync("auth.getToken", signed: true).ConfigureAwait(false);
            var token = LastFmProtocol.Text(response["token"]);
            if (token.Length == 0) throw new LastFmException(LastFmException.Reason.UnexpectedResponse);
            lock (_lock)
            {
                _credentials.Token = token;
                _credentials.TokenDate = DateTimeOffset.UtcNow;
                SaveCredentialsLocked();
            }

            return LastFmProtocol.AuthorizationUrl(key, token);
        }).ConfigureAwait(false);
    }

    /// <summary>Sign in again with the keys already saved.</summary>
    public Task<Uri> RestartSignInAsync()
    {
        string key, secret;
        lock (_lock) (key, secret) = (_credentials.ApiKey, _credentials.Secret);
        return BeginSignInAsync(key, secret);
    }

    /// <summary>Step two: exchange the approved token for a session. Another account than before
    /// starts from an empty queue, so no play is ever sent under the wrong name.
    /// <paramref name="quietly"/> is a check made while waiting for the listener: "not approved
    /// yet" is the expected answer then, and is not shown as a problem.</summary>
    public Task FinishSignInAsync(bool quietly = false) => RunAsync(async () =>
    {
        string token;
        DateTimeOffset? tokenDate;
        lock (_lock) (token, tokenDate) = (_credentials.Token, _credentials.TokenDate);
        if (token.Length == 0) throw new LastFmException(LastFmException.Reason.NoPendingSignIn);
        if (tokenDate is { } date && DateTimeOffset.UtcNow - date > TimeSpan.FromHours(1))
        {
            throw new LastFmException(LastFmException.Reason.SignInExpired);
        }

        var response = await RequestAsync("auth.getSession", new() { ["token"] = token }, signed: true).ConfigureAwait(false);
        var name = LastFmProtocol.Text(response["session"]?["name"]);
        var key = LastFmProtocol.Text(response["session"]?["key"]);
        if (name.Length == 0 || key.Length == 0) throw new LastFmException(LastFmException.Reason.UnexpectedResponse);
        lock (_lock)
        {
            if (!string.Equals(name, _credentials.User, StringComparison.OrdinalIgnoreCase))
            {
                _queue = [];
                _profile = null;
                _recent = [];
                _refreshedAt = null;
                _lastSent = null;
            }

            _credentials.Session = key;
            _credentials.User = name;
            _credentials.Token = "";
            _credentials.TokenDate = null;
            SaveCredentialsLocked();
            SaveLocked();
        }

        RaiseChanged();
        await FlushAsync().ConfigureAwait(false);
        await RefreshDataAsync().ConfigureAwait(false);
        return true;
    }, quietly);

    /// <summary>Send what is waiting and fetch the profile and recent plays.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            await RunAsync(async () =>
            {
                await FlushAsync().ConfigureAwait(false);
                await RefreshDataAsync().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (LastFmException)
        {
            // Shown through Error.
        }
    }

    public void DismissNotice()
    {
        lock (_lock)
        {
            _notice = null;
            if (!_busy) _error = null;
        }

        RaiseChanged();
    }

    // MARK: Playback

    /// <summary>A song began playing. <paramref name="occurrence"/> tells one play of a song from the
    /// next; a song coming round again on repeat one is called again, as a new play.</summary>
    public void Started(Guid occurrence, Song song)
    {
        var artist = LastFmPlay.ArtistOf(song);
        lock (_lock)
        {
            if (!CanScrobbleLocked || !IsScrobblable(artist, song.Title, song.IsVideo))
            {
                _clock.Reset();
                return;
            }

            _clock.Start(occurrence, DateTimeOffset.UtcNow);
        }

        var play = new LastFmPlay(artist, song.Title, song.Album?.Title ?? "", song.Duration ?? 0, DateTimeOffset.UtcNow);
        _ = Task.Run(() => NowPlayingAsync(play));
    }

    /// <summary><paramref name="seconds"/> more of a song were heard.</summary>
    public void Heard(double seconds, Guid occurrence, Song song)
    {
        var artist = LastFmPlay.ArtistOf(song);
        var duration = song.Duration ?? 0;
        LastFmPlay play;
        lock (_lock)
        {
            if (!CanScrobbleLocked || !IsScrobblable(artist, song.Title, song.IsVideo)) return;
            if (!_clock.Add(seconds, occurrence, duration, DateTimeOffset.UtcNow) || _clock.StartedAt is not { } startedAt) return;
            play = new LastFmPlay(artist, song.Title, song.Album?.Title ?? "", duration, startedAt);
        }

        Enqueue(play);
    }

    /// <summary>Music with a title and an artist. Videos are left out: their artist is often the
    /// channel that uploaded them, which would file a fan's upload under the wrong name.</summary>
    public static bool IsScrobblable(string artist, string track, bool isVideo) =>
        !isVideo && artist.Trim().Length > 0 && track.Trim().Length > 0;

    private void Enqueue(LastFmPlay play)
    {
        lock (_lock)
        {
            if (_queue.Count >= QueueLimit)
            {
                _notice = $"{QueueLimit} plays are waiting to be sent, so new ones aren't being kept. Sync to send them.";
            }
            else
            {
                _queue.Add(play);
                SaveLocked();
            }
        }

        RaiseChanged();
        SyncSoon();
    }

    /// <summary>Try to send waiting plays, unless a call is running or Last.fm asked Koda to wait.</summary>
    public void SyncSoon()
    {
        lock (_lock)
        {
            if (!CanScrobbleLocked || _queue.Count == 0 || _busy || DateTimeOffset.UtcNow < _retryAfter) return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(async () =>
                {
                    await FlushAsync().ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (LastFmException)
            {
                // Shown through Error; the plays wait.
            }
        });
    }

    private void RefreshSoon()
    {
        lock (_lock)
        {
            if (!_enabled || _credentials.Session.Length == 0 || _paused || _busy) return;
        }

        _ = Task.Run(RefreshAsync);
    }

    // MARK: Calls

    private async Task NowPlayingAsync(LastFmPlay play)
    {
        // Best effort, and never in the way of sign-in or sending the queue.
        lock (_lock)
        {
            if (!CanScrobbleLocked || _busy || DateTimeOffset.UtcNow < _retryAfter) return;
        }

        try
        {
            await RequestAsync("track.updateNowPlaying", play.Parameters(), authenticated: true).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A missed "now playing" costs nothing; the scrobble is what counts.
        }
    }

    /// <summary>Send queued plays oldest first; each leaves the queue only once Last.fm answers for it.</summary>
    private async Task FlushAsync()
    {
        while (true)
        {
            LastFmPlay play;
            lock (_lock)
            {
                if (!CanScrobbleLocked || _queue.Count == 0) return;
                play = _queue[0];
            }

            var parameters = play.Parameters();
            parameters["timestamp"] = play.StartedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var code = LastFmProtocol.ScrobbleCode(await RequestAsync("track.scrobble", parameters, authenticated: true).ConfigureAwait(false));
            if (code == 5)
            {
                lock (_lock) _retryAfter = DateTimeOffset.UtcNow + Cooldown;
                throw new LastFmException(LastFmException.Reason.DailyLimit);
            }

            lock (_lock)
            {
                if (_queue.Count > 0 && _queue[0] == play) _queue.RemoveAt(0);
                _lastSent = DateTimeOffset.UtcNow;
                if (code != 0) _notice = IgnoredReason(code);
                SaveLocked();
            }

            RaiseChanged();
        }
    }

    private static string IgnoredReason(int code) => code switch
    {
        1 => "Last.fm ignored a play: it didn't accept the artist's name.",
        2 => "Last.fm ignored a play: it didn't accept the song's title.",
        3 => "Last.fm ignored a play: its time was too far in the past.",
        4 => "Last.fm ignored a play: its time was in the future.",
        _ => $"Last.fm ignored a play (reason {code}).",
    };

    private async Task RefreshDataAsync()
    {
        string user;
        lock (_lock)
        {
            if (_credentials.Session.Length == 0 || _credentials.User.Length == 0) return;
            user = _credentials.User;
        }

        var info = await RequestAsync("user.getInfo", new() { ["user"] = user }).ConfigureAwait(false);
        var tracks = await RequestAsync("user.getRecentTracks", new() { ["user"] = user, ["limit"] = "30", ["extended"] = "0" }).ConfigureAwait(false);
        lock (_lock)
        {
            _profile = LastFmProtocol.Profile(info) ?? _profile;
            _recent = [.. LastFmProtocol.RecentTracks(tracks)];
            _refreshedAt = DateTimeOffset.UtcNow;
            SaveLocked();
        }

        RaiseChanged();
    }

    /// <summary>Runs one operation at a time, publishing busy and errors, and drops its result if
    /// what Koda may do changed while it ran.</summary>
    private async Task<T> RunAsync<T>(Func<Task<T>> body, bool quietly = false)
    {
        lock (_lock)
        {
            if (!_enabled || _paused) throw new LastFmException(LastFmException.Reason.Paused);
            if (DateTimeOffset.UtcNow < _retryAfter) throw new LastFmException(LastFmException.Reason.RateLimited);
        }

        await _operation.WaitAsync().ConfigureAwait(false);
        int expected;
        lock (_lock)
        {
            expected = _generation;
            _busy = true;
            _error = null;
        }

        RaiseChanged();
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var failure = error as LastFmException ?? new LastFmException(
                error is HttpRequestException or TaskCanceledException
                    ? LastFmException.Reason.Network
                    : LastFmException.Reason.UnexpectedResponse);
            lock (_lock)
            {
                if (expected == _generation && !(quietly && failure.Kind == LastFmException.Reason.NotApprovedYet))
                {
                    if (failure.CoolsDown) _retryAfter = DateTimeOffset.UtcNow + Cooldown;
                    _error = failure;
                }
            }

            throw failure;
        }
        finally
        {
            lock (_lock)
            {
                if (expected == _generation) _busy = false;
            }

            _operation.Release();
            RaiseChanged();
        }
    }

    private async Task<JsonObject> RequestAsync(string method, Dictionary<string, string>? parameters = null,
        bool signed = false, bool authenticated = false)
    {
        int expected;
        var all = parameters is null ? new Dictionary<string, string>() : new Dictionary<string, string>(parameters);
        string secret;
        lock (_lock)
        {
            if (!_enabled || _paused) throw new LastFmException(LastFmException.Reason.Paused);
            expected = _generation;
            all["method"] = method;
            all["api_key"] = _credentials.ApiKey;
            if (authenticated)
            {
                if (_credentials.Session.Length == 0) throw new LastFmException(LastFmException.Reason.NotConnected);
                all["sk"] = _credentials.Session;
            }

            secret = _credentials.Secret;
        }

        if (signed || authenticated) all["api_sig"] = LastFmProtocol.Signature(all, secret);
        all["format"] = "json";

        using var request = authenticated
            ? new HttpRequestMessage(HttpMethod.Post, LastFmProtocol.Endpoint)
            {
                Content = new StringContent(LastFmProtocol.FormEncoded(all), Encoding.UTF8, "application/x-www-form-urlencoded"),
            }
            : new HttpRequestMessage(HttpMethod.Get, new Uri(LastFmProtocol.Endpoint, "?" + LastFmProtocol.FormEncoded(all)));

        string body;
        try
        {
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new LastFmException(LastFmException.Reason.Network);
        }

        // Turned off, paused or signed out while the call was out: its answer no longer applies.
        lock (_lock)
        {
            if (expected != _generation || !_enabled || _paused) throw new OperationCanceledException();
        }

        JsonObject json;
        try
        {
            json = JsonNode.Parse(body) as JsonObject ?? throw new LastFmException(LastFmException.Reason.UnexpectedResponse);
        }
        catch (JsonException)
        {
            throw new LastFmException(LastFmException.Reason.UnexpectedResponse);
        }

        if (json["error"] is JsonValue failure && failure.TryGetValue<int>(out var code))
        {
            if (code == 9)
            {
                lock (_lock)
                {
                    _credentials.Session = "";
                    SaveCredentialsLocked();
                }
            }

            throw LastFmException.FromApi(code, LastFmProtocol.Text(json["message"]));
        }

        return json;
    }

    /// <summary>Stops anything in flight from writing its result.</summary>
    private void InvalidateLocked()
    {
        _generation++;
        _busy = false;
        _clock.Reset();
    }

    // MARK: Storage

    private void Load()
    {
        try
        {
            if (_secrets.Read() is { Length: > 0 } saved &&
                JsonSerializer.Deserialize<Credentials>(saved, Json) is { } credentials)
            {
                _credentials = credentials;
            }
        }
        catch (Exception)
        {
            // Unreadable credentials are no credentials: connect again.
        }

        try
        {
            if (File.Exists(_stateFile) &&
                JsonSerializer.Deserialize<Stored>(File.ReadAllText(_stateFile), Json) is { } stored)
            {
                _enabled = stored.Enabled;
                _queue = stored.Queue ?? [];
                _profile = stored.Profile;
                _recent = stored.Recent ?? [];
                _refreshedAt = stored.RefreshedAt;
                _lastSent = stored.LastSent;
            }
        }
        catch (Exception)
        {
            // A damaged state file starts Last.fm off, with nothing waiting.
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            var temp = _stateFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Stored
            {
                Enabled = _enabled,
                Queue = _queue,
                Profile = _profile,
                Recent = _recent,
                RefreshedAt = _refreshedAt,
                LastSent = _lastSent,
            }, Json));
            File.Move(temp, _stateFile, overwrite: true);
        }
        catch (Exception)
        {
            // The plays stay in memory and are written with the next change.
        }
    }

    private void SaveCredentialsLocked()
    {
        try
        {
            _secrets.Write(JsonSerializer.Serialize(_credentials, Json));
        }
        catch (Exception)
        {
            // Kept for this session; signing in again after a restart puts it back.
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A listener that throws must not stop a scrobble.
        }
    }

    public void Dispose() => _http.Dispose();
}
