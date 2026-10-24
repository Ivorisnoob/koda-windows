using System.Text.RegularExpressions;

namespace Koda.Core;

/// <summary>An image as InnerTube serves it, with the size it was served at.</summary>
public record Artwork(Uri Url, int Width = 0, int Height = 0)
{
    /// <summary>A video frame (16:9) rather than square cover art; drawn cropped to a square.</summary>
    public bool IsLandscape => Width > 0 && Height > 0 && (double)Width / Height > 1.2;

    /// <summary>
    /// The same image asked for at <paramref name="pixels"/> on its longest side.
    /// Only Google's image hosts take a size, and the size directive lives
    /// after the last <c>=</c>: the opaque token before it can itself contain
    /// <c>s&lt;digits&gt;</c>, so rewriting the whole URL corrupts it into a 404.
    /// (The Swift port named this <c>url(pixels:)</c>; C# cannot overload a
    /// property with a method of the same name.)
    /// </summary>
    public Uri Resized(int pixels)
    {
        var s = Url.ToString();
        if (!(s.Contains("googleusercontent.com") || s.Contains("ggpht.com"))) return Url;
        var separator = s.LastIndexOf('=');
        if (separator < 0) return Url;
        var head = s[..separator];
        var directives = s[separator..];
        directives = Regex.Replace(directives, @"w\d+-h\d+", $"w{pixels}-h{pixels}");
        directives = Regex.Replace(directives, @"(?<![a-z])s\d+", $"s{pixels}");
        return Uri.TryCreate(head + directives, UriKind.Absolute, out var rewritten) ? rewritten : Url;
    }
}

/// <summary>An InnerTube error, on the video itself or the network between Koda and YouTube.</summary>
public class MusicException : Exception
{
    public Reason Kind { get; }
    public int Status { get; }

    public enum Reason
    {
        /// <summary>No connection, a timeout, or a body that could not be read.</summary>
        Network,
        /// <summary>YouTube answered, but not with what was asked for.</summary>
        BadResponse,
        /// <summary>A verdict on the video itself: removed, private, region-blocked.</summary>
        Unplayable,
        /// <summary>The bot check refused even a fresh identity.</summary>
        BotCheck,
        /// <summary>YouTube is throttling this client; try later.</summary>
        RateLimited,
    }

    private MusicException(Reason reason, string? detail, int status = 0) : base(detail is null ? reason.ToString() : $"{reason}: {detail}") =>
        (Kind, Status) = (reason, status);

    public static MusicException Network(string? detail = null) => new(Reason.Network, detail);
    public static MusicException BadResponse(int status) => new(Reason.BadResponse, $"HTTP {status}", status);
    public static MusicException Unplayable(string? reason) => new(Reason.Unplayable, reason);
    public static MusicException BotCheck() => new(Reason.BotCheck, null);
    public static MusicException RateLimited() => new(Reason.RateLimited, null);
}

/// <summary>
/// A <c>UC…</c> channel id ref; <see cref="Id"/> null when the response named the artist without a link.
/// </summary>
public record ArtistRef(string? Id, string Name);

/// <summary>An <c>MPRE…</c> browse id ref.</summary>
public record AlbumRef(string Id, string Title);

/// <summary>
/// A track or music video row. Equality is by value, artists included, as the
/// Swift struct's Hashable is: a record compares an array field by reference,
/// so two parses of the same row would otherwise never be equal.
/// </summary>
public record Song(
    string Id,
    string Title,
    string ArtistLine,
    ArtistRef[] Artists,
    AlbumRef? Album,
    double? Duration,
    Artwork? Artwork,
    bool IsVideo,
    int? Year)
{
    public Song With(AlbumRef? album = null, int? year = null, Artwork? artwork = null,
        string? id = null, bool? isVideo = null, double? duration = null) => new(
        id ?? Id, Title, ArtistLine, Artists, album ?? Album, duration ?? Duration, artwork ?? Artwork,
        isVideo ?? IsVideo, year ?? Year);

    public virtual bool Equals(Song? other) =>
        other is not null && Id == other.Id && Title == other.Title && ArtistLine == other.ArtistLine
        && Artists.AsSpan().SequenceEqual(other.Artists) && Album == other.Album && Duration == other.Duration
        && Artwork == other.Artwork && IsVideo == other.IsVideo && Year == other.Year;

    public override int GetHashCode() => HashCode.Combine(Id, Title, ArtistLine, Artists.Length, Album, Duration, IsVideo, Year);
}

/// <summary>Written as the Swift raw values ("Album", "EP", "Single") so library backups move between platforms.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ReleaseKind>))]
public enum ReleaseKind
{
    Album,
    [System.Text.Json.Serialization.JsonStringEnumMemberName("EP")]
    Ep,
    Single,
}

/// <summary>An album, EP or single link. Value equality, artists included (see <see cref="Song"/>).</summary>
public record AlbumSummary(
    string Id,
    string Title,
    string ArtistLine,
    ArtistRef[] Artists,
    int? Year,
    ReleaseKind? Kind,
    Artwork? Artwork)
{
    public virtual bool Equals(AlbumSummary? other) =>
        other is not null && Id == other.Id && Title == other.Title && ArtistLine == other.ArtistLine
        && Artists.AsSpan().SequenceEqual(other.Artists) && Year == other.Year && Kind == other.Kind
        && Artwork == other.Artwork;

    public override int GetHashCode() => HashCode.Combine(Id, Title, ArtistLine, Artists.Length, Year, Kind);
}

/// <param name="Subtitle">"238M monthly audience", "2.29M subscribers".</param>
public record ArtistSummary(string Id, string Name, string? Subtitle, Artwork? Artwork);

public record PlaylistSummary(string Id, string Title, string? Subtitle, Artwork? Artwork);

/// <summary>Any result kind; the tag names the payload, so equality and dedupe stay whole-row.</summary>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SongRecord), "song")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(AlbumRecord), "album")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ArtistRecord), "artist")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(PlaylistRecord), "playlist")]
public abstract record MusicItem
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract Artwork? Artwork { get; }

    public sealed record SongRecord(Song Value) : MusicItem
    {
        public override string Id => "s:" + Value.Id;
        public override string Title => Value.Title;
        public override Artwork? Artwork => Value.Artwork;
    }

    public sealed record AlbumRecord(AlbumSummary Value) : MusicItem
    {
        public override string Id => "a:" + Value.Id;
        public override string Title => Value.Title;
        public override Artwork? Artwork => Value.Artwork;
    }

    public sealed record ArtistRecord(ArtistSummary Value) : MusicItem
    {
        public override string Id => "r:" + Value.Id;
        public override string Title => Value.Name;
        public override Artwork? Artwork => Value.Artwork;
    }

    public sealed record PlaylistRecord(PlaylistSummary Value) : MusicItem
    {
        public override string Id => "p:" + Value.Id;
        public override string Title => Value.Title;
        public override Artwork? Artwork => Value.Artwork;
    }

    /// <summary>A playlist the listener made here, drawn like any other collection.</summary>
    public sealed record UserPlaylistRecord(UserPlaylist Value) : MusicItem
    {
        public override string Id => "u:" + Value.Id;
        public override string Title => Value.Name;
        public override Artwork? Artwork => Value.Songs.FirstOrDefault(s => s.Artwork is not null)?.Artwork;
    }
}

/// <summary>A home or explore carousel.</summary>
public record Shelf(string Title, string? Strapline, MusicItem[] Items)
{
    public string Id => Title + ":" + (Items.FirstOrDefault()?.Id ?? "");
}

/// <summary>A mood or genre chip; browsing it opens a page of shelves.</summary>
public record Mood(string Title, string BrowseId, string? Params)
{
    public string Id => BrowseId + (Params ?? "");
}

public record ShelfPage(Shelf[] Shelves, Mood[] Moods);

public record AlbumPage(
    AlbumSummary Album,
    string? Description,
    string? DetailLine,
    Song[] Songs,
    AlbumSummary[] OtherVersions,
    string? AudioPlaylistId);

public record ArtistPage(
    ArtistSummary Artist,
    string? Description,
    Artwork? Banner,
    Song[] TopSongs,
    /// <summary>The playlist behind the Top songs shelf's "See all".</summary>
    string? AllSongsPlaylistId,
    AlbumSummary[] Albums,
    AlbumSummary[] Singles,
    PlaylistSummary[] FeaturedOn,
    ArtistSummary[] Similar);

public record PlaylistPage(
    PlaylistSummary Playlist,
    string? Author,
    string? DetailLine,
    string? Description,
    Song[] Songs,
    string? Continuation);

public record SearchResults(
    MusicItem? Top,
    Song[] TopSongs,
    Song[] Songs,
    AlbumSummary[] Albums,
    ArtistSummary[] Artists,
    PlaylistSummary[] Playlists,
    Song[] Videos)
{
    public static readonly SearchResults Empty = new(null, [], [], [], [], [], []);

    public bool IsEmpty =>
        Top is null && Songs.Length == 0 && Albums.Length == 0 && Artists.Length == 0
        && Playlists.Length == 0 && Videos.Length == 0;
}

/// <summary>The YouTube Music search chips. <see cref="Params"/> values are pinned to the fixtures. [verified October 2026]</summary>
public enum SearchFilter
{
    All,
    Songs,
    Albums,
    Artists,
    Playlists,
}

public static class SearchFilterExtensions
{
    public static string? Params(this SearchFilter filter) => filter switch
    {
        SearchFilter.All => null,
        SearchFilter.Songs => "EgWKAQIIAWoSEAUQCRADEAoQBBAQEBUQDhAR",
        SearchFilter.Albums => "EgWKAQIYAWoSEAUQCRADEAoQBBAQEBUQDhAR",
        SearchFilter.Artists => "EgWKAQIgAWoSEAUQCRADEAoQBBAQEBUQDhAR",
        SearchFilter.Playlists => "EgeKAQQoADgBahIQBRAJEAMQChAEEBAQFRAOEBE%3D",
        _ => null,
    };
}

/// <summary>A resolved playable audio stream.</summary>
public record ResolvedStream(
    string VideoId,
    Uri Url,
    /// <summary>The User-Agent of the client that resolved the URL; googlevideo binds a URL to its client.</summary>
    string UserAgent,
    int Itag,
    long? ContentLength,
    double? Duration,
    /// <summary>How far the track sits above YouTube's -14 LUFS target, when the format says.</summary>
    double? LoudnessDb,
    /// <summary>When googlevideo stops honouring the URL (its <c>expire</c> parameter).</summary>
    DateTimeOffset? ExpiresAt)
{
    public bool IsFresh(DateTimeOffset? now = null) =>
        ExpiresAt is not { } e || (now ?? DateTimeOffset.UtcNow).AddMinutes(10) < e;
}

/// <summary>The songs of one <c>/next</c> queue page, with its follow-up token if any.</summary>
public record QueuePage(Song[] Songs, string? Continuation);
