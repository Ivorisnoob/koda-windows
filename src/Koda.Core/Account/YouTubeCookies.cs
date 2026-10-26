using System.Text;
using System.Text.Json.Serialization;

namespace Koda.Core;

/// <summary>
/// A signed-in account's youtube.com cookies, in the order they were captured.
///
/// Only youtube.com's cookies belong here. The sign-in web view's jar also holds
/// google.com's, including a SAPISID of their own; signing with that one is answered
/// as "logged in" but anonymous, so cookies from any other domain are dropped.
/// </summary>
public sealed class YouTubeCookies : IEquatable<YouTubeCookies>
{
    public sealed record Cookie(string Name, string Value);

    /// <summary>A cookie as the sign-in web view reports it: its domain and expiry decide
    /// whether it is part of the session.</summary>
    public readonly record struct WebCookie(string Name, string Value, string Domain, DateTimeOffset? Expires);

    /// <summary>The exact cookie names Google sets on a YouTube sign-in. Exact names, not
    /// substrings: "SID" would also match SAPISID and __Secure-3PSID, which a jar with no
    /// real session can still carry.</summary>
    public static readonly string[] RequiredNames = ["SID", "HSID", "SSID", "APISID", "SAPISID"];

    private readonly List<Cookie> _cookies;

    /// <summary>Cookies by name; a repeated name keeps its first value.</summary>
    [JsonConstructor]
    public YouTubeCookies(IEnumerable<Cookie>? cookies)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        _cookies = [];
        foreach (var cookie in cookies ?? [])
        {
            if (string.IsNullOrEmpty(cookie.Name) || string.IsNullOrEmpty(cookie.Value)) continue;
            if (seen.Add(cookie.Name)) _cookies.Add(cookie);
        }
    }

    public IReadOnlyList<Cookie> Cookies => _cookies;

    /// <summary>
    /// A <c>Cookie</c> header as pasted or stored: <c>a=1; b=2</c>, tolerating a leading
    /// <c>Cookie:</c>, surrounding quotes and line breaks.
    /// </summary>
    public static YouTubeCookies FromHeader(string header)
    {
        var text = header.Trim();
        if (text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"')) text = text[1..^1];
        if (text.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase)) text = text["cookie:".Length..];

        var pairs = new List<Cookie>();
        foreach (var part in text.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var piece = part.Trim();
            // Only the first "=" separates: values are base64-ish and can end in "=".
            var equals = piece.IndexOf('=');
            if (equals < 0) continue;
            pairs.Add(new Cookie(piece[..equals].Trim(), piece[(equals + 1)..]));
        }
        return new YouTubeCookies(pairs);
    }

    /// <summary>
    /// The sign-in web view's cookies, keeping youtube.com's only. Where a name is set both
    /// for the whole domain and for one host, the domain-wide cookie wins: that is the one
    /// every youtube.com host is sent.
    /// </summary>
    public static YouTubeCookies FromWebView(IEnumerable<WebCookie> cookies, DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.UtcNow;
        var kept = cookies
            .Where(c => IsYouTubeDomain(c.Domain) && (c.Expires is not { } expires || expires > time))
            .ToArray();
        var ordered = kept.Where(c => IsDomainWide(c.Domain)).Concat(kept.Where(c => !IsDomainWide(c.Domain)));
        return new YouTubeCookies(ordered.Select(c => new Cookie(c.Name, c.Value)));
    }

    public string? this[string name] => _cookies.FirstOrDefault(c => c.Name == name)?.Value;

    public bool IsEmpty => _cookies.Count == 0;

    /// <summary>The <c>Cookie</c> request header.</summary>
    public string Header => string.Join("; ", _cookies.Select(c => $"{c.Name}={c.Value}"));

    /// <summary>The secret requests are signed with. YouTube hands the same value out under
    /// the legacy name and its partitioned twin, so a jar missing one usually has the other.</summary>
    public string? Sapisid => this["SAPISID"] ?? this["__Secure-3PAPISID"];

    /// <summary>Has what a signed-in session can't work without: a SAPISID to sign with and
    /// the SID session itself.</summary>
    public bool LooksSignedIn => Sapisid is not null && this["SID"] is not null;

    /// <summary>The five cookies Google sets when the browser is really signed in.</summary>
    public bool HasSignInCookies => RequiredNames.All(name => this[name] is not null);

    // MARK: Rotation

    /// <summary>Cookies Google re-issues during a live session. The <c>*PSIDTS</c> pair in
    /// particular is replaced every few hours, and a session that keeps replaying the value
    /// it signed in with is eventually answered as signed out.</summary>
    public static readonly HashSet<string> RotatingNames = new(StringComparer.Ordinal)
    {
        "SID", "HSID", "SSID", "APISID", "SAPISID", "LOGIN_INFO", "SIDCC",
        "__Secure-1PSID", "__Secure-3PSID", "__Secure-1PAPISID", "__Secure-3PAPISID",
        "__Secure-1PSIDTS", "__Secure-3PSIDTS", "__Secure-1PSIDCC", "__Secure-3PSIDCC",
    };

    /// <summary>
    /// These cookies with a response's fresh values folded in.
    ///
    /// Only names already here, or ones Google rotates, are taken: every response also sets
    /// throwaway cookies (YSC, VISITOR_INFO1_LIVE, consent) that authenticate nothing.
    /// Deletions are never applied, so one odd response can't sign anyone out; the session's
    /// fate is YouTube's <c>logged_in</c> verdict, not a cookie going missing. A cookie whose
    /// value changed since <paramref name="sent"/> went out is left alone: several signed
    /// requests are in flight together, and an older response must not roll back a newer
    /// rotation.
    /// </summary>
    internal YouTubeCookies Merging(IReadOnlyList<SetCookie> updates, YouTubeCookies sent)
    {
        var merged = _cookies.ToList();
        foreach (var update in updates)
        {
            if (update.IsDeletion || update.Value.Length == 0) continue;
            var index = merged.FindIndex(c => c.Name == update.Name);
            if (index < 0 && !RotatingNames.Contains(update.Name)) continue;
            var current = index >= 0 ? merged[index].Value : null;
            if (sent[update.Name] != current) continue;
            if (index >= 0) merged[index] = merged[index] with { Value = update.Value };
            else merged.Add(new Cookie(update.Name, update.Value));
        }
        return new YouTubeCookies(merged);
    }

    public static bool IsYouTubeDomain(string domain)
    {
        var host = domain.Trim().Trim('.').ToLowerInvariant();
        return host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal);
    }

    private static bool IsDomainWide(string domain)
    {
        var host = domain.Trim().ToLowerInvariant();
        return host is ".youtube.com" or "youtube.com";
    }

    public bool Equals(YouTubeCookies? other) =>
        other is not null && _cookies.Count == other._cookies.Count
        && _cookies.Zip(other._cookies).All(pair => pair.First == pair.Second);

    public override bool Equals(object? obj) => Equals(obj as YouTubeCookies);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var cookie in _cookies)
        {
            hash.Add(cookie.Name, StringComparer.Ordinal);
            hash.Add(cookie.Value, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    /// <summary>Never the values: they are the account.</summary>
    public override string ToString() => $"YouTubeCookies({string.Join(", ", _cookies.Select(c => c.Name))})";
}

/// <summary>One cookie from a <c>Set-Cookie</c> header.</summary>
public sealed record SetCookie(string Name, string Value, bool IsDeletion)
{
    /// <summary>
    /// Every cookie in <c>Set-Cookie</c> header values. HTTP stacks join several headers into
    /// one with commas, and <c>Expires</c> dates hold a comma of their own ("Wed, 21 Oct"),
    /// so a comma starts a new cookie only where a <c>name=</c> follows it. Cookies for a
    /// domain other than youtube.com are dropped.
    /// </summary>
    public static IReadOnlyList<SetCookie> Parse(IEnumerable<string> headers, DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.UtcNow;
        return headers.SelectMany(Split)
            .Select(part => ParseOne(part, time))
            .OfType<SetCookie>()
            .ToArray();
    }

    private static IEnumerable<string> Split(string header)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < header.Length; i++)
        {
            var c = header[i];
            if (c == ',' && StartsCookie(header.AsSpan(i + 1)))
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        parts.Add(current.ToString());
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0);
    }

    /// <summary>Whether text begins with <c>token=</c>, as a cookie does and a date
    /// ("21 Oct 2026") does not.</summary>
    private static bool StartsCookie(ReadOnlySpan<char> text)
    {
        var start = 0;
        while (start < text.Length && text[start] == ' ') start++;
        var trimmed = text[start..];
        var equals = trimmed.IndexOf('=');
        if (equals <= 0) return false;
        foreach (var c in trimmed[..equals])
        {
            if (c is ' ' or ';' or ',' or '\t') return false;
        }
        return true;
    }

    private static SetCookie? ParseOne(string text, DateTimeOffset now)
    {
        var fields = text.Split(';', StringSplitOptions.None).Select(f => f.Trim()).ToArray();
        if (fields.Length == 0) return null;
        var first = fields[0];
        var equals = first.IndexOf('=');
        if (equals < 0) return null;
        var name = first[..equals].Trim();
        if (name.Length == 0) return null;

        var value = first[(equals + 1)..];
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"')) value = value[1..^1];

        int? maxAge = null;
        DateTimeOffset? expires = null;
        foreach (var field in fields.Skip(1))
        {
            var pair = field.Split('=', 2);
            if (pair.Length != 2) continue;
            var key = pair[0].Trim().ToLowerInvariant();
            var val = pair[1].Trim();
            switch (key)
            {
                case "max-age" when int.TryParse(val, out var seconds):
                    maxAge = seconds;
                    break;
                case "expires" when DateTimeOffset.TryParse(val, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var date):
                    expires = date;
                    break;
                case "domain" when !YouTubeCookies.IsYouTubeDomain(val):
                    return null;
            }
        }

        // Max-Age outranks Expires when both are present (RFC 6265 §5.3).
        var deleted = maxAge is { } age ? age <= 0 : expires is { } expiry && expiry <= now;
        return new SetCookie(name, value, deleted || value.Length == 0);
    }
}
