using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Koda.Core.LastFm;

/// <summary>
/// Last.fm's wire format: request signing and the few responses Koda reads.
/// https://www.last.fm/api/authspec, https://www.last.fm/api/scrobbling
/// Ported from KodaCore LastFMProtocol.swift.
/// </summary>
public static class LastFmProtocol
{
    public static readonly Uri Endpoint = new("https://ws.audioscrobbler.com/2.0/");

    /// <summary>The <c>api_sig</c> of a call: every parameter but <c>format</c> and <c>callback</c>,
    /// sorted by name, names and values run together, the shared secret on the end, as an MD5 in hex.</summary>
    public static string Signature(IReadOnlyDictionary<string, string> parameters, string secret)
    {
        var text = new StringBuilder();
        foreach (var (key, value) in parameters
                     .Where(p => p.Key is not ("format" or "callback" or "api_sig"))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append(key).Append(value);
        }

        text.Append(secret);
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Where the listener approves Koda's token in their browser.</summary>
    public static Uri AuthorizationUrl(string apiKey, string token) =>
        new($"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(apiKey)}&token={Uri.EscapeDataString(token)}");

    /// <summary>An API key or shared secret as Last.fm issues them: 32 hex digits.</summary>
    public static bool IsCredential(string text) => text.Length == 32 && text.All(Uri.IsHexDigit);

    /// <summary>The form body of a POST: sorted, with only unreserved characters left bare.</summary>
    public static string FormEncoded(IReadOnlyDictionary<string, string> parameters) =>
        string.Join("&", parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    // MARK: Reading responses

    /// <summary>Last.fm answers a single item as an object and several as an array.</summary>
    internal static IEnumerable<JsonObject> Objects(JsonNode? value) => value switch
    {
        JsonArray array => array.OfType<JsonObject>(),
        JsonObject single => [single],
        _ => [],
    };

    /// <summary>The largest image Last.fm offers over HTTPS; it lists them smallest first.</summary>
    internal static Uri? ImageUrl(JsonNode? value) =>
        Objects(value).Reverse()
            .Select(o => Text(o["#text"]))
            .Where(t => t.StartsWith("https://", StringComparison.Ordinal))
            .Select(t => Uri.TryCreate(t, UriKind.Absolute, out var uri) ? uri : null)
            .FirstOrDefault(u => u is not null);

    internal static string Text(JsonNode? value)
    {
        if (value is not JsonValue scalar) return "";
        if (scalar.TryGetValue<string>(out var text)) return text;
        if (scalar.TryGetValue<long>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
        if (scalar.TryGetValue<double>(out var real)) return real.ToString(CultureInfo.InvariantCulture);
        return "";
    }

    private static DateTimeOffset? Date(string seconds) =>
        long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : null;

    public static LastFmTrack[] RecentTracks(JsonObject root) =>
        [.. Objects(root["recenttracks"]?["track"]).Select(track =>
        {
            var title = Text(track["name"]);
            if (title.Length == 0) return null;
            return new LastFmTrack(
                title,
                Text(track["artist"]?["#text"]),
                Text(track["album"]?["#text"]),
                ImageUrl(track["image"]),
                Text(track["@attr"]?["nowplaying"]) == "true",
                Date(Text(track["date"]?["uts"])));
        }).OfType<LastFmTrack>()];

    public static LastFmProfile? Profile(JsonObject root)
    {
        if (root["user"] is not JsonObject user) return null;
        var country = Text(user["country"]);
        return new LastFmProfile(
            Text(user["name"]),
            Text(user["realname"]),
            country == "None" ? "" : country,
            ImageUrl(user["image"]),
            int.TryParse(Text(user["playcount"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var plays) ? plays : 0,
            Date(Text(user["registered"]?["unixtime"])),
            Text(user["subscriber"]) == "1");
    }

    /// <summary>The <c>ignoredMessage</c> code of a one-track <c>track.scrobble</c>: 0 when accepted.</summary>
    public static int ScrobbleCode(JsonObject root)
    {
        if (Objects(root["scrobbles"]?["scrobble"]).FirstOrDefault() is { } scrobble &&
            scrobble["ignoredMessage"] is JsonObject ignored &&
            int.TryParse(Text(ignored["code"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            return code;
        }

        throw new LastFmException(LastFmException.Reason.UnexpectedResponse);
    }
}

/// <summary>A play in the listener's Last.fm history.</summary>
public sealed record LastFmTrack(string Title, string Artist, string Album, Uri? Artwork, bool IsNowPlaying, DateTimeOffset? PlayedAt);

/// <summary>The listener's public Last.fm profile.</summary>
public sealed record LastFmProfile(string Name, string RealName, string Country, Uri? Avatar, int PlayCount,
    DateTimeOffset? Registered, bool IsSubscriber);

/// <summary>Why a Last.fm call didn't go through, worded for the listener.</summary>
public sealed class LastFmException : Exception
{
    public enum Reason
    {
        InvalidCredentials,
        NotConnected,
        Paused,
        NoPendingSignIn,
        SignInExpired,
        NotApprovedYet,
        SessionRevoked,
        RateLimited,
        DailyLimit,
        UnexpectedResponse,
        Network,
        Api,
    }

    public Reason Kind { get; }

    /// <summary>Last.fm's own error number, for <see cref="Reason.Api"/>.</summary>
    public int Code { get; }

    public LastFmException(Reason kind, int code = 0, string? message = null) : base(Describe(kind, code, message))
    {
        Kind = kind;
        Code = code;
    }

    private static string Describe(Reason kind, int code, string? message) => kind switch
    {
        Reason.InvalidCredentials => "An API key and shared secret are each 32 letters and digits. Copy them from your Last.fm API account.",
        Reason.NotConnected => "Connect your Last.fm account first.",
        Reason.Paused => "Last.fm is off.",
        Reason.NoPendingSignIn => "Start signing in again.",
        Reason.SignInExpired => "That sign-in expired after an hour. Start again.",
        Reason.NotApprovedYet => "Approve Koda on the Last.fm page first, then come back.",
        Reason.SessionRevoked => "Last.fm no longer accepts this sign-in. Connect again.",
        Reason.RateLimited => "Last.fm asked Koda to slow down. Plays wait and send in a few minutes.",
        Reason.DailyLimit => "Last.fm's daily scrobble limit is reached. Plays wait and send later.",
        Reason.UnexpectedResponse => "Last.fm sent something Koda didn't understand.",
        Reason.Network => "Couldn't reach Last.fm. Plays wait and send when you're back online.",
        _ => string.IsNullOrEmpty(message) ? $"Last.fm error {code}." : $"{message} (Last.fm error {code})",
    };

    /// <summary>Errors after which the queue waits five minutes rather than trying again at once.</summary>
    public bool CoolsDown => Kind is Reason.Network or Reason.RateLimited or Reason.DailyLimit ||
        (Kind == Reason.Api && Code is 8 or 11 or 16 or 29);

    public static LastFmException FromApi(int code, string message) => code switch
    {
        9 => new(Reason.SessionRevoked),
        14 => new(Reason.NotApprovedYet),
        15 => new(Reason.SignInExpired),
        29 => new(Reason.RateLimited),
        _ => new(Reason.Api, code, message),
    };
}

/// <summary>
/// Counts what was actually heard of one play, the way Last.fm defines a scrobble: a track
/// over 30 seconds, heard for half its length or four minutes, whichever comes first.
/// Seeking never adds time; the player reports only seconds of sound.
/// </summary>
public sealed class LastFmListeningClock
{
    public Guid? Occurrence { get; private set; }
    public double Heard { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public bool IsSubmitted { get; private set; }

    public void Start(Guid occurrence, DateTimeOffset at)
    {
        Occurrence = occurrence;
        Heard = 0;
        StartedAt = at;
        IsSubmitted = false;
    }

    public void Reset()
    {
        Occurrence = null;
        Heard = 0;
        StartedAt = null;
        IsSubmitted = false;
    }

    /// <summary>Adds <paramref name="seconds"/> of sound; true the moment the play first qualifies.</summary>
    public bool Add(double seconds, Guid occurrence, double duration, DateTimeOffset at)
    {
        if (occurrence != Occurrence) Start(occurrence, at.AddSeconds(-seconds));
        Heard += Math.Max(0, Math.Min(seconds, 6));
        if (IsSubmitted || !Qualifies(Heard, duration)) return false;
        IsSubmitted = true;
        return true;
    }

    public static bool Qualifies(double heard, double duration) => duration > 30 && heard >= Math.Min(duration / 2, 240);
}

/// <summary>The song facts a scrobble needs, taken from whatever is playing.</summary>
public sealed record LastFmPlay(string Artist, string Track, string Album, double Duration, DateTimeOffset StartedAt)
{
    public Dictionary<string, string> Parameters()
    {
        var parameters = new Dictionary<string, string> { ["artist"] = Artist, ["track"] = Track };
        if (Album.Length > 0) parameters["album"] = Album;
        if (Duration > 0) parameters["duration"] = ((int)Math.Round(Duration)).ToString(CultureInfo.InvariantCulture);
        return parameters;
    }

    /// <summary>The artist a scrobble is filed under: the first one credited, as Last.fm keeps one
    /// artist per track, or the whole byline when YouTube didn't split it.</summary>
    public static string ArtistOf(Song song)
    {
        var first = song.Artists.FirstOrDefault()?.Name.Trim() ?? "";
        return first.Length == 0 ? song.ArtistLine : first;
    }
}
