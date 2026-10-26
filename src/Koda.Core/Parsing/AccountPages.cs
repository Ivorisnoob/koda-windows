namespace Koda.Core.Parsing;

// Signed-in WEB_REMIX responses: the account menu and the library pages. Koda can't sign in
// to probe these here, so the paths follow Koda for Android's production parsers
// (YouTubeAccount.kt, MusicResponseParsers.kt); the rows inside them are the same renderers
// Rows reads signed out. The verdict and the signed-out answers were probed in October 2026.
public static class AccountPages
{
    // MARK: Verdict

    /// <summary>
    /// YouTube's <c>logged_in</c> for the request, from <c>responseContext.serviceTrackingParams</c>
    /// (GFEEDBACK carries it): false when a signed request was answered as anonymous. Null when
    /// the response doesn't say, which proves nothing either way.
    /// </summary>
    public static bool? SignedIn(Json root)
    {
        foreach (var service in root["responseContext"]["serviceTrackingParams"].Array)
        {
            foreach (var param in service["params"].Array)
            {
                if (param["key"].String != "logged_in") continue;
                switch (param["value"].String)
                {
                    case "1": return true;
                    case "0": return false;
                }
            }
        }
        return null;
    }

    // MARK: Account menu (account/account_menu)

    public static AccountInfo? AccountInfo(Json root)
    {
        var popup = root["actions"][0]["openPopupAction"]["popup"]["multiPageMenuRenderer"];
        var header = popup["header"]["activeAccountHeaderRenderer"].Exists
            ? popup["header"]["activeAccountHeaderRenderer"]
            : root.First("activeAccountHeaderRenderer");
        var name = header["accountName"].Text().TrimmedOrNull();
        if (name is null) return null;
        var photo = header["accountPhoto"].BestThumbnail()
            ?? header["avatar"].BestThumbnail()
            ?? header.BestThumbnail();
        // "Your channel" links the account's own channel; the menu's other links go elsewhere.
        var channel = popup["sections"].Objects("compactLinkRenderer")
            .Select(link => link["navigationEndpoint"].TargetOf()?.Id)
            .FirstOrDefault(id => id is not null && id.StartsWith("UC", StringComparison.Ordinal));
        return new AccountInfo(
            name,
            header["channelHandle"].Text().TrimmedOrNull(),
            channel,
            photo,
            root["responseContext"]["mainAppWebResponseContext"]["datasyncId"].NonEmpty);
    }

    // MARK: Library pages (FEmusic_liked_videos, _liked_playlists, _liked_albums, _library_corpus_artists)

    /// <summary>Where a library page and its continuations keep their items: a list shelf of rows
    /// or a grid of cards first, then the legacy and modern continuation shapes.</summary>
    private static Json[] LibraryContainers(Json root) =>
    [
        .. root.Objects("musicShelfRenderer"),
        .. root.Objects("gridRenderer"),
        .. root.Objects("musicShelfContinuation"),
        .. root.Objects("gridContinuation"),
        .. root.Objects("appendContinuationItemsAction"),
    ];

    public static MusicItem[] LibraryItems(Json root)
    {
        var entries = LibraryContainers(root)
            .SelectMany(container => container["contents"].Array
                .Concat(container["items"].Array)
                .Concat(container["continuationItems"].Array));
        return Pages.DedupeById(entries.Select(Rows.ItemFromEntry).OfType<MusicItem>(), item => item.Id);
    }

    /// <summary>Only the library container's own token: the section list around it can carry one
    /// of its own that leads to other shelves, not more of the library.</summary>
    public static string? LibraryContinuation(Json root)
    {
        foreach (var scope in LibraryContainers(root))
        {
            var legacy = scope["continuations"].Objects("nextContinuationData");
            if (legacy.Length > 0 && legacy[0]["continuation"].NonEmpty is { } token) return token;
            var modern = scope.Objects("continuationItemRenderer")
                .Select(item => item["continuationEndpoint"]["continuationCommand"]["token"].NonEmpty)
                .FirstOrDefault(value => value is not null);
            if (modern is not null) return modern;
        }
        return null;
    }
}
