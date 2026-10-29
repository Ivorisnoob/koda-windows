using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Koda.Core;

/// <summary>
/// Client for a lossless server the listener points Koda at: it searches the server's
/// catalogue and downloads a track as FLAC. Nothing about the server is built in; the
/// address (with its token, as <c>https://host/a/TOKEN/</c>) and the optional signing
/// secret come from Settings, typed in when lossless downloads are turned on.
/// </summary>
public sealed class LosslessServer : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _secret;

    public LosslessServer(string baseUrl, string? secret)
    {
        _baseUrl = baseUrl.Trim().TrimEnd('/');
        _secret = secret?.Trim() ?? "";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Koda/1.0");
    }

    /// <summary>True when the text is an http or https address Koda can use as a server.</summary>
    public static bool IsValidUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private string Token => ExtractToken(_baseUrl);

    private static string ExtractToken(string url)
    {
        var m = Regex.Match(url, @"/a/([^/]+)");
        return m.Success ? m.Groups[1].Value : "";
    }

    private (string ts, string sign) Sign(string method, string path)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var message = $"{ts}\n{method.ToUpperInvariant()}\n{path}\n{Token}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        var sign = Convert.ToHexString(hash).ToLowerInvariant();
        return (ts, sign);
    }

    /// <summary>A request under the server's address; signed when a secret was given.</summary>
    private async Task<string> RequestAsync(string relativePathAndQuery, string method = "GET", CancellationToken ct = default)
    {
        var url = new Uri(_baseUrl + relativePathAndQuery);
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (_secret.Length > 0)
        {
            // The server's own signing headers: what it checks, so they keep its names.
            var (ts, sign) = Sign(method, url.AbsolutePath);
            req.Headers.Add("X-LW-TS", ts);
            req.Headers.Add("X-LW-Sign", sign);
            req.Headers.Add("X-LW-Intent", "stream");
        }
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Search the server for tracks.</summary>
    public async Task<List<LosslessTrack>> SearchAsync(string query, string quality = "lossless", CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(query);
        var path = $"/search?q={encoded}&quality={quality}&atmos=none";
        var json = await RequestAsync(path, "GET", ct);
        using var doc = JsonDocument.Parse(json);
        var tracks = new List<LosslessTrack>();
        if (doc.RootElement.TryGetProperty("tracks", out var arr))
        {
            foreach (var t in arr.EnumerateArray())
            {
                tracks.Add(new LosslessTrack
                {
                    Id = t.GetProperty("id").GetString() ?? "",
                    Title = t.GetProperty("title").GetString() ?? "",
                    Artist = t.GetProperty("artist").GetString() ?? "",
                    Album = t.TryGetProperty("album", out var a) ? a.GetString() ?? "" : "",
                    Duration = t.TryGetProperty("duration", out var d) ? d.GetInt32() : 0,
                    Format = t.TryGetProperty("format", out var f) ? f.GetString() ?? "flac" : "flac",
                    Quality = t.TryGetProperty("quality", out var q) ? q.GetString() ?? "Lossless" : "Lossless",
                    ArtworkUrl = t.TryGetProperty("artworkURL", out var art) ? art.GetString() ?? "" : "",
                });
            }
        }
        return tracks;
    }

    /// <summary>Get a DASH manifest URL for a track.</summary>
    public async Task<string?> GetStreamUrlAsync(string trackId, string quality = "hi_res_lossless", CancellationToken ct = default)
    {
        var path = $"/stream/{trackId}?quality={quality}&atmos=none";
        var json = await RequestAsync(path, "GET", ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("url", out var url))
            return url.GetString();
        if (doc.RootElement.TryGetProperty("streamUrl", out var su))
            return su.GetString();
        if (doc.RootElement.TryGetProperty("directUrl", out var du))
            return du.GetString();
        return null;
    }

    /// <summary>Download a track as FLAC. Returns the file path.</summary>
    public async Task<string> DownloadAsync(string trackId, string outputPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var mpdUrl = await GetStreamUrlAsync(trackId, "hi_res_lossless", ct);
        if (mpdUrl is null)
            throw new InvalidOperationException("No stream URL returned");

        // The stream URL is a DASH manifest, or (from some servers) the file itself.
        using var mpdResp = await _http.GetAsync(mpdUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        mpdResp.EnsureSuccessStatusCode();
        var mediaType = mpdResp.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            await using var direct = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await mpdResp.Content.CopyToAsync(direct, ct);
            progress?.Report(1);
            return outputPath;
        }

        var mpdText = await mpdResp.Content.ReadAsStringAsync(ct);

        // Parse init and media segment templates
        var initMatch = Regex.Match(mpdText, @"initialization=""([^""]+)""");
        var mediaMatch = Regex.Match(mpdText, @"\smedia=""([^""]+)""");
        if (!initMatch.Success || !mediaMatch.Success)
            throw new InvalidOperationException("Could not parse DASH manifest");

        var baseUri = new Uri(mpdUrl);
        var initUrl = new Uri(baseUri, WebUtility.HtmlDecode(initMatch.Groups[1].Value)).AbsoluteUri;
        var mediaTemplate = new Uri(baseUri, WebUtility.HtmlDecode(mediaMatch.Groups[1].Value)).AbsoluteUri;

        // Parse duration. The manifest's numbers are invariant: "12.5", never "12,5".
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var number = System.Globalization.NumberStyles.Float;
        var durMatch = Regex.Match(mpdText, @"mediaPresentationDuration=""PT(?:(\d+)H)?(?:(\d+)M)?([\d.]+)S""");
        double duration = 0;
        if (durMatch.Success)
        {
            var h = double.TryParse(durMatch.Groups[1].Value, number, invariant, out var hv) ? hv : 0;
            var m = double.TryParse(durMatch.Groups[2].Value, number, invariant, out var mv) ? mv : 0;
            var s = double.TryParse(durMatch.Groups[3].Value, number, invariant, out var sv) ? sv : 0;
            duration = h * 3600 + m * 60 + s;
        }

        var tsMatch = Regex.Match(mpdText, @"timescale=""(\d+)""");
        var timescale = tsMatch.Success ? int.Parse(tsMatch.Groups[1].Value, invariant) : 44100;
        var segDurMatch = Regex.Match(mpdText, @"segmentDuration=""(\d+)""");
        var segDur = segDurMatch.Success ? double.Parse(segDurMatch.Groups[1].Value, invariant) / timescale : 4.0;
        if (segDur <= 0) segDur = 4.0;
        var expected = Math.Max(1, (int)Math.Ceiling(duration / segDur));

        // Written as it comes rather than held whole in memory.
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var initBytes = await _http.GetByteArrayAsync(initUrl, ct);
        await output.WriteAsync(initBytes, ct);

        // The duration's rounding can promise one segment more than there is: only a
        // missing segment past the expected count ends the song. Earlier, it is a failure,
        // not a shorter file.
        for (var i = 1; i <= expected + 1; i++)
        {
            ct.ThrowIfCancellationRequested();
            var segUrl = mediaTemplate.Replace("$Number$", i.ToString(invariant));
            using var seg = await _http.GetAsync(segUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!seg.IsSuccessStatusCode)
            {
                if (i >= expected) break;
                seg.EnsureSuccessStatusCode();
            }

            var segBytes = await seg.Content.ReadAsByteArrayAsync(ct);
            if (segBytes.Length == 0) break;
            await output.WriteAsync(segBytes, ct);
            progress?.Report(Math.Min(1, (double)i / expected));
        }

        return outputPath;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class LosslessTrack
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public int Duration { get; set; }
    public string Format { get; set; } = "flac";
    public string Quality { get; set; } = "Lossless";
    public string ArtworkUrl { get; set; } = "";
}