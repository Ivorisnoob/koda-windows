using System.Text.RegularExpressions;

namespace Koda.Core;

/// <summary>What a YouTube or YouTube Music link points at.</summary>
/// <remarks>
/// Accepts music.youtube.com, youtube.com (www and m), youtu.be, and Koda's own
/// <c>koda://</c> links, which carry the same path and query as a YouTube one
/// (<c>koda://watch?v=…</c>, <c>koda://playlist?list=…</c>, <c>koda://browse/MPREb_…</c>).
/// Ported from KodaCore's YouTubeLink.swift.
/// </remarks>
public abstract record YouTubeLink
{
    public sealed record Song(string VideoId) : YouTubeLink;

    public sealed record Album(string BrowseId) : YouTubeLink;

    public sealed record Playlist(string Id) : YouTubeLink;

    public sealed record Artist(string ChannelId) : YouTubeLink;

    /// <summary>The row offered when a link is pasted into search.</summary>
    public string OpenTitle => this switch
    {
        Song => "Play Song from Link",
        Album => "Open Album from Link",
        Playlist => "Open Playlist from Link",
        Artist => "Open Artist from Link",
        _ => "Open Link",
    };

    private static readonly Regex LinkPattern = new(
        @"(?:https?://|koda://)[^\s<>""']+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A URL on its own, or the first link in free text such as something pasted
    /// into search. A link to a radio mix has no page of its own, so it is not one.
    /// </summary>
    public static YouTubeLink? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (FromUrl(trimmed) is { } direct)
        {
            return direct;
        }

        foreach (Match match in LinkPattern.Matches(trimmed))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"');
            if (FromUrl(candidate) is { } link)
            {
                return link;
            }
        }

        return null;
    }

    private static YouTubeLink? FromUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            var scheme = uri.Scheme.ToLowerInvariant();
            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            var query = ParseQuery(uri.Query);

            switch (scheme)
            {
                case "koda":
                    // koda://watch?v=… puts the first word in the host.
                    if (host.Length > 0)
                    {
                        path.Insert(0, host);
                    }
                    break;
                case "http":
                case "https":
                    if (host == "youtu.be")
                    {
                        return path.Count > 0 && IsVideoId(path[0]) ? new Song(path[0]) : null;
                    }
                    if (host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com"))
                    {
                        return null;
                    }
                    break;
                default:
                    return null;
            }

            if (path.Count == 0)
            {
                return null;
            }

            switch (path[0])
            {
                case "watch":
                    if (query.TryGetValue("v", out var videoId) && IsVideoId(videoId))
                    {
                        return new Song(videoId);
                    }
                    return query.TryGetValue("list", out var list) ? PlaylistLink(list) : null;
                case "shorts":
                case "live":
                case "embed":
                    return path.Count > 1 && IsVideoId(path[1]) ? new Song(path[1]) : null;
                case "playlist":
                    return query.TryGetValue("list", out var playlistId) ? PlaylistLink(playlistId) : null;
                case "browse":
                    if (path.Count <= 1)
                    {
                        return null;
                    }
                    var browseId = path[1];
                    if (browseId.StartsWith("MPREb_", StringComparison.Ordinal))
                    {
                        return new Album(browseId);
                    }
                    if (browseId.StartsWith("UC", StringComparison.Ordinal))
                    {
                        return new Artist(browseId);
                    }
                    if (browseId.StartsWith("VL", StringComparison.Ordinal) && browseId.Length > 2)
                    {
                        return new Playlist(browseId[2..]);
                    }
                    return null;
                case "channel":
                    return path.Count > 1 && path[1].StartsWith("UC", StringComparison.Ordinal)
                        ? new Artist(path[1])
                        : null;
                default:
                    return null;
            }
        }
        catch (UriFormatException)
        {
            // A malformed escape is not a link.
            return null;
        }
    }

    /// <summary>A playlist id, or null for a radio mix (RD…), which has no page of its own.</summary>
    private static YouTubeLink? PlaylistLink(string id) =>
        id.Length > 0 && !id.StartsWith("RD", StringComparison.Ordinal) ? new Playlist(id) : null;

    private static bool IsVideoId(string id)
    {
        if (id.Length != 11)
        {
            return false;
        }

        foreach (var c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The query's first value per name, as the Swift parser's dictionary keeps it.</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Length <= 1)
        {
            return values;
        }

        foreach (var part in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = part.IndexOf('=');
            var name = at < 0 ? part : part[..at];
            var value = at < 0 ? "" : part[(at + 1)..];
            values.TryAdd(Uri.UnescapeDataString(name.Replace('+', ' ')), Uri.UnescapeDataString(value.Replace('+', ' ')));
        }

        return values;
    }
}
