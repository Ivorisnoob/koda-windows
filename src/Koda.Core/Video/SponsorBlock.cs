using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Koda.Core.Video;

/// <summary>A stretch of a video SponsorBlock's community marked for skipping.</summary>
/// <param name="Category">sponsor, selfpromo or interaction.</param>
public sealed record SkipSegment(double Start, double End, string Category)
{
    /// <summary>As the note after a skip says it.</summary>
    public string Label => Category switch
    {
        "selfpromo" => "self-promotion",
        "interaction" => "subscribe reminder",
        _ => "sponsor",
    };
}

/// <summary>
/// SponsorBlock (sponsor.ajay.app): the community's marks for sponsor reads, self-promotion and
/// "like and subscribe" reminders. Asked by the first four characters of the video id's SHA-256,
/// so the service never learns which video is playing: it answers for every video sharing the
/// prefix and the one wanted is picked out here. Nothing found, or the service down, is no
/// segments: the video just plays.
/// </summary>
public sealed class SponsorBlock : IDisposable
{
    public static readonly string[] Categories = ["sponsor", "selfpromo", "interaction"];

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public SponsorBlock(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        if (_ownsHttp) _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Koda");
    }

    public async Task<SkipSegment[]> SegmentsAsync(string videoId, CancellationToken cancellation = default)
    {
        var categories = Uri.EscapeDataString("[" + string.Join(",", Categories.Select(c => $"\"{c}\"")) + "]");
        var url = $"https://sponsor.ajay.app/api/skipSegments/{HashPrefix(videoId)}?categories={categories}&actionTypes=%5B%22skip%22%5D";
        try
        {
            using var response = await _http.GetAsync(url, cancellation);
            if (!response.IsSuccessStatusCode) return [];
            return Parse(await response.Content.ReadAsStringAsync(cancellation), videoId);
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            return [];
        }
    }

    public static string HashPrefix(string videoId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(videoId)))[..4];

    /// <summary>This video's skip segments from a hash-prefix answer, in order, short ones dropped.</summary>
    public static SkipSegment[] Parse(string text, string videoId)
    {
        var root = Json.Parse(text);
        var video = root.Array.FirstOrDefault(v => v["videoID"].String == videoId);
        return video["segments"].Array
            .Where(s => (s["actionType"].String ?? "skip") == "skip")
            .Select(s => (Range: s["segment"].Array, Category: s["category"].String ?? "sponsor"))
            .Where(s => s.Range.Length == 2 && s.Range[0].Double is not null && s.Range[1].Double is not null)
            .Select(s => new SkipSegment(s.Range[0].Double!.Value, s.Range[1].Double!.Value, s.Category))
            .Where(s => s.End - s.Start >= 1)
            .OrderBy(s => s.Start)
            .ToArray();
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
