using System.Text.Json.Nodes;
using Koda.Core.InnerTube;
using Koda.Core.Parsing;

namespace Koda.Core;

/// <summary>
/// The signed-in account's own pages and writes, all on music.youtube.com as WEB_REMIX and signed
/// with <see cref="YouTubeMusic.Accounts"/>' active session. Each throws
/// <see cref="MusicException"/> with <see cref="MusicException.Reason.SignedOut"/> when there is no
/// session or when YouTube answers as signed out. Shapes and endpoints follow Koda for Android
/// (YouTubeAccount.kt, MusicPlaylists.kt, PlaylistEditing.kt, WatchPage.kt).
/// </summary>
public sealed partial class YouTubeMusic
{
    // MARK: Who

    /// <summary>Name, handle, photo and datasyncId of the active session's account.</summary>
    public async Task<AccountInfo> AccountInfoAsync(CancellationToken cancellation = default)
    {
        var root = await _api.MusicAsync("account/account_menu", new JsonObject(), cancellation, Signing.Required);
        return AccountPages.AccountInfo(root) ?? throw MusicException.BadResponse(200);
    }

    // MARK: Library

    /// <summary>Liked songs, newest first, every page up to <paramref name="limit"/>. All or
    /// nothing: a page that fails throws rather than returning part of the list, which a sync
    /// would read as unlikes.</summary>
    public async Task<Song[]> LikedSongsAsync(int limit = 5000, CancellationToken cancellation = default)
    {
        var items = await LibraryAsync("FEmusic_liked_videos", limit, cancellation);
        return [.. items.OfType<MusicItem.SongRecord>().Select(record => record.Value)];
    }

    /// <summary>Playlists in the library: the account's own and ones it saved. Liked Music and
    /// Episodes for Later are left out; liked songs come from <see cref="LikedSongsAsync"/>.</summary>
    public async Task<PlaylistSummary[]> LibraryPlaylistsAsync(int limit = 5000, CancellationToken cancellation = default)
    {
        var items = await LibraryAsync("FEmusic_liked_playlists", limit, cancellation);
        return [.. items.OfType<MusicItem.PlaylistRecord>().Select(record => record.Value)
            .Where(playlist => playlist.Id is not ("LM" or "SE"))];
    }

    public async Task<AlbumSummary[]> LibraryAlbumsAsync(int limit = 5000, CancellationToken cancellation = default)
    {
        var items = await LibraryAsync("FEmusic_liked_albums", limit, cancellation);
        return [.. items.OfType<MusicItem.AlbumRecord>().Select(record => record.Value)];
    }

    /// <summary>Artists the account subscribes to.</summary>
    public async Task<ArtistSummary[]> LibraryArtistsAsync(int limit = 5000, CancellationToken cancellation = default)
    {
        var items = await LibraryAsync("FEmusic_library_corpus_artists", limit, cancellation);
        return [.. items.OfType<MusicItem.ArtistRecord>().Select(record => record.Value)];
    }

    /// <summary>A library page and its continuations.</summary>
    private async Task<MusicItem[]> LibraryAsync(string browseId, int limit, CancellationToken cancellation)
    {
        var root = await _api.MusicAsync("browse", new JsonObject { ["browseId"] = browseId }, cancellation, Signing.Required);
        var items = AccountPages.LibraryItems(root).ToList();
        var seen = new HashSet<string>();
        // 100 rows a page: 80 pages is past any library, so a chain that long isn't ending.
        while (items.Count < limit && seen.Count < 80
            && AccountPages.LibraryContinuation(root) is { } token && seen.Add(token))
        {
            cancellation.ThrowIfCancellationRequested();
            root = await _api.MusicAsync("browse", new JsonObject { ["continuation"] = token }, cancellation, Signing.Required);
            items.AddRange(AccountPages.LibraryItems(root));
        }
        return [.. Pages.DedupeById(items, item => item.Id).Take(limit)];
    }

    // MARK: Writes

    /// <summary>Like or unlike a song; liked songs are the account's Liked Music.</summary>
    public async Task SetLikedAsync(string videoId, bool liked, CancellationToken cancellation = default)
    {
        await _api.MusicAsync(liked ? "like/like" : "like/removelike",
            new JsonObject { ["target"] = new JsonObject { ["videoId"] = videoId } }, cancellation, Signing.Required);
    }

    /// <summary>Save an album or playlist to the library, or remove it. An album is saved by its
    /// audio playlist (<c>OLAK5uy…</c>, <see cref="AlbumPage.AudioPlaylistId"/>), not its
    /// <c>MPRE…</c> id.</summary>
    public async Task SetSavedAsync(string playlistId, bool saved, CancellationToken cancellation = default)
    {
        var id = playlistId.StartsWith("VL", StringComparison.Ordinal) ? playlistId[2..] : playlistId;
        await _api.MusicAsync(saved ? "like/like" : "like/removelike",
            new JsonObject { ["target"] = new JsonObject { ["playlistId"] = id } }, cancellation, Signing.Required);
    }

    /// <summary>Subscribe to an artist's channel (<c>UC…</c>), or unsubscribe.</summary>
    public async Task SetSubscribedAsync(string channelId, bool subscribed, CancellationToken cancellation = default)
    {
        await _api.MusicAsync(subscribed ? "subscription/subscribe" : "subscription/unsubscribe",
            new JsonObject { ["channelIds"] = new JsonArray { channelId } }, cancellation, Signing.Required);
    }

    // MARK: History

    /// <summary>File one play in the active account's history. Throws
    /// <see cref="MusicException.Reason.SignedOut"/> with no session or when YouTube answers as
    /// signed out: a dead session still gets tracking URLs and a 204 on the ping, and the play goes
    /// nowhere, so <c>logged_in</c> is the only honest check.</summary>
    public async Task ReportPlaybackAsync(string videoId, CancellationToken cancellation = default)
    {
        if (_accounts.Active is null) throw MusicException.SignedOut();
        var cpn = HistoryPing.Cpn();
        var sent = await _signatures.CurrentAsync(cancellation: cancellation);
        var root = await _api.MusicAsync("player", HistoryPing.PlayerPayload(videoId, cpn, sent),
            cancellation, Signing.Required);
        if (!root["playbackTracking"].Exists
            && await _signatures.RefreshAfterRejectionAsync(sent, cancellation: cancellation) is { } retry)
        {
            // A signatureTimestamp YouTube no longer takes looks exactly like an unavailable video.
            root = await _api.MusicAsync("player", HistoryPing.PlayerPayload(videoId, cpn, retry),
                cancellation, Signing.Required);
        }
        if (root["playbackTracking"]["videostatsPlaybackUrl"]["baseUrl"].NonEmpty is not { } baseUrl
            || HistoryPing.TrackingUrl(baseUrl, cpn) is not { } url)
        {
            throw MusicException.BadResponse(200);
        }
        await _api.PingAsync(url, $"https://music.youtube.com/watch?v={videoId}", cancellation: cancellation);
    }
}
