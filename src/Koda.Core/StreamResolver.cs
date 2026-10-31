using System.Net;
using System.Text.Json.Nodes;
using Koda.Core.InnerTube;
using Koda.Core.Video;

namespace Koda.Core;

/// <summary>
/// Turns a video id into a playable AAC URL.
///
/// One visionOS /player call under Koda's visitorData. Two refusals look
/// alike from here and need the same answer, one replacement of the token:
/// /player saying LOGIN_REQUIRED, and googlevideo serving the opening of the
/// file then 403 past it, which a single last-byte request catches before the
/// player is handed a URL that would stall a minute in. A replacement that is
/// refused too means the verdict is on the network, so it is not repeated.
/// Ported from the KodaCore StreamResolver.swift contract.
/// [verified October 2026: AAC itags 139/140 with URLs, no cipher]
/// </summary>
public sealed class StreamResolver : IDisposable
{
    private readonly HttpClient _http;
    private readonly VisitorIdentity _identity;
    private readonly ClientProfiles _clients;
    private readonly Dictionary<string, ResolvedStream> _cache = [];
    private readonly Dictionary<string, Task<ResolvedStream>> _inFlight = [];
    private readonly Dictionary<string, VideoStream> _videoCache = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _ownsHttp;
    private bool _disposed;

    public StreamResolver(ClientProfiles clients, VisitorIdentity identity, HttpClient? http = null)
    {
        _clients = clients;
        _identity = identity;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 6,
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>The best AAC/M4A audio-only format of the original soundtrack, from a recorded or live player response.</summary>
    /// <param name="userAgent">The resolving client's User-Agent; the default profile's VisionOS one when null.</param>
    public static ResolvedStream? PickAudio(Json root, string videoId, string? userAgent = null)
    {
        var formats = root["streamingData"]["adaptiveFormats"].Array
            .Where(f => (f["mimeType"].String ?? "").StartsWith("audio/mp4") && f["url"].NonEmpty is not null)
            .ToArray();
        // A dubbed video carries several audio tracks; keep the default one.
        var originals = formats.Where(f => f["audioTrack"]["audioIsDefault"].Bool == true).ToArray();
        var candidates = originals.Length > 0 ? originals : formats.Where(f => !f["audioTrack"].Exists).ToArray();
        var pool = candidates.Length > 0 ? candidates : formats;
        var best = pool.Length > 0 ? pool.MaxBy(f => f["bitrate"].Int ?? 0) : Json.Null;
        if (!best.Exists) return null;
        var url = best["url"].String is { } s && Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri : null;
        if (url is null) return null;

        DateTimeOffset? expiresAt = null;
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = pair.Split('=', 2)[0];
            if (key == "expire" && double.TryParse(pair.Split('=', 2).Skip(1).FirstOrDefault(),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                expiresAt = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
                break;
            }
        }

        var durationMs = best["approxDurationMs"].Double;
        return new ResolvedStream(
            videoId,
            url,
            userAgent ?? ClientProfiles.Default.VisionOs.UserAgent,
            best["itag"].Int ?? 0,
            best["contentLength"].String is { } len && long.TryParse(len, out var l) ? l : null,
            durationMs is { } ms ? ms / 1000d : root["videoDetails"]["lengthSeconds"].Double,
            best["loudnessDb"].Double,
            expiresAt);
    }

    private sealed record AttemptResult(Attempt Outcome, ResolvedStream? Stream);

    private enum Attempt
    {
        Stream,
        Refused,
    }

    /// <summary>A resolved stream, cached while it stays fresh; callers after the same video share one fetch.</summary>
    public async Task<ResolvedStream> ResolveAsync(string videoId, CancellationToken cancellation = default)
    {
        Task<ResolvedStream> task;
        await _gate.WaitAsync(cancellation);
        try
        {
            if (_cache.TryGetValue(videoId, out var cached) && cached.IsFresh()) return cached;
            if (_inFlight.TryGetValue(videoId, out var running))
            {
                task = running;
            }
            else
            {
                // Shared by every caller after this video, so it must not die with the first
                // caller's token (the Swift Task {} is unstructured for the same reason); each
                // caller stops waiting on its own token below.
                task = Task.Run(() => FetchAsync(videoId, CancellationToken.None), CancellationToken.None);
                _inFlight[videoId] = task;
            }
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            var stream = await task.WaitAsync(cancellation);
            await _gate.WaitAsync(cancellation);
            try
            {
                _cache[videoId] = stream;
                if (_cache.Count > 200) TrimCacheLocked();
            }
            finally
            {
                _gate.Release();
            }
            return stream;
        }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                if (_inFlight.TryGetValue(videoId, out var registered) && registered == task)
                    _inFlight.Remove(videoId);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// A video's stream from the same <c>/player</c> call, under the same bot check and so
    /// the same one replacement of a refused token. Cached until shortly before it expires.
    /// </summary>
    public async Task<VideoStream> ResolveVideoAsync(string videoId, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            if (_videoCache.TryGetValue(videoId, out var cached) && cached.IsFresh()) return cached;
        }
        finally
        {
            _gate.Release();
        }

        var token = await _identity.CurrentAsync(cancellation)
            ?? throw MusicException.Network("no visitor identity");
        var stream = await VideoAttemptAsync(videoId, token, cancellation);
        if (stream is null)
        {
            var fresh = await _identity.ReplaceAsync(token, cancellation);
            if (fresh is null || fresh == token) throw MusicException.BotCheck();
            stream = await VideoAttemptAsync(videoId, fresh, cancellation) ?? throw MusicException.BotCheck();
        }

        await _gate.WaitAsync(cancellation);
        try
        {
            _videoCache[videoId] = stream;
            if (_videoCache.Count > 100)
            {
                foreach (var (key, old) in _videoCache.Where(kvp => !kvp.Value.IsFresh()).ToList())
                    _videoCache.Remove(key);
            }
        }
        finally
        {
            _gate.Release();
        }
        return stream;
    }

    /// <summary>Null for the bot check's signatures: LOGIN_REQUIRED, OK with nothing to stream, and a last byte refused.</summary>
    private async Task<VideoStream?> VideoAttemptAsync(string videoId, string token, CancellationToken cancellation)
    {
        var root = await PlayerAsync(videoId, token, cancellation);
        var status = root["playabilityStatus"]["status"].String ?? "";
        if (status == "LOGIN_REQUIRED") return null;
        if (!string.IsNullOrEmpty(status) && status != "OK")
            throw MusicException.Unplayable(root["playabilityStatus"]["reason"].NonEmpty);
        var data = root["streamingData"];
        if (!data.Exists) return null;

        var expiresAt = data["expiresInSeconds"].Double is { } seconds
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : (DateTimeOffset?)null;
        var userAgent = _clients.VisionOs.UserAgent;

        // Live has no indexed formats to describe, only the HLS master Windows refuses.
        if (root["videoDetails"]["isLive"].Bool == true)
            throw MusicException.Unplayable("Live videos are not supported yet");

        if (DashManifest.Build(root) is not { } dash) throw MusicException.Unplayable("No playable stream");

        // A new token is vetted on the audio, as a song's is: refused, it would play a few
        // seconds of picture and then stall.
        if (!_identity.IsVetted(token) && PickAudio(root, videoId, userAgent) is { } audio)
        {
            using var probeBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            probeBudget.CancelAfter(TimeSpan.FromSeconds(6));
            var lastByte = await ServesLastByteAsync(audio, probeBudget.Token);
            if (lastByte == false) return null;
            if (lastByte == true) _identity.MarkVetted(token);
        }
        return new VideoStream(videoId, dash.Manifest, dash.Qualities, userAgent, expiresAt);
    }

    /// <summary>Forget a stream googlevideo refused mid-play, so the next resolve starts fresh.</summary>
    public async Task InvalidateAsync(string videoId, bool refusedIdentity, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            _cache.Remove(videoId);
            _videoCache.Remove(videoId);
        }
        finally
        {
            _gate.Release();
        }
        // Outside the resolver's lock: a mint is a network round trip, and every
        // resolve would queue behind it.
        if (refusedIdentity && await _identity.CachedAsync() is { } token)
            await _identity.ReplaceAsync(token, cancellation);
    }

    private void TrimCacheLocked()
    {
        foreach (var (key, stream) in _cache.Where(kvp => !kvp.Value.IsFresh()).ToList())
            _cache.Remove(key);
    }

    private async Task<ResolvedStream> FetchAsync(string videoId, CancellationToken cancellation)
    {
        var token = await _identity.CurrentAsync(cancellation)
            ?? throw MusicException.Network("no visitor identity");
        var first = await AttemptAsync(videoId, token, cancellation);
        if (first.Outcome == Attempt.Stream) return first.Stream!;
        var fresh = await _identity.ReplaceAsync(token, cancellation);
        if (fresh is null || fresh == token) throw MusicException.BotCheck();
        var second = await AttemptAsync(videoId, fresh, cancellation);
        return second.Outcome == Attempt.Stream
            ? second.Stream!
            : throw MusicException.BotCheck();
    }

    private async Task<AttemptResult> AttemptAsync(string videoId, string token, CancellationToken cancellation)
    {
        var root = await PlayerAsync(videoId, token, cancellation);
        var status = root["playabilityStatus"]["status"].String ?? "";
        if (status == "LOGIN_REQUIRED") return new AttemptResult(Attempt.Refused, null);
        if (!string.IsNullOrEmpty(status) && status != "OK")
            throw MusicException.Unplayable(root["playabilityStatus"]["reason"].NonEmpty);
        // OK without streamingData is the bot check's other signature.
        if (!root["streamingData"].Exists) return new AttemptResult(Attempt.Refused, null);
        var stream = PickAudio(root, videoId, _clients.VisionOs.UserAgent)
            ?? throw MusicException.Unplayable("No playable audio");
        if (_identity.IsVetted(token)) return new AttemptResult(Attempt.Stream, stream);
        // A 6 s budget as the Swift probe has: a slow answer proves nothing and must not hold up play.
        using var probeBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        probeBudget.CancelAfter(TimeSpan.FromSeconds(6));
        var lastByte = await ServesLastByteAsync(stream, probeBudget.Token);
        if (lastByte == false) return new AttemptResult(Attempt.Refused, null);
        if (lastByte == true) _identity.MarkVetted(token);
        return new AttemptResult(Attempt.Stream, stream);
    }

    private async Task<Json> PlayerAsync(string videoId, string token, CancellationToken cancellation)
    {
        var client = new JsonObject
        {
            ["clientName"] = _clients.VisionOs.Name,
            ["clientVersion"] = _clients.VisionOs.Version,
            ["hl"] = "en",
            ["gl"] = "US",
            ["utcOffsetMinutes"] = 0,
            ["visitorData"] = token,
        };
        foreach (var (key, value) in _clients.VisionOs.Fields) client[key] = value;
        var body = new JsonObject
        {
            ["videoId"] = videoId,
            ["context"] = new JsonObject { ["client"] = client },
            ["contentPlaybackNonce"] = Nonce(16),
            ["t"] = Nonce(12),
            ["contentCheckOk"] = true,
            ["racyCheckOk"] = true,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://youtubei.googleapis.com/youtubei/v1/player?key={_clients.ApiKey}&prettyPrint=false");
        request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", _clients.VisionOs.UserAgent);
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Format-Version", "2");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", _clients.VisionOs.HeaderId);
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", _clients.VisionOs.Version);
        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellation);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw MusicException.Network("timeout");
        }
        catch (Exception e) when (e is HttpRequestException or System.IO.IOException)
        {
            throw MusicException.Network(e.Message);
        }
        var code = (int)response.StatusCode;
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(cancellation);
        }
        finally
        {
            response.Dispose();
        }
        if (code != 200) throw MusicException.BadResponse(code);
        try
        {
            return Json.Parse(text);
        }
        catch (Exception)
        {
            throw MusicException.Network("unreadable body");
        }
    }

    /// <summary>
    /// The last byte is refused under a walled token and served under a good
    /// one. Null when the answer would prove nothing: no length, or a clip
    /// short enough to fit inside the window googlevideo serves either way.
    /// </summary>
    private async Task<bool?> ServesLastByteAsync(ResolvedStream stream, CancellationToken cancellation)
    {
        if (stream.ContentLength is not { } length || length <= 0 || (stream.Duration ?? 0) < 120)
            return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
        request.Headers.TryAddWithoutValidation("User-Agent", stream.UserAgent);
        request.Headers.TryAddWithoutValidation("Range", $"bytes={length - 1}-{length - 1}");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellation);
        }
        catch (Exception)
        {
            return null;
        }
        var status = (int)response.StatusCode;
        response.Dispose();
        return status switch
        {
            206 => true,
            403 => false,
            _ => null,
        };
    }

    private static string Nonce(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        return new string(chars);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
        _gate.Dispose();
    }
}
