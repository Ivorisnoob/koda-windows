using System.Text.Json.Serialization;

namespace Koda.Core.Video;

/// <summary>
/// A YouTube video, as feeds, search, channels and Up Next list it.
/// Ported from KodaCore's VideoModels.swift. Declared inside <c>Koda.Core.Video</c>
/// because the namespace itself owns that name.
/// </summary>
public sealed record Video(
    string Id,
    string Title,
    ChannelRef? Channel = null,
    Artwork? Thumbnail = null,
    double? Duration = null,
    string? Views = null,
    string? Published = null,
    bool IsLive = false)
{
    /// <summary>YouTube's 1280-wide frame, which exists for every video; both 16:9,
    /// unlike the letterboxed <c>hqdefault</c>.</summary>
    [JsonIgnore]
    public Uri ThumbnailUrl => new($"https://i.ytimg.com/vi/{Id}/hq720.jpg");

    [JsonIgnore]
    public Uri FallbackThumbnailUrl => new($"https://i.ytimg.com/vi/{Id}/mqdefault.jpg");

    /// <summary>"Channel · 1.2M views · 3 days ago", leaving out what isn't known.</summary>
    [JsonIgnore]
    public string Byline => string.Join(" · ", new[] { Channel?.Name, Views, Published }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>"12:34", or "1:02:03"; empty when the feed didn't say.</summary>
    [JsonIgnore]
    public string DurationText => Duration is { } seconds && seconds > 0
        ? TimeSpan.FromSeconds(seconds).TotalHours >= 1
            ? $"{(int)TimeSpan.FromSeconds(seconds).TotalHours}:{TimeSpan.FromSeconds(seconds).Minutes:00}:{TimeSpan.FromSeconds(seconds).Seconds:00}"
            : $"{TimeSpan.FromSeconds(seconds).Minutes}:{TimeSpan.FromSeconds(seconds).Seconds:00}"
        : "";
}

/// <summary>The channel a video is from.</summary>
public sealed record ChannelRef(string? Id, string Name, Artwork? Avatar = null)
{
    /// <summary>Subscribed channels, those that posted most recently first: in the order they
    /// first appear in the subscriptions feed (newest uploads first), then the rest as given.
    /// YouTube's own list (the guide) is alphabetical, with no dates.</summary>
    public static ChannelRef[] ByLatestUpload(IReadOnlyList<ChannelRef> channels, IReadOnlyList<Video> feed)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var video in feed)
        {
            if (video.Channel is not { } channel) continue;
            var key = channel.Id ?? channel.Name;
            if (!rank.ContainsKey(key)) rank[key] = rank.Count;
        }

        var ranked = channels.Select((channel, offset) => (offset, channel,
            rank: rank.TryGetValue(channel.Id ?? channel.Name, out var r) ? r : (int?)null));
        return [.. ranked
            .OrderBy(x => x.rank is null ? 1 : 0)
            .ThenBy(x => x.rank ?? int.MaxValue)
            .ThenBy(x => x.offset)
            .Select(x => x.channel)];
    }
}

/// <summary>A channel as search lists it.</summary>
public sealed record ChannelSummary(
    string Id,
    string Name,
    string? Handle = null,
    string? Subscribers = null,
    Artwork? Avatar = null)
{
    public ChannelRef Ref => new(Id, Name, Avatar);
}

/// <summary>One page of a list of videos, and the token for the next.</summary>
public sealed record VideoPage(Video[] Videos, string? Continuation = null)
{
    public static readonly VideoPage Empty = new([]);

    public VideoPage() : this([])
    {
    }
}

public sealed record VideoSearchResults(ChannelSummary[] Channels, VideoPage Videos);

/// <summary>A titled row of videos on a topic page.</summary>
public sealed record VideoShelf(string Title, Video[] Videos);

public sealed record VideoPlaylistPage(string Title, VideoPage Videos);

/// <summary>The account's rating of a video, as YouTube's like and dislike buttons draw it.</summary>
public enum LikeStatus
{
    Like,
    Dislike,
    Indifferent,
}

/// <summary>What the watch page shows under the player.</summary>
public sealed record WatchPage(
    Video Video,
    string? ViewCount,
    string? Date,
    string Description,
    string? Subscribers,
    bool? IsSubscribed,
    Dictionary<LikeStatus, string> LikeCounts,
    LikeStatus LikeStatus,
    string? CommentCount,
    string? CommentsToken,
    string? AutoplayId,
    VideoPage Related)
{
    /// <summary>Up Next's first choice: YouTube's own autoplay pick when it's in the list, else its top.</summary>
    public Video? Next =>
        Related.Videos.FirstOrDefault(v => v.Id == AutoplayId) ?? Related.Videos.FirstOrDefault();
}

/// <summary>One of a channel's tabs that Koda can show.</summary>
public enum ChannelTabKind
{
    Videos,
    Live,
}

public static class ChannelTabKindText
{
    public static string Title(this ChannelTabKind kind) => kind == ChannelTabKind.Videos ? "Videos" : "Live";
}

public sealed record ChannelTab(ChannelTabKind Kind, string Params);

/// <summary>An order a channel's videos can be listed in: Latest, Popular, Oldest.</summary>
public sealed record ChannelSort(string Title, string Token, bool IsSelected);

public sealed record ChannelPage(
    ChannelSummary Channel,
    Artwork? Banner,
    string? Description,
    string? VideoCount,
    bool? IsSubscribed,
    bool IsVerified,
    ChannelTab[] Tabs,
    ChannelTabKind Tab,
    ChannelSort[] Sorts,
    string? AboutToken,
    VideoPage Videos);

/// <summary>A channel's About panel.</summary>
public sealed record ChannelAbout(
    string? Description,
    string? Country,
    string? Subscribers,
    string? Views,
    string? Joined,
    string? VideoCount,
    ChannelLink[] Links);

/// <summary>A link a channel lists about itself, its URL unwrapped from YouTube's redirect.</summary>
public sealed record ChannelLink(string Title, string Label, Uri Url);

/// <summary>A comment under a video.</summary>
public sealed record VideoComment(
    string Id,
    string Author,
    string? AuthorChannelId,
    Uri? Avatar,
    string Text,
    string Published,
    string Likes,
    string Replies,
    bool IsCreator,
    bool IsVerified);

public sealed record CommentsPage(VideoComment[] Comments, string? Continuation = null);

/// <summary>One caption track a video offers, as the player's Captions menu lists it.</summary>
public sealed record VideoCaptionTrack(string Language, string Name, bool IsAutomatic, Uri Url);

/// <summary>How far into a video the listener got. The same rules as the Mac:
/// resume when it had really begun and wasn't all but over.</summary>
public sealed record VideoProgress(double Position, double Duration)
{
    [JsonIgnore]
    public double Fraction => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;

    /// <summary>Where the video starts again: here, unless it had barely begun or was all but
    /// over, when it starts from the top, as YouTube does.</summary>
    [JsonIgnore]
    public double? ResumeAt =>
        Position >= 10 && Position < Duration - 20 && Fraction < 0.95 ? Position : null;
}
