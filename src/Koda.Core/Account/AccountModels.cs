using System.Text.Json;
using System.Text.Json.Serialization;

namespace Koda.Core;

/// <summary>Who a signed-in session belongs to, from YouTube Music's account menu.</summary>
/// <param name="Handle">"@handle", when the account has a channel.</param>
/// <param name="ChannelId">The account's own <c>UC…</c> channel, when the menu links it.</param>
/// <param name="DatasyncId">YouTube's id for the account
/// (<c>responseContext.mainAppWebResponseContext.datasyncId</c>), kept verbatim. The same account
/// signed in twice has the same one.</param>
public sealed record AccountInfo(
    string Name,
    string? Handle = null,
    string? ChannelId = null,
    Artwork? Photo = null,
    string? DatasyncId = null);

/// <summary>What a signed-in account holds, as one sync read it.</summary>
public sealed record AccountLibrary(
    Song[] Liked,
    AlbumSummary[] Albums,
    PlaylistSummary[] Playlists,
    ArtistSummary[] Artists)
{
    public static readonly AccountLibrary Empty = new([], [], [], []);
}

/// <summary>What a change is about: liked songs, saved albums, playlists and subscribed artists.</summary>
public enum AccountChangeKind
{
    Liked,
    Album,
    Playlist,
    Artist,
}

/// <summary>
/// Something the listener did to their library that the account should hear about. Only the id
/// and the direction are kept: a write needs nothing more, and the mirror stays small.
/// </summary>
public sealed record AccountChange(AccountChangeKind Kind, string Id, bool IsOn)
{
    /// <summary>The item the change is about, whichever way it went.</summary>
    [JsonIgnore]
    public string TargetKey => $"{(int)Kind}:{Id}";
}

/// <summary>
/// A library's memory of its account between syncs: what the account held last time, and the
/// listener's own changes that haven't reached it yet. Kept in the data root.
///
/// It is what lets a sync tell "removed on the account" (drop it here too) from "only ever
/// here" (keep it), and keeps a change made offline from being undone by the next sync.
/// </summary>
public sealed class AccountMirror
{
    public HashSet<string> Liked { get; set; } = [];
    public HashSet<string> Albums { get; set; } = [];
    public HashSet<string> Playlists { get; set; } = [];
    public HashSet<string> Artists { get; set; } = [];

    /// <summary>Oldest first, one per item: a newer change to the same item replaces the older.</summary>
    public List<AccountChange> Pending { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Liked.Count == 0 && Albums.Count == 0 && Playlists.Count == 0 && Artists.Count == 0;

    public void Queue(AccountChange change)
    {
        Pending.RemoveAll(c => c.TargetKey == change.TargetKey);
        Pending.Add(change);
    }

    /// <summary>The change reached the account; a newer one to the same item stays queued.</summary>
    public void Sent(AccountChange change) => Pending.RemoveAll(c => c == change);

    /// <summary>Ids with an unsent change, by whether the listener added or removed them.</summary>
    public HashSet<string> Unsent(AccountChangeKind kind, bool adding) =>
        Pending.Where(c => c.Kind == kind && c.IsOn == adding).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Ids the account held last time and no longer does, less changes still waiting to
    /// be sent: an unlike made offline isn't dropped, a like made offline isn't either.</summary>
    public HashSet<string> Gone(AccountChangeKind kind, IEnumerable<string> current)
    {
        var previous = kind switch
        {
            AccountChangeKind.Liked => Liked,
            AccountChangeKind.Album => Albums,
            AccountChangeKind.Playlist => Playlists,
            _ => Artists,
        };
        var gone = previous.ToHashSet(StringComparer.Ordinal);
        gone.ExceptWith(current);
        gone.ExceptWith(Unsent(kind, adding: true));
        return gone;
    }

    public HashSet<string> Ids(AccountChangeKind kind) => kind switch
    {
        AccountChangeKind.Liked => [.. Liked],
        AccountChangeKind.Album => [.. Albums],
        AccountChangeKind.Playlist => [.. Playlists],
        _ => [.. Artists],
    };

    public void Remember(AccountLibrary account)
    {
        Liked = account.Liked.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        Albums = account.Albums.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        Playlists = account.Playlists.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        Artists = account.Artists.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
    }

    public AccountMirror Copy() => new()
    {
        Liked = new HashSet<string>(Liked, StringComparer.Ordinal),
        Albums = new HashSet<string>(Albums, StringComparer.Ordinal),
        Playlists = new HashSet<string>(Playlists, StringComparer.Ordinal),
        Artists = new HashSet<string>(Artists, StringComparer.Ordinal),
        Pending = [.. Pending],
    };

    /// <summary>
    /// Bring in what the account holds and take out what was removed from it since the last sync,
    /// then remember what it holds now. Something only ever kept here stays, and so does anything
    /// with a change still waiting to be sent.
    /// </summary>
    public void Reconcile(Library library, AccountLibrary account)
    {
        var removedLiked = Unsent(AccountChangeKind.Liked, adding: false);
        var removedAlbums = Unsent(AccountChangeKind.Album, adding: false);
        var removedPlaylists = Unsent(AccountChangeKind.Playlist, adding: false);
        var removedArtists = Unsent(AccountChangeKind.Artist, adding: false);
        var goneLiked = Gone(AccountChangeKind.Liked, account.Liked.Select(s => s.Id));
        var goneAlbums = Gone(AccountChangeKind.Album, account.Albums.Select(a => a.Id));
        var gonePlaylists = Gone(AccountChangeKind.Playlist, account.Playlists.Select(p => p.Id));
        var goneArtists = Gone(AccountChangeKind.Artist, account.Artists.Select(a => a.Id));

        library.MergeAccount(
            account.Liked.Where(s => !removedLiked.Contains(s.Id)).ToArray(),
            account.Albums.Where(a => !removedAlbums.Contains(a.Id)).ToArray(),
            account.Playlists.Where(p => !removedPlaylists.Contains(p.Id)).ToArray(),
            account.Artists.Where(a => !removedArtists.Contains(a.Id)).ToArray());

        if (goneLiked.Count > 0 || goneAlbums.Count > 0 || gonePlaylists.Count > 0 || goneArtists.Count > 0)
        {
            var keepLiked = library.Liked.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            keepLiked.ExceptWith(goneLiked);
            var keepAlbums = library.Albums.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            keepAlbums.ExceptWith(goneAlbums);
            var keepPlaylists = library.Playlists.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            keepPlaylists.ExceptWith(gonePlaylists);
            var keepArtists = library.Artists.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            keepArtists.ExceptWith(goneArtists);
            library.DropMissing(keepLiked, keepAlbums, keepPlaylists, keepArtists);
        }

        Remember(account);
    }
}

/// <summary>How the account files are written: the library's shape, one JSON object per file.</summary>
public static class AccountJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<AccountChangeKind>() },
    };
}
