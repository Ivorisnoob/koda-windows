using System.Text.Json.Nodes;
using Koda.Core.InnerTube;
using Koda.Core.Video;

namespace Koda.Core;

/// <summary>
/// YouTube itself, beside YouTube Music: video feeds, search, watch pages, channels and streams,
/// on the WEB client at www.youtube.com, signed in as the active account when there is one.
/// Ported from KodaCore's VideoTube.swift.
/// </summary>
public sealed partial class YouTubeMusic
{
    /// <summary>Where a list came from, which decides the endpoint its continuation goes to.</summary>
    public enum VideoListSource
    {
        Browse,
        Search,
        Next,
    }

    /// <summary>The account's Home. Signed out YouTube sends an empty Home ("search to get
    /// started"), so the app builds its own then (Up Next of what was watched, or a search).</summary>
    public async Task<VideoPage> VideoHomeAsync(CancellationToken cancellation = default) =>
        VideoPages.Feed(await _api.WebAsync("browse",
            new JsonObject { ["browseId"] = "FEwhat_to_watch" }, cancellation, Signing.Required));

    /// <summary>The latest from the account's subscriptions.</summary>
    public async Task<VideoPage> VideoSubscriptionsAsync(CancellationToken cancellation = default) =>
        VideoPages.Feed(await _api.WebAsync("browse",
            new JsonObject { ["browseId"] = "FEsubscriptions" }, cancellation, Signing.Required));

    /// <summary>Videos and the channels that match, best first. Shorts are left out.</summary>
    public async Task<VideoSearchResults> SearchVideosAsync(string query, CancellationToken cancellation = default) =>
        VideoPages.Search(await _api.WebAsync("search", new JsonObject { ["query"] = query }, cancellation));

    /// <summary>The next page of a list.</summary>
    public async Task<VideoPage> MoreVideosAsync(string continuation, VideoListSource source,
        CancellationToken cancellation = default)
    {
        var endpoint = source switch
        {
            VideoListSource.Browse => "browse",
            VideoListSource.Search => "search",
            _ => "next",
        };
        return VideoPages.Continuation(await _api.WebAsync(endpoint,
            new JsonObject { ["continuation"] = continuation }, cancellation));
    }

    /// <summary>Everything under the player: the video's details, its channel and Up Next. Kept a
    /// few minutes per session: going back to a video, or opening one a signed-out Home was built
    /// from, asks YouTube nothing. (Ratings and subscriptions made since are the app's to apply.)</summary>
    public Task<WatchPage> WatchPageAsync(string videoId, CancellationToken cancellation = default) =>
        Page($"watch|{videoId}", async () =>
            VideoPages.Watch(await _api.WebAsync("next", new JsonObject { ["videoId"] = videoId }, cancellation), videoId)
            ?? throw MusicException.BadResponse(200));

    /// <summary>A channel's header and one of its tabs: the Videos tab, newest first, unless
    /// <paramref name="tab"/> (from an earlier page's tabs) says otherwise.</summary>
    public Task<ChannelPage> ChannelAsync(string channelId, ChannelTab? tab = null, bool fresh = false,
        CancellationToken cancellation = default)
    {
        // The Videos tab's params [verified October 2026].
        var @params = tab?.Params ?? "EgZ2aWRlb3PyBgQKAjoA";
        Task<ChannelPage> Load() => LoadChannelAsync(channelId, @params, cancellation);
        return fresh ? Load() : Page($"channel|{channelId}|{@params}", Load);
    }

    private async Task<ChannelPage> LoadChannelAsync(string channelId, string @params, CancellationToken cancellation) =>
        VideoPages.Channel(await _api.WebAsync("browse",
            new JsonObject { ["browseId"] = channelId, ["params"] = @params }, cancellation), channelId)
        ?? throw MusicException.BadResponse(200);

    /// <summary>A topic page's shelves: Music, Live, News, Sports, Learning.</summary>
    public Task<VideoShelf[]> ExploreAsync(string browseId, CancellationToken cancellation = default) =>
        Page($"explore|{browseId}", async () =>
        {
            var shelves = VideoPages.Shelves(await _api.WebAsync("browse",
                new JsonObject { ["browseId"] = browseId }, cancellation));
            return shelves.Length > 0 ? shelves : throw MusicException.BadResponse(200);
        });

    /// <summary>A playlist of videos: <c>WL</c> (Watch Later) and <c>LL</c> (Liked videos) are the
    /// account's own and need it signed in. Never cached: they change as the listener uses them.</summary>
    public async Task<VideoPlaylistPage> VideoPlaylistAsync(string playlistId, CancellationToken cancellation = default)
    {
        var signing = playlistId is "WL" or "LL" ? Signing.Required : Signing.IfSignedIn;
        return VideoPages.Playlist(await _api.WebAsync("browse",
            new JsonObject { ["browseId"] = "VL" + playlistId }, cancellation, signing))
            ?? throw MusicException.BadResponse(200);
    }

    /// <summary>The channels the signed-in account subscribes to, from YouTube's sidebar (guide):
    /// one call, kept a few minutes.</summary>
    public Task<ChannelRef[]> SubscribedChannelsAsync(CancellationToken cancellation = default) =>
        Page("guide", async () => VideoPages.Subscriptions(
            await _api.WebAsync("guide", new JsonObject(), cancellation, Signing.Required)));

    /// <summary>A channel tab's videos in another order (a <see cref="ChannelSort"/>), and the
    /// orders as now chosen.</summary>
    public async Task<(VideoPage Videos, ChannelSort[] Sorts)> ChannelVideosAsync(ChannelSort sort,
        ChannelRef channel, CancellationToken cancellation = default)
    {
        var reloaded = VideoPages.Reloaded(await _api.WebAsync("browse",
            new JsonObject { ["continuation"] = sort.Token }, cancellation));
        if (reloaded.Videos.Videos.Length == 0) throw MusicException.BadResponse(200);
        return (VideoPages.WithChannel(reloaded.Videos, channel), reloaded.Sorts);
    }

    /// <summary>What a channel says about itself: the About panel.</summary>
    public async Task<ChannelAbout> ChannelAboutAsync(string token, CancellationToken cancellation = default) =>
        VideoPages.About(await _api.WebAsync("browse", new JsonObject { ["continuation"] = token }, cancellation))
        ?? throw MusicException.BadResponse(200);

    /// <summary>A page of a video's comments, top first: the watch page's token, then each page's
    /// continuation.</summary>
    public async Task<CommentsPage> CommentsAsync(string token, CancellationToken cancellation = default) =>
        VideoPages.Comments(await _api.WebAsync("next", new JsonObject { ["continuation"] = token }, cancellation));

    /// <summary>Like, dislike or clear the account's rating of a video, on YouTube itself (a like
    /// there is a like in YouTube Music too, for a song).</summary>
    public async Task RateVideoAsync(string videoId, LikeStatus status, CancellationToken cancellation = default)
    {
        var endpoint = status switch
        {
            LikeStatus.Like => "like/like",
            LikeStatus.Dislike => "like/dislike",
            _ => "like/removelike",
        };
        await _api.WebAsync(endpoint,
            new JsonObject { ["target"] = new JsonObject { ["videoId"] = videoId } }, cancellation, Signing.Required);
    }

    /// <summary>Subscribe to a channel on YouTube itself, or unsubscribe.</summary>
    public async Task SetSubscribedOnYouTubeAsync(string channelId, bool subscribed,
        CancellationToken cancellation = default)
    {
        var endpoint = subscribed ? "subscription/subscribe" : "subscription/unsubscribe";
        await _api.WebAsync(endpoint,
            new JsonObject { ["channelIds"] = new JsonArray(channelId) }, cancellation, Signing.Required);
    }

    /// <summary>A video's DASH stream, built from the same visionOS <c>/player</c> answer the
    /// music streams use.</summary>
    public Task<VideoStream> VideoStreamAsync(string videoId, CancellationToken cancellation = default) =>
        _streams.ResolveVideoAsync(videoId, cancellation);

    /// <summary>Forget a stream googlevideo refused mid-play, so the next resolve starts fresh.</summary>
    public Task VideoStreamFailedAsync(string videoId, CancellationToken cancellation = default) =>
        _streams.InvalidateAsync(videoId, refusedIdentity: true, cancellation);

    /// <summary>A video's captions in WebVTT, the default track first; null when it has none.</summary>
    public async Task<VideoCaptionTrack[]> VideoCaptionsAsync(string videoId, CancellationToken cancellation = default)
    {
        var root = await _api.WebAsync("player", new JsonObject { ["videoId"] = videoId }, cancellation);
        var tracks = root["captions"]["playerCaptionsTracklistRenderer"]["captionTracks"].Array;
        var list = new List<VideoCaptionTrack>();
        foreach (var track in tracks)
        {
            var url = track["baseUrl"].String;
            if (url is null) continue;
            var language = track["languageCode"].String ?? "";
            var name = track["name"].Text();
            var kind = track["kind"].String;
            list.Add(new VideoCaptionTrack(language, string.IsNullOrWhiteSpace(name) ? language : name,
                kind == "asr", new Uri(url.Contains('?') ? url + "&fmt=vtt" : url + "?fmt=vtt")));
        }

        return [.. list];
    }
}
