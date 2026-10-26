using System.Security.Cryptography;
using System.Text;

namespace Koda.Core;

/// <summary>
/// The <c>SAPISIDHASH</c> signature YouTube's web client sends with a signed request.
/// Not used for anything that must be secure; it is how a browser proves it holds the
/// session's SAPISID cookie.
/// </summary>
public static class SapisidHash
{
    public const string MusicOrigin = "https://music.youtube.com";

    /// <summary><c>SAPISIDHASH &lt;unix&gt;_&lt;sha1("&lt;unix&gt; &lt;sapisid&gt; &lt;origin&gt;")&gt;</c>.</summary>
    public static string Authorization(string sapisid, string origin = MusicOrigin, DateTimeOffset? at = null)
    {
        var unix = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{unix} {sapisid} {origin}")))
            .ToLowerInvariant();
        return $"SAPISIDHASH {unix}_{hash}";
    }

    /// <summary>
    /// The headers that carry an account on an InnerTube request. Without a SAPISID the
    /// cookies still go but nothing is signed: an empty Authorization is worse than none.
    /// </summary>
    public static Dictionary<string, string> Headers(YouTubeCookies cookies, string origin = MusicOrigin,
        DateTimeOffset? at = null)
    {
        var headers = new Dictionary<string, string>
        {
            ["Cookie"] = cookies.Header,
            ["X-Origin"] = origin,
        };
        if (cookies.Sapisid is { } sapisid)
        {
            headers["Authorization"] = Authorization(sapisid, origin, at);
            headers["X-Goog-AuthUser"] = "0";
        }
        return headers;
    }
}
