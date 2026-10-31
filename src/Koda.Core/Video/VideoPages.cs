using System.Text;
using System.Text.Json;
using Koda.Core.Parsing;

namespace Koda.Core.Video;

/// <summary>
/// WEB (www.youtube.com) responses → video models. [verified October 2026, signed out]
///
/// Two shapes of video row are current: search still sends <c>videoRenderer</c>; the watch
/// page's Up Next, channels and the signed-in Home send <c>lockupViewModel</c>. Each page's
/// rows and its continuation are read from that page's own list, never the whole response:
/// a response also holds end screens, menus and other panels' tokens (the channel page's last
/// token is its About panel's, search's is a filter's).
///
/// Ported from KodaCore's VideoPages.swift.
/// </summary>
public static class VideoPages
{
    // MARK: Pages

    /// <summary>Home or the subscriptions feed: a rich grid.</summary>
    public static VideoPage Feed(Json root) => Page(root.First("richGridRenderer")["contents"]);

    public static VideoSearchResults Search(Json root)
    {
        var list = root.First("twoColumnSearchResultsRenderer")["primaryContents"]["sectionListRenderer"]["contents"];
        return new VideoSearchResults(Channels(list), Page(list));
    }

    /// <summary>The next page of any list: Home, search, a channel's videos or Up Next.</summary>
    public static VideoPage Continuation(Json root)
    {
        foreach (var key in new[] { "onResponseReceivedActions", "onResponseReceivedEndpoints", "onResponseReceivedCommands" })
        {
            foreach (var action in root[key].Array)
            {
                if (action["appendContinuationItemsAction"].Exists)
                {
                    return Page(action["appendContinuationItemsAction"]["continuationItems"]);
                }
            }
        }

        return VideoPage.Empty;
    }

    public static WatchPage? Watch(Json root, string id)
    {
        var primary = root.First("videoPrimaryInfoRenderer");
        var secondary = root.First("videoSecondaryInfoRenderer");
        var title = primary["title"].Text();
        if (string.IsNullOrEmpty(title)) return null;
        var owner = secondary["owner"]["videoOwnerRenderer"];
        var channelId = owner["navigationEndpoint"]["browseEndpoint"]["browseId"].NonEmpty;
        var channel = owner.Exists
            ? new ChannelRef(channelId, owner["title"].Text(), owner["thumbnail"].BestThumbnail())
            : null;
        var counts = primary["viewCount"]["videoViewCountRenderer"];
        var isLive = counts["isLive"].Bool == true;
        var video = new Video(id, title, channel,
            Views: counts["shortViewCount"].Text().NonEmptyOrNull(),
            Published: primary["relativeDateText"].Text().NonEmptyOrNull(),
            IsLive: isLive);
        var related = root.First("twoColumnWatchNextResults")["secondaryResults"]["secondaryResults"]["results"];
        var comments = root.Objects("engagementPanelSectionListRenderer")
            .FirstOrDefault(p => p["panelIdentifier"].String == "engagement-panel-comments-section");
        return new WatchPage(
            video,
            counts["viewCount"].Text().NonEmptyOrNull(),
            primary["dateText"].Text().NonEmptyOrNull(),
            secondary["attributedDescription"]["content"].String ?? "",
            owner["subscriberCountText"].Text().NonEmptyOrNull(),
            IsSubscribed(root, channelId),
            LikeCounts(primary),
            LikeStatusOf(root, id),
            comments["engagementPanelTitleHeaderRenderer"]["contextualInfo"].Text().NonEmptyOrNull(),
            CommentsToken(root),
            root.First("autoplayVideo")["watchEndpoint"]["videoId"].NonEmpty,
            Page(related));
    }

    /// <summary>The count on the video's own like button for each rating. A creator hiding it
    /// leaves the button saying "Like", which isn't a count.</summary>
    public static Dictionary<LikeStatus, string> LikeCounts(Json primary)
    {
        var entity = primary.First("likeCountEntity");
        var counts = new Dictionary<LikeStatus, string>();
        foreach (var (status, key) in new[]
                 {
                     (LikeStatus.Indifferent, "likeCountIfIndifferent"),
                     (LikeStatus.Like, "likeCountIfLiked"),
                     (LikeStatus.Dislike, "likeCountIfDisliked"),
                 })
        {
            if (entity[key]["content"].NonEmpty is { } count && count.Any(char.IsDigit)) counts[status] = count;
        }

        return counts;
    }

    /// <summary>The response draws a like button in several places (under the video, in full
    /// screen), each with its own status entity: the one keyed to this video is the video's.</summary>
    public static LikeStatus LikeStatusOf(Json root, string videoId)
    {
        var entities = root.Objects("likeStatusEntity");
        var own = entities.FirstOrDefault(e => EntityKey(e["key"].String, videoId));
        if (!own.Exists) own = entities.FirstOrDefault();
        return own["likeStatus"].String switch
        {
            "LIKE" => LikeStatus.Like,
            "DISLIKE" => LikeStatus.Dislike,
            _ => LikeStatus.Indifferent,
        };
    }

    /// <summary>Whether the account follows <paramref name="channelId"/>. The current Subscribe
    /// button keeps its state in a <c>subscriptionStateEntity</c> (frameworkUpdates), the older
    /// one in <c>subscribeButtonRenderer</c>; both are live. A page draws buttons for other
    /// channels too (featured ones, collaborators), so only a state naming this channel counts,
    /// and none is null rather than false.</summary>
    public static bool? IsSubscribed(Json root, string? channelId)
    {
        if (channelId is null) return null;
        if (root.Objects("subscriptionStateEntity").FirstOrDefault(e => EntityKey(e["key"].String, channelId)) is { Exists: true } entity
            && entity["subscribed"].Bool is { } subscribed)
        {
            return subscribed;
        }

        if (root.Objects("subscribeButtonRenderer").FirstOrDefault(b => b["channelId"].String == channelId) is { Exists: true } button
            && button["subscribed"].Bool is { } older)
        {
            return older;
        }

        return null;
    }

    /// <summary>A framework entity's key is URL-encoded base64 with the id of what it describes
    /// inside it in plain ASCII (a channel's for a subscription, a video's for a like).</summary>
    public static bool EntityKey(string? key, string id)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(id)) return false;
        try
        {
            var decoded = Uri.UnescapeDataString(key)
                .Replace('-', '+').Replace('_', '/');
            while (decoded.Length % 4 != 0) decoded += "=";
            var text = Encoding.Latin1.GetString(Convert.FromBase64String(decoded));
            return text.Contains(id, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The comments section's token. It holds two; the longer opens the full list.</summary>
    public static string? CommentsToken(Json root)
    {
        string? longest = null;
        foreach (var section in root.Objects("itemSectionRenderer")
                     .Where(s => s["sectionIdentifier"].String == "comment-item-section"))
        {
            foreach (var command in section.Objects("continuationCommand"))
            {
                if (command["token"].NonEmpty is { } token && (longest is null || token.Length > longest.Length))
                {
                    longest = token;
                }
            }
        }

        return longest;
    }

    public static ChannelPage? Channel(Json root, string id)
    {
        var header = root.First("pageHeaderViewModel");
        var name = header["title"].Strings("content").FirstOrDefault()
                   ?? root.First("channelMetadataRenderer")["title"].String ?? "";
        if (string.IsNullOrEmpty(name)) return null;
        var parts = header.First("contentMetadataViewModel")["metadataRows"].Array
            .SelectMany(row => row["metadataParts"].Array)
            .Select(part => part["text"]["content"].NonEmpty)
            .OfType<string>()
            .ToArray();
        var handle = parts.FirstOrDefault(p => p.StartsWith('@'));
        var subscribers = parts.FirstOrDefault(p => p.Contains("subscriber", StringComparison.OrdinalIgnoreCase));
        var videoCount = parts.FirstOrDefault(p => p.Contains("video", StringComparison.OrdinalIgnoreCase));
        var summary = new ChannelSummary(id, name, handle, subscribers, BestSource(header.First("decoratedAvatarViewModel")));
        // The tab that is open is the one asked for; its grid is the page's videos.
        var tabRenderers = root.Objects("tabRenderer");
        var open = tabRenderers.FirstOrDefault(t => t["selected"].Bool == true);
        if (!open.Exists) open = root;
        var tabs = tabRenderers.Select(Tab).OfType<ChannelTab>().ToArray();
        var preview = header.First("descriptionPreviewViewModel");
        return new ChannelPage(
            summary,
            BestSource(header.First("imageBannerViewModel")),
            preview["description"]["content"].NonEmpty,
            videoCount,
            IsSubscribed(root, id),
            // The tick is an image run on the title, named by a client resource, not a label.
            header["title"].Strings("imageName").Any(n => n.StartsWith("CHECK_CIRCLE", StringComparison.Ordinal)),
            tabs,
            Tab(open)?.Kind ?? ChannelTabKind.Videos,
            Sorts(open),
            preview.First("continuationCommand")["token"].NonEmpty,
            WithChannel(Feed(open), summary.Ref));
    }

    /// <summary>A tab Koda shows; Home, Shorts, Playlists, Posts and the rest aren't.</summary>
    public static ChannelTab? Tab(Json renderer)
    {
        var encoded = renderer["endpoint"]["browseEndpoint"]["params"].NonEmpty;
        if (encoded is null) return null;
        var @params = Uri.UnescapeDataString(encoded);
        var kind = renderer["title"].String switch
        {
            "Videos" => ChannelTabKind.Videos,
            "Live" => ChannelTabKind.Live,
            _ => (ChannelTabKind?)null,
        };
        return kind is { } value ? new ChannelTab(value, @params) : null;
    }

    /// <summary>Latest, Popular and Oldest: chips over the tab's grid, each a continuation that reloads it.</summary>
    public static ChannelSort[] Sorts(Json tab)
    {
        var sorts = new List<ChannelSort>();
        foreach (var chip in tab.First("chipBarViewModel")["chips"].Array)
        {
            var model = chip["chipViewModel"];
            if (model["text"].NonEmpty is { } title
                && model["tapCommand"].First("continuationCommand")["token"].NonEmpty is { } token)
            {
                sorts.Add(new ChannelSort(title, token, model["selected"].Bool == true));
            }
        }

        return [.. sorts];
    }

    /// <summary>A sort chip's answer: the grid's header (the chips, now with this one selected)
    /// and its body, each in a <c>reloadContinuationItemsCommand</c>.</summary>
    public static (VideoPage Videos, ChannelSort[] Sorts) Reloaded(Json root)
    {
        var videos = VideoPage.Empty;
        var sorts = Array.Empty<ChannelSort>();
        foreach (var action in root["onResponseReceivedActions"].Array)
        {
            var command = action["reloadContinuationItemsCommand"];
            switch (command["slot"].String)
            {
                case "RELOAD_CONTINUATION_SLOT_BODY":
                    videos = Page(command["continuationItems"]);
                    break;
                case "RELOAD_CONTINUATION_SLOT_HEADER":
                    sorts = Sorts(command);
                    break;
            }
        }

        return (videos, sorts);
    }

    /// <summary>The About panel, reached by the header description's continuation.</summary>
    public static ChannelAbout? About(Json root)
    {
        var about = root.First("aboutChannelViewModel");
        if (!about.Exists) return null;
        var links = new List<ChannelLink>();
        foreach (var item in about["links"].Array)
        {
            var link = item["channelExternalLinkViewModel"];
            var label = link["link"]["content"].NonEmpty;
            var url = UnwrapRedirect(link["link"].First("urlEndpoint")["url"].String);
            if (label is null) continue;
            url ??= Uri.TryCreate("https://" + label, UriKind.Absolute, out var fallback) ? fallback : null;
            if (url is null) continue;
            links.Add(new ChannelLink(link["title"]["content"].NonEmpty ?? label, label, url));
        }

        return new ChannelAbout(
            about["description"].NonEmpty,
            about["country"].NonEmpty,
            about["subscriberCountText"].NonEmpty,
            about["viewCountText"].NonEmpty,
            about["joinedDateText"]["content"].NonEmpty,
            about["videoCountText"].NonEmpty,
            [.. links]);
    }

    /// <summary>YouTube sends outside links through <c>youtube.com/redirect?q=&lt;the link&gt;</c>.</summary>
    public static Uri? UnwrapRedirect(string? value)
    {
        if (string.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.AbsolutePath == "/redirect")
        {
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2 && pair[0] == "q");
            var target = query is { Length: 2 } ? Uri.UnescapeDataString(query[1].Replace('+', ' ')) : null;
            return target is not null && Uri.TryCreate(target, UriKind.Absolute, out var unwrapped) ? unwrapped : null;
        }

        return uri.Scheme is "http" or "https" ? uri : null;
    }

    /// <summary>A page of comments: <c>commentEntityPayload</c>s in the framework updates, in order
    /// (replies left out), and the token at the end of the list for the next page.</summary>
    public static CommentsPage Comments(Json root)
    {
        var comments = new List<VideoComment>();
        foreach (var payload in root.Objects("commentEntityPayload"))
        {
            var properties = payload["properties"];
            if (properties["commentId"].NonEmpty is not { } id || (properties["replyLevel"].Int ?? 0) != 0) continue;
            var author = payload["author"];
            var toolbar = payload["toolbar"];
            comments.Add(new VideoComment(
                id,
                author["displayName"].String ?? "",
                author["channelId"].NonEmpty,
                author["avatarThumbnailUrl"].String is { } avatar ? Urls.Image(avatar) : null,
                properties["content"]["content"].String ?? "",
                properties["publishedTime"].String ?? "",
                toolbar["likeCountNotliked"].String ?? "",
                toolbar["replyCount"].String ?? "",
                author["isCreator"].Bool == true,
                author["isVerified"].Bool == true));
        }

        string? token = null;
        foreach (var key in new[] { "onResponseReceivedEndpoints", "onResponseReceivedActions" })
        {
            foreach (var action in root[key].Array)
            {
                var items = action["reloadContinuationItemsCommand"]["continuationItems"].Exists
                    ? action["reloadContinuationItemsCommand"]["continuationItems"]
                    : action["appendContinuationItemsAction"]["continuationItems"];
                if (Token(items) is { } found) token = found;
            }
        }

        var seen = new HashSet<string>();
        return new CommentsPage([.. comments.Where(c => seen.Add(c.Id))], token);
    }

    /// <summary>A channel's own rows leave the channel out; it's the page's.</summary>
    public static VideoPage WithChannel(VideoPage page, ChannelRef channel) =>
        page with
        {
            Videos = [.. page.Videos.Select(v => v.Channel is null ? v with { Channel = channel } : v)],
        };

    // MARK: Lists

    /// <summary>The videos in a list, in order, and the list's own continuation.</summary>
    public static VideoPage Page(Json list)
    {
        var videos = new List<Video>();
        CollectVideos(list.Raw, videos);
        var seen = new HashSet<string>();
        return new VideoPage([.. videos.Where(v => seen.Add(v.Id))], Token(list));
    }

    /// <summary>The continuation at the end of a list (wrapped lists keep it at their own end).
    /// Older lists end with a <c>continuationItemRenderer</c>, playlists now with a
    /// <c>continuationItemViewModel</c> whose token sits in a command nested in a command.</summary>
    public static string? Token(Json list)
    {
        foreach (var item in list.Array.Reverse())
        {
            if (item["continuationItemRenderer"]["continuationEndpoint"].First("continuationCommand")["token"].NonEmpty is { } token)
            {
                return token;
            }

            if (item["continuationItemViewModel"]["continuationCommand"]["innertubeCommand"]
                .First("continuationCommand")["token"].NonEmpty is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>A topic page (Music, News, Live…): its shelves of videos, each with its title, in
    /// order. Shelves with no videos (news posts, Shorts) are left out.</summary>
    public static VideoShelf[] Shelves(Json root)
    {
        var shelves = new List<VideoShelf>();
        foreach (var shelf in root.Objects("richShelfRenderer").Concat(root.Objects("shelfRenderer")))
        {
            var title = shelf["title"].Text();
            var videos = Page(shelf["contents"].Exists ? shelf["contents"] : shelf["content"]).Videos;
            if (title.Length > 0 && videos.Length > 0) shelves.Add(new VideoShelf(title, videos));
        }

        return [.. shelves];
    }

    /// <summary>A playlist's page (Watch Later, Liked videos, any other): its title and videos, in
    /// the first section's list. Most are lockups like Up Next's; Watch Later still comes as
    /// <c>playlistVideoRenderer</c> rows wrapped in a <c>playlistVideoListRenderer</c>.</summary>
    public static VideoPlaylistPage? Playlist(Json root)
    {
        var header = root.First("pageHeaderViewModel");
        var title = header["title"].Strings("content").FirstOrDefault()
                    ?? root.First("playlistMetadataRenderer")["title"].String ?? "";
        var sections = root.First("sectionListRenderer")["contents"];
        var list = sections[0]["itemSectionRenderer"]["contents"];
        // Unwrapped, so its continuation is found at the list's own end.
        if (list[0]["playlistVideoListRenderer"].Exists) list = list[0]["playlistVideoListRenderer"]["contents"];
        if (title.Length == 0 && !list.Exists) return null;
        var videos = Page(list);
        // The first page's continuation sits beside the list, not at its end.
        if (videos.Continuation is null && Token(sections) is { } token) videos = videos with { Continuation = token };
        return new VideoPlaylistPage(title, videos);
    }

    /// <summary>The channels the account subscribes to, as YouTube's own sidebar lists them (A to Z),
    /// from the guide. Signed out there are none.</summary>
    public static ChannelRef[] Subscriptions(Json root)
    {
        var channels = new List<ChannelRef>();
        foreach (var section in root.Objects("guideSubscriptionsSectionRenderer"))
        {
            foreach (var entry in section.Objects("guideEntryRenderer"))
            {
                if (entry["navigationEndpoint"]["browseEndpoint"]["browseId"].NonEmpty is not { } id
                    || !id.StartsWith("UC", StringComparison.Ordinal)) continue;
                var name = entry["formattedTitle"]["simpleText"].NonEmpty ?? entry["title"].NonEmpty;
                if (name is null) continue;
                channels.Add(new ChannelRef(id, name, entry["thumbnail"].BestThumbnail()));
            }
        }

        return [.. channels];
    }

    private static ChannelSummary[] Channels(Json list)
    {
        var seen = new HashSet<string>();
        var channels = new List<ChannelSummary>();
        foreach (var row in list.Objects("channelRenderer"))
        {
            if (ChannelOf(row) is { } channel && seen.Add(channel.Id)) channels.Add(channel);
        }

        return [.. channels];
    }

    /// <summary>Video rows in document order. Shorts (<c>shortsLockupViewModel</c>, reel shelves)
    /// are left out: they're a format of their own, not something to watch here.</summary>
    private static void CollectVideos(JsonElement node, List<Video> into)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    var child = new Json(property.Value);
                    switch (property.Name)
                    {
                        case "videoRenderer":
                            if (VideoOf(child) is { } video) into.Add(video);
                            break;
                        case "lockupViewModel":
                            if (VideoOfLockup(child) is { } lockup) into.Add(lockup);
                            break;
                        case "playlistVideoRenderer":
                            if (VideoOfPlaylistRow(child) is { } row) into.Add(row);
                            break;
                        case "reelShelfRenderer":
                        case "shortsLockupViewModel":
                        case "continuationItemRenderer":
                            break;
                        default:
                            CollectVideos(property.Value, into);
                            break;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var element in node.EnumerateArray()) CollectVideos(element, into);
                break;
        }
    }

    // MARK: Rows

    /// <summary>Search's row: <c>videoRenderer</c>.</summary>
    public static Video? VideoOf(Json row)
    {
        if (row["videoId"].NonEmpty is not { } id) return null;
        var ownerRuns = row["ownerText"].Runs();
        var owner = ownerRuns.Length > 0 ? ownerRuns[0] : row["longBylineText"].Runs().FirstOrDefault();
        var channel = owner.Exists
            ? new ChannelRef(owner["navigationEndpoint"]["browseEndpoint"]["browseId"].NonEmpty,
                owner["text"].String ?? "", row["channelThumbnailSupportedRenderers"].BestThumbnail())
            : null;
        var isLive = row["badges"].Array.Any(b =>
            b["metadataBadgeRenderer"]["style"].String == "BADGE_STYLE_TYPE_LIVE_NOW");
        return new Video(id, row["title"].Text(), channel, row["thumbnail"].BestThumbnail(),
            Rows.DurationOf(row["lengthText"].Text()),
            row["shortViewCountText"].Text().NonEmptyOrNull() ?? row["viewCountText"].Text().NonEmptyOrNull(),
            row["publishedTimeText"].Text().NonEmptyOrNull(), isLive);
    }

    /// <summary>Watch Later's row: <c>playlistVideoRenderer</c>. Views and age share one line
    /// ("150K views", " • ", "1 day ago"). Deleted and private videos stay in the list but can't be played.</summary>
    public static Video? VideoOfPlaylistRow(Json row)
    {
        if (row["videoId"].NonEmpty is not { } id || row["isPlayable"].Bool == false) return null;
        var owner = row["shortBylineText"].Runs().FirstOrDefault();
        var channel = owner.Exists
            ? new ChannelRef(owner["navigationEndpoint"]["browseEndpoint"]["browseId"].NonEmpty,
                owner["text"].String ?? "", null)
            : null;
        var info = row["videoInfo"].Runs()
            .Select(run => run["text"].String?.Trim())
            .Where(text => !string.IsNullOrEmpty(text) && text != "•")
            .ToArray();
        var isLive = row["thumbnailOverlays"].Array.Any(o =>
            o["thumbnailOverlayTimeStatusRenderer"]["style"].String == "LIVE");
        return new Video(id, row["title"].Text(), channel, row["thumbnail"].BestThumbnail(),
            Rows.DurationOf(row["lengthText"].Text()), info.FirstOrDefault(),
            isLive ? null : info.Skip(1).FirstOrDefault(), isLive);
    }

    /// <summary>The watch page's, channels' and Home's row: <c>lockupViewModel</c> of content type video.</summary>
    public static Video? VideoOfLockup(Json lockup)
    {
        if (lockup["contentType"].String != "LOCKUP_CONTENT_TYPE_VIDEO"
            || lockup["contentId"].NonEmpty is not { } id
            || !lockup["rendererContext"].First("watchEndpoint").Exists) return null;
        var meta = lockup["metadata"]["lockupMetadataViewModel"];
        var rows = meta["metadata"]["contentMetadataViewModel"]["metadataRows"].Array;
        // Two lines: the channel, then views and age. On a channel's own tabs there is one line,
        // views and age, and the channel is the page's.
        var firstParts = rows.Length > 1 ? rows[0]["metadataParts"].Array : [];
        var firstPart = firstParts.Length > 0 ? firstParts[0] : Json.Null;
        var channelName = firstPart["text"]["content"].NonEmpty;
        var channelId = firstPart["text"].First("browseEndpoint")["browseId"].NonEmpty
                        ?? meta["image"].First("browseEndpoint")["browseId"].NonEmpty;
        var channel = channelName is null
            ? null
            : new ChannelRef(channelId, channelName, BestSource(meta["image"].First("avatarViewModel")));
        var lastRow = rows.Length > 0 ? rows[^1] : Json.Null;
        var second = lastRow["metadataParts"].Array
            .Select(part => part["text"]["content"].NonEmpty).OfType<string>().ToArray();
        var badges = lockup["contentImage"].Objects("thumbnailBadgeViewModel")
            .Select(b => b["text"].NonEmpty).OfType<string>().ToArray();
        var duration = badges.Select(Rows.DurationOf).FirstOrDefault(d => d is not null);
        var watching = second.FirstOrDefault(t => t.Contains("watching", StringComparison.OrdinalIgnoreCase));
        var isLive = watching is not null || badges.Any(b => b.Equals("LIVE", StringComparison.OrdinalIgnoreCase));
        var views = second.FirstOrDefault();
        // Lockups give the bare count ("87K"); say what it counts, as the other rows do.
        if (views is not null && watching is null && char.IsDigit(views[0])
            && !views.Contains("view", StringComparison.OrdinalIgnoreCase))
        {
            views = views == "1" ? "1 view" : views + " views";
        }

        return new Video(id, meta["title"]["content"].String ?? "", channel,
            BestSource(lockup["contentImage"]), duration, views,
            isLive ? null : second.Skip(1).FirstOrDefault(), isLive);
    }

    public static ChannelSummary? ChannelOf(Json row)
    {
        if (row["channelId"].NonEmpty is not { } id) return null;
        // Search swaps them: the handle sits in subscriberCountText, the count in videoCountText.
        var texts = new[] { row["subscriberCountText"].Text(), row["videoCountText"].Text() }
            .Where(t => t.Length > 0).ToArray();
        return new ChannelSummary(id, row["title"].Text(),
            texts.FirstOrDefault(t => t.StartsWith('@')),
            texts.FirstOrDefault(t => t.Contains("subscriber", StringComparison.OrdinalIgnoreCase)),
            row["thumbnail"].BestThumbnail());
    }

    /// <summary>The widest image of the first <c>sources</c> list below a view model.</summary>
    public static Artwork? BestSource(Json node)
    {
        if (FirstSources(node.Raw) is not { Length: > 0 } list) return null;
        var best = list.Select(item => new Json(item)).MaxBy(item => item["width"].Int ?? 0);
        var url = best["url"].String is { } value ? Urls.Image(value) : null;
        return url is null ? null : new Artwork(url, best["width"].Int ?? 0, best["height"].Int ?? 0);
    }

    private static JsonElement[]? FirstSources(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name == "sources" && property.Value.ValueKind == JsonValueKind.Array
                        && property.Value.GetArrayLength() > 0)
                    {
                        return [.. property.Value.EnumerateArray()];
                    }

                    if (FirstSources(property.Value) is { } found) return found;
                }

                return null;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    if (FirstSources(item) is { } found) return found;
                }

                return null;
            default:
                return null;
        }
    }
}

internal static class VideoText
{
    /// <summary>InnerTube's <c>Text()</c> returns "" for a missing node; a page model wants null.</summary>
    public static string? NonEmptyOrNull(this string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
