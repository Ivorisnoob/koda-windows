using System.Net;
using System.Text.Json.Nodes;

namespace Koda.Core.InnerTube;

/// <summary>
/// WEB_REMIX calls on music.youtube.com. Ported from the KodaCore
/// InnerTube.swift contract: one visitor token per identity, context built
/// per call, and the same error semantics (non-200 is BadResponse, a body
/// carrying "error" is Network).
/// </summary>
public sealed class InnerTubeClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ClientProfiles _clients;
    private readonly VisitorIdentity _identity;
    private readonly RateGate _gate;
    private bool _disposed;

    /// <param name="http">The session shared with the identity and the resolver, as the Swift
    /// port shares one URLSession; a private one when null.</param>
    public InnerTubeClient(ClientProfiles clients, VisitorIdentity identity, RateGate? gate = null, HttpClient? http = null)
    {
        _clients = clients;
        _identity = identity;
        _gate = gate ?? new RateGate();
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 6,
        }) { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>
    /// One music.youtube.com youtubei call. The visitor token rides both the
    /// context and the header; browse calls that must not wait on a mint use
    /// the cached one first, exactly as the Swift port does.
    /// </summary>
    public async Task<Json> MusicAsync(string endpoint, JsonObject payload, CancellationToken cancellation = default)
    {
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
        request.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", _clients.WebRemixClientHeader);
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", _clients.WebRemixVersion);
        if (visitor is not null) request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);

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

        var status = (int)response.StatusCode;
        if (status != 200)
        {
            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                : null;
            response.Dispose();
            if (_gate.Hit(status, retryAfter)) throw MusicException.RateLimited();
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
        return root;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
    }
}
