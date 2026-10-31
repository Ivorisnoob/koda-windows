using System.Net;
using System.Text.Json.Nodes;
using Koda.Core.Parsing;

namespace Koda.Core.InnerTube;

/// <summary>Whether a call carries the active account: public pages are signed when there is one,
/// account pages and writes need one.</summary>
public enum Signing
{
    IfSignedIn,
    Required,
}

/// <summary>
/// WEB_REMIX calls on music.youtube.com. Ported from the KodaCore
/// InnerTube.swift contract: one visitor token per identity, context built
/// per call, and the same error semantics (non-200 is BadResponse, a body
/// carrying "error" is Network).
///
/// When an <see cref="AccountSessions"/> is supplied, each call is signed with the active session's
/// cookies, rotated <c>Set-Cookie</c> values are folded back into that session, and YouTube's
/// <c>logged_in</c> verdict is recorded. HTTP 200 proves nothing about a session: a dead one reads
/// public pages fine.
/// </summary>
public sealed class InnerTubeClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ClientProfiles _clients;
    private readonly VisitorIdentity _identity;
    private readonly RateGate _gate;
    private readonly AccountSessions? _accounts;
    private bool _disposed;

    /// <param name="http">The session shared with the identity and the resolver, as the Swift
    /// port shares one URLSession; a private one when null.</param>
    public InnerTubeClient(ClientProfiles clients, VisitorIdentity identity, RateGate? gate = null,
        HttpClient? http = null, AccountSessions? accounts = null)
    {
        _clients = clients;
        _identity = identity;
        _gate = gate ?? new RateGate();
        _accounts = accounts;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 6,
            UseCookies = false,
        }) { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>
    /// One music.youtube.com youtubei call. The visitor token rides both the
    /// context and the header; browse calls that must not wait on a mint use
    /// the cached one first, exactly as the Swift port does.
    /// </summary>
    public async Task<Json> MusicAsync(string endpoint, JsonObject payload, CancellationToken cancellation = default,
        Signing signing = Signing.IfSignedIn)
    {
        var ticket = _accounts?.Ticket();
        if (signing == Signing.Required && ticket?.Cookies.Sapisid is null) throw MusicException.SignedOut();

        var visitor = await _identity.CachedAsync();
        visitor ??= await _identity.CurrentAsync(cancellation);

        var client = new JsonObject
        {
            ["clientName"] = _clients.WebRemixName,
            ["clientVersion"] = _clients.WebRemixVersion,
            ["hl"] = "en",
            ["gl"] = "US",
        };
        if (visitor is not null) client["visitorData"] = visitor;
        payload["context"] = new JsonObject { ["client"] = client };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://music.youtube.com/youtubei/v1/{endpoint}?prettyPrint=false");
        request.Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", _clients.BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Origin", SapisidHash.MusicOrigin);
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", _clients.WebRemixClientHeader);
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", _clients.WebRemixVersion);
        if (visitor is not null) request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
        if (ticket is not null)
        {
            foreach (var (field, value) in SapisidHash.Headers(ticket.Cookies))
            {
                request.Headers.TryAddWithoutValidation(field, value);
            }
        }

        // No local wait here: the listener asked for this call (see RateGate).
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellation);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw MusicException.Network("timeout");
        }
        catch (Exception e) when (e is HttpRequestException or System.IO.IOException)
        {
            throw MusicException.Network(e.Message);
        }

        if (ticket is not null) _accounts?.Received(SetCookies(response), ticket);

        var status = (int)response.StatusCode;
        if (status != 200)
        {
            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                : null;
            response.Dispose();
            if (_gate.Hit(status, retryAfter)) throw MusicException.RateLimited();
            // A write under a session YouTube no longer honours answers 401 UNAUTHENTICATED.
            if (status == 401 && (ticket is not null || signing == Signing.Required)) throw MusicException.SignedOut();
            throw MusicException.BadResponse(status);
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellation);
        }
        finally
        {
            response.Dispose();
        }

        Json root;
        try
        {
            root = Json.Parse(body);
        }
        catch (Exception)
        {
            throw MusicException.Network("unreadable body");
        }
        if (root["error"].Exists) throw MusicException.Network("body carries an error");

        if (ticket is not null && AccountPages.SignedIn(root) is { } signedIn)
        {
            _accounts?.Note(signedIn, ticket);
            if (!signedIn && signing == Signing.Required) throw MusicException.SignedOut();
        }
        return root;
    }

    /// <summary>
    /// One www.youtube.com youtubei call, as the WEB client: video feeds, search, watch pages,
    /// channels and writes. Signed in, it carries the account and is signed for www.youtube.com,
    /// so Home is the account's own.
    /// </summary>
    public async Task<Json> WebAsync(string endpoint, JsonObject payload, CancellationToken cancellation = default,
        Signing signing = Signing.IfSignedIn)
    {
        var ticket = _accounts?.Ticket();
        if (signing == Signing.Required && ticket?.Cookies.Sapisid is null) throw MusicException.SignedOut();

        var visitor = await _identity.CachedAsync();
        visitor ??= await _identity.CurrentAsync(cancellation);

        var client = new JsonObject
        {
            ["clientName"] = "WEB",
            ["clientVersion"] = _clients.WebVersion,
            ["hl"] = "en",
            ["gl"] = "US",
        };
        if (visitor is not null) client["visitorData"] = visitor;
        payload["context"] = new JsonObject { ["client"] = client };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://www.youtube.com/youtubei/v1/{endpoint}?prettyPrint=false");
        request.Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", _clients.BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Origin", WebOrigin);
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", _clients.WebVersion);
        if (visitor is not null) request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
        if (ticket is not null)
        {
            foreach (var (field, value) in SapisidHash.Headers(ticket.Cookies, WebOrigin))
            {
                request.Headers.TryAddWithoutValidation(field, value);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellation);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw MusicException.Network("timeout");
        }
        catch (Exception e) when (e is HttpRequestException or System.IO.IOException)
        {
            throw MusicException.Network(e.Message);
        }

        if (ticket is not null) _accounts?.Received(SetCookies(response), ticket);

        var status = (int)response.StatusCode;
        if (status != 200)
        {
            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                : null;
            response.Dispose();
            if (_gate.Hit(status, retryAfter)) throw MusicException.RateLimited();
            if (status == 401 && (ticket is not null || signing == Signing.Required)) throw MusicException.SignedOut();
            throw MusicException.BadResponse(status);
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellation);
        }
        finally
        {
            response.Dispose();
        }

        Json root;
        try
        {
            root = Json.Parse(body);
        }
        catch (Exception)
        {
            throw MusicException.Network("unreadable body");
        }
        if (root["error"].Exists) throw MusicException.Network("body carries an error");

        if (ticket is not null && AccountPages.SignedIn(root) is { } signedIn)
        {
            _accounts?.Note(signedIn, ticket);
            if (!signedIn && signing == Signing.Required) throw MusicException.SignedOut();
        }
        return root;
    }

    /// <summary>www.youtube.com, the origin a WEB request is signed for.</summary>
    public const string WebOrigin = "https://www.youtube.com";

    /// <summary>
    /// A signed GET to one of YouTube's tracking URLs (history pings), as the active account.
    /// Only <see cref="HistoryPing.TrackingUrl"/> builds these, so the cookies go to YouTube's
    /// hosts alone.
    /// </summary>
    public async Task PingAsync(Uri url, string referer, string origin = SapisidHash.MusicOrigin,
        CancellationToken cancellation = default)
    {
        var ticket = _accounts?.Ticket();
        if (ticket?.Cookies.Sapisid is null) throw MusicException.SignedOut();

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _clients.BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Referer", referer);
        foreach (var (field, value) in SapisidHash.Headers(ticket.Cookies, origin))
        {
            request.Headers.TryAddWithoutValidation(field, value);
        }

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

        using (response)
        {
            _accounts?.Received(SetCookies(response), ticket);
            var status = (int)response.StatusCode;
            if (status == 429)
            {
                var retryAfter = response.Headers.RetryAfter is { } header
                    ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                    : null;
                _gate.Hit(status, retryAfter);
                throw MusicException.RateLimited();
            }
            if (status is < 200 or >= 300) throw MusicException.BadResponse(status);
        }
    }

    private static string[] SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values] : [];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
    }
}
