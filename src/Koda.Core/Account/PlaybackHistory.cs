using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Koda.Core;

/// <summary>
/// Telling YouTube what was played, so the signed-in account's history and recommendations follow
/// Koda, as the web player does: a signed <c>/player</c> call hands back tracking URLs, and the
/// <c>videostatsPlaybackUrl</c> ping files the play. Koda for Android's PlaybackHistory.kt, which
/// verified it against FEmusic_history (September–October 2026).
/// </summary>
public static class HistoryPing
{
    /// <summary>Where a history ping may carry the account's cookies: the hosts <c>/player</c>
    /// returns tracking URLs on [verified September 2026, signed in].</summary>
    public static readonly HashSet<string> TrackingHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "s.youtube.com", "www.youtube.com",
    };

    /// <summary>The client playback nonce the web player makes per play: 16 URL-safe characters.</summary>
    public static string Cpn()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";
        var chars = new char[16];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        return new string(chars);
    }

    public static JsonObject PlayerPayload(string videoId, string cpn, int signatureTimestamp) => new()
    {
        ["videoId"] = videoId,
        ["cpn"] = cpn,
        ["playbackContext"] = new JsonObject
        {
            ["contentPlaybackContext"] = new JsonObject { ["signatureTimestamp"] = signatureTimestamp },
        },
    };

    /// <summary>The ping URL: https on a YouTube tracking host only, with this play's <c>cpn</c>,
    /// <c>ver</c> and <c>c</c> set (replacing any the URL carries rather than sending them twice).</summary>
    public static Uri? TrackingUrl(string baseUrl, string cpn) => Tracking(baseUrl, new Dictionary<string, string>
    {
        ["cpn"] = cpn,
        ["ver"] = "2",
        ["c"] = "WEB_REMIX",
    });

    private static Uri? Tracking(string baseUrl, IReadOnlyDictionary<string, string> set)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed) || parsed.Scheme != "https"
            || !TrackingHosts.Contains(parsed.Host)) return null;
        var query = new List<string>();
        foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            if (!set.ContainsKey(name)) query.Add(pair);
        }
        foreach (var (name, value) in set.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            query.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        }
        return new UriBuilder(parsed) { Query = string.Join("&", query) }.Uri;
    }
}

/// <summary>
/// The <c>playbackContext.contentPlaybackContext.signatureTimestamp</c> a history <c>/player</c>
/// call must carry. Without it WEB_REMIX answers every video "Video unavailable" with no tracking
/// URLs [verified October 2026, Koda for Android]. It is the player script's build day counted
/// from the epoch, and YouTube takes about the last seven months of them, so it comes in three
/// layers, most exact first:
///
/// 1. The live value from the current player script: <c>iframe_api</c> names the player (1 KB);
///    its <c>base.js</c> holds <c>signatureTimestamp:&lt;n&gt;</c> (~3 MB, read only when the player
///    changes, about weekly). Kept, and the player rechecked at most once a day.
/// 2. Otherwise today's date less a fortnight, never below a kept live value: no network, so
///    history keeps working when YouTube reshapes the script.
/// 3. <see cref="RefreshAfterRejectionAsync"/>: a <c>/player</c> without tracking asks for a fresh
///    read and retries once if the value changed.
/// </summary>
public sealed class PlayerSignatureTimestamp
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly TimeSpan Recheck = Day;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(14);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan ForcedRefreshMinimum = TimeSpan.FromMinutes(10);
    private const int EstimateLagDays = 14;

    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly string? _storePath;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly object _lock = new();
    private int _value;
    private string? _playerId;
    private DateTimeOffset? _checkedAt;
    private DateTimeOffset? _failedAt;

    public PlayerSignatureTimestamp(HttpClient http, string userAgent, string? storePath = null)
    {
        _http = http;
        _userAgent = userAgent;
        _storePath = storePath;
        Load();
    }

    private sealed record Persisted(int Value, string? PlayerId, DateTimeOffset? CheckedAtUtc, DateTimeOffset? FailedAtUtc);

    /// <summary>The value to send now, refreshing the live read when it is due.</summary>
    public async Task<int> CurrentAsync(DateTimeOffset? now = null, CancellationToken cancellation = default)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        DateTimeOffset checkedAt, failedAt;
        lock (_lock)
        {
            checkedAt = _checkedAt ?? DateTimeOffset.MinValue;
            failedAt = _failedAt ?? DateTimeOffset.MinValue;
        }
        if (at - checkedAt >= Recheck && at - failedAt >= FailureBackoff)
        {
            await RefreshAsync(cancellation);
        }
        lock (_lock) return Best(at);
    }

    /// <summary>After a <c>/player</c> came back without tracking: a value worth retrying with, or
    /// null when it would only repeat <paramref name="rejected"/> (a video really unavailable
    /// mustn't cost a second call).</summary>
    public async Task<int?> RefreshAfterRejectionAsync(int rejected, DateTimeOffset? now = null,
        CancellationToken cancellation = default)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        DateTimeOffset latest;
        lock (_lock)
        {
            var checkedAt = _checkedAt ?? DateTimeOffset.MinValue;
            var failedAt = _failedAt ?? DateTimeOffset.MinValue;
            latest = checkedAt > failedAt ? checkedAt : failedAt;
        }
        if (at - latest >= ForcedRefreshMinimum) await RefreshAsync(cancellation);
        int next;
        lock (_lock) next = Best(at);
        if (next != rejected) return next;
        // The live read was what got rejected: the date estimate is the one independent answer left.
        var estimate = Estimate(at);
        return estimate != rejected ? estimate : null;
    }

    private int Best(DateTimeOffset now)
    {
        var fresh = _checkedAt is { } checkedAt && now - checkedAt < StaleAfter;
        return _value > 0 && fresh ? _value : Math.Max(_value, Estimate(now));
    }

    public static int Estimate(DateTimeOffset now) => (int)(now.ToUnixTimeSeconds() / 86400) - EstimateLagDays;

    private async Task RefreshAsync(CancellationToken cancellation)
    {
        await _refreshing.WaitAsync(cancellation);
        try
        {
            await ReadLiveAsync(cancellation);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private async Task ReadLiveAsync(CancellationToken cancellation)
    {
        var iframe = await FetchAsync("https://www.youtube.com/iframe_api", cancellation);
        if (iframe is null || PlayerIdIn(iframe) is not { } playerId)
        {
            Failed();
            return;
        }

        bool unchanged;
        lock (_lock) unchanged = _value > 0 && _playerId == playerId;
        if (unchanged)
        {
            lock (_lock)
            {
                _checkedAt = DateTimeOffset.UtcNow;
                PersistLocked();
            }
            return;
        }

        var script = await FetchAsync($"https://www.youtube.com/s/player/{playerId}/player_ias.vflset/en_US/base.js", cancellation);
        if (script is null || SignatureTimestampIn(script) is not { } value)
        {
            Failed();
            return;
        }
        lock (_lock)
        {
            _value = value;
            _playerId = playerId;
            _checkedAt = DateTimeOffset.UtcNow;
            PersistLocked();
        }
    }

    private void Failed()
    {
        lock (_lock)
        {
            _failedAt = DateTimeOffset.UtcNow;
            PersistLocked();
        }
    }

    private async Task<string?> FetchAsync(string url, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        try
        {
            using var response = await _http.SendAsync(request, cancellation);
            if ((int)response.StatusCode != 200) return null;
            return await response.Content.ReadAsStringAsync(cancellation);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary><c>iframe_api</c> writes the player path with escaped slashes: <c>\/s\/player\/&lt;id&gt;\/</c>.</summary>
    public static string? PlayerIdIn(string text) =>
        FirstGroup(@"\\?/s\\?/player\\?/([A-Za-z0-9_-]{8})\\?/", text);

    public static int? SignatureTimestampIn(string text) =>
        FirstGroup(@"signatureTimestamp[=:]""?(\d{5,})", text) is { } value && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string? FirstGroup(string pattern, string text) =>
        Regex.Match(text, pattern) is { Success: true } match && match.Groups.Count > 1
            ? match.Groups[1].Value
            : null;

    private void Load()
    {
        if (_storePath is null) return;
        try
        {
            if (!File.Exists(_storePath)) return;
            if (JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_storePath), AccountJson.Options) is not { } state) return;
            _value = state.Value;
            _playerId = state.PlayerId;
            _checkedAt = state.CheckedAtUtc;
            _failedAt = state.FailedAtUtc;
        }
        catch (Exception)
        {
            // A damaged store re-reads the live value; history still works from the estimate.
        }
    }

    private void PersistLocked()
    {
        if (_storePath is null) return;
        try
        {
            var directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_storePath, JsonSerializer.Serialize(
                new Persisted(_value, _playerId, _checkedAt, _failedAt), AccountJson.Options));
        }
        catch (Exception)
        {
            // Persistence is best-effort; the value works in memory.
        }
    }
}

/// <summary>
/// When a play counts for the account's history: once per occurrence, after 15 seconds heard
/// (half the song for one shorter than 30), as Koda for Android waits 15 seconds. Skips and a
/// song restored but never played don't count.
/// </summary>
public sealed class HeardThreshold
{
    private Guid? _occurrence;
    private double _heard;
    private bool _reported;

    /// <summary>Add listening to <paramref name="occurrence"/>; true exactly once, when it
    /// crosses the threshold.</summary>
    public bool Add(double seconds, Guid occurrence, double? duration)
    {
        if (_occurrence != occurrence)
        {
            _occurrence = occurrence;
            _heard = 0;
            _reported = false;
        }
        if (_reported || seconds <= 0) return false;
        _heard += seconds;
        var needed = Math.Min(15, Math.Max(1, (duration ?? 30) / 2));
        if (_heard < needed) return false;
        _reported = true;
        return true;
    }
}
