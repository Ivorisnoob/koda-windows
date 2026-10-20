using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Koda.Core.InnerTube;

/// <summary>
/// Koda's anonymous identity towards YouTube: the visitorData token.
///
/// YouTube's bot check keys on it. A /player call without one is refused,
/// and about one fresh token in ten is refused mid-stream: googlevideo serves
/// the opening ~1 MB and answers 403 past it. So there is one token per
/// install, kept for six hours, persisted across launches, and replaced the
/// moment it is refused. [verified October 2026 on the iOS sibling]
///
/// The token persists under <see cref="DataPaths"/>, where the Swift port
/// used a UserDefaults suite.
/// </summary>
public sealed class VisitorIdentity : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly ClientProfiles _clients;
    private readonly string _storePath;
    /// <summary>Serializes mints only; never taken by a read.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private readonly bool _ownsHttp;
    private string? _token;
    private DateTimeOffset? _mintedAt;
    private string? _vetted;
    private bool _disposed;

    public VisitorIdentity(ClientProfiles clients, HttpClient? http = null, string? storePath = null)
    {
        _clients = clients;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        _storePath = storePath ?? Path.Combine(DataPaths.EngineDirectory, "visitor.json");
        Load();
    }

    private sealed record Persisted(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("mintedAtUtc")] DateTimeOffset? MintedAtUtc,
        [property: JsonPropertyName("vetted")] string? Vetted);

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var state = System.Text.Json.JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_storePath));
            if (state is null) return;
            _token = state.Token;
            _mintedAt = state.MintedAtUtc;
            _vetted = state.Vetted;
        }
        catch (Exception)
        {
            // A corrupt store must not break the app; it re-mints.
            _token = null;
            _mintedAt = null;
            _vetted = null;
        }
    }

    private void Persist()
    {
        try
        {
            // Under the state lock: a mint and a vetting mark can persist at the same moment.
            lock (_state)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
                File.WriteAllText(_storePath, System.Text.Json.JsonSerializer.Serialize(
                    new Persisted(_token, _mintedAt, _vetted)));
            }
        }
        catch (Exception)
        {
            // Persistence is best-effort; an unwritable store still works in memory.
        }
    }

    // [scar] The first port took the mint semaphore (synchronously, with Wait()) in
    // Cached/IsVetted/MarkVetted while Current held it across the network mint, so the
    // read that exists to "not wait on a mint" blocked a thread for up to 8 s. The
    // Swift actor is reentrant across await: reads answer at once during a mint.
    // Fields live under _state (never held across an await); _gate only serializes mints.

    /// <summary>The token without minting, for browse calls that must not wait on a mint.</summary>
    public Task<string?> CachedAsync()
    {
        lock (_state) return Task.FromResult(_token);
    }

    /// <summary>The token to send, minting one when there is none or it has aged out.
    /// Null only when a mint failed and nothing was ever stored.</summary>
    public async Task<string?> CurrentAsync(CancellationToken cancellation = default)
    {
        if (FreshToken() is { } fresh) return fresh;
        await _gate.WaitAsync(cancellation);
        try
        {
            // Another caller may have minted while this one waited.
            if (FreshToken() is { } minted) return minted;
            var token = await MintAsync(cancellation);
            lock (_state) return token ?? _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? FreshToken()
    {
        lock (_state)
        {
            var now = DateTimeOffset.UtcNow;
            return _token is not null && _mintedAt is { } minted && now >= minted && now - minted < Lifetime
                ? _token
                : null;
        }
    }

    /// <summary>Drop <paramref name="refused"/> and mint a replacement. A caller that
    /// waited while another replaced the same token gets that replacement instead of a second mint.</summary>
    public async Task<string?> ReplaceAsync(string? refused, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            bool hadToken;
            lock (_state)
            {
                if (_token is not null && _token != refused) return _token;
                hadToken = _token is not null;
                _token = null;
                _mintedAt = null;
            }
            var minted = await MintAsync(cancellation);
            if (minted is null && hadToken) Persist(); // don't resurrect a refused token on the next launch
            return minted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether googlevideo has been seen serving a whole stream under this token.</summary>
    public bool IsVetted(string candidate)
    {
        lock (_state) return _vetted == candidate;
    }

    public void MarkVetted(string candidate)
    {
        lock (_state) _vetted = candidate;
        Persist();
    }

    /// <summary>The caller holds the mint gate; single flight falls out of that.</summary>
    private async Task<string?> MintAsync(CancellationToken cancellation)
    {
        try
        {
            var body = new JsonObject
            {
                ["context"] = new JsonObject
                {
                    ["client"] = new JsonObject
                    {
                        ["clientName"] = "WEB",
                        ["clientVersion"] = _clients.WebVersion,
                        ["hl"] = "en",
                        ["gl"] = "US",
                    },
                },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://www.youtube.com/youtubei/v1/visitor_id?prettyPrint=false");
            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", _clients.BrowserUserAgent);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellation);
            }
            catch (Exception e) when (e is HttpRequestException or System.IO.IOException or OperationCanceledException
                { })
            {
                return null;
            }

            string? raw = null;
            if ((int)response.StatusCode == 200)
            {
                try
                {
                    var json = Json.Parse(await response.Content.ReadAsStringAsync(cancellation));
                    raw = json["responseContext"]["visitorData"].NonEmpty;
                }
                catch (Exception)
                {
                    raw = null;
                }
            }
            response.Dispose();
            if (raw is null) return null;

            // The response URL-encodes the base64 padding; /player wants it raw.
            var token = raw.Replace("%3D", "=").Replace("%3d", "=");
            lock (_state)
            {
                _token = token;
                _mintedAt = DateTimeOffset.UtcNow;
            }
            Persist();
            return token;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
        _gate.Dispose();
    }
}
