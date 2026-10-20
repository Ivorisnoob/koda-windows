using System.Collections.Generic;
using System.Linq;
using Koda.Core;

namespace Koda.Helpers;

/// <summary>A cover on a shelf: what it says under it, and whether it is drawn round (artists).</summary>
public sealed record CardItem(MusicItem Item, string Title, string Subtitle, Artwork? Artwork, bool IsRound)
{
    public static CardItem From(MusicItem item) => item switch
    {
        MusicItem.SongRecord s => new(item, s.Value.Title, s.Value.ArtistLine, s.Value.Artwork, false),
        MusicItem.AlbumRecord a => new(item, a.Value.Title, AlbumLine(a.Value), a.Value.Artwork, false),
        MusicItem.ArtistRecord r => new(item, r.Value.Name, r.Value.Subtitle ?? "Artist", r.Value.Artwork, true),
        MusicItem.PlaylistRecord p => new(item, p.Value.Title, p.Value.Subtitle ?? "Playlist", p.Value.Artwork, false),
        _ => new(item, item.Title, "", item.Artwork, false),
    };

    public static string AlbumLine(AlbumSummary album)
    {
        var parts = new List<string>();
        if (album.Kind is { } kind) parts.Add(kind == ReleaseKind.Ep ? "EP" : kind.ToString());
        if (album.Year is { } year) parts.Add(year.ToString());
        if (album.ArtistLine.Length > 0) parts.Add(album.ArtistLine);
        return string.Join(" · ", parts);
    }
}

/// <summary>One shelf as drawn: its cards, and its songs as the queue a song card plays into.</summary>
public sealed record ShelfView(string Title, string? Strapline, CardItem[] Cards)
{
    public Song[] Songs { get; } = [.. Cards.Select(c => c.Item).OfType<MusicItem.SongRecord>().Select(s => s.Value)];

    public bool HasStrapline => !string.IsNullOrEmpty(Strapline);

    public static ShelfView From(Shelf shelf) => new(shelf.Title, shelf.Strapline, [.. shelf.Items.Select(CardItem.From)]);

    public static ShelfView Of(string title, IEnumerable<MusicItem> items) => new(title, null, [.. items.Select(CardItem.From)]);
}

/// <summary>A numbered track row (album and playlist tables).</summary>
public sealed record TrackRow(int Number, Song Song)
{
    public string Title => Song.Title;
    public string ArtistLine => Song.ArtistLine;
    public string AlbumTitle => Song.Album?.Title ?? "";
    public double? Duration => Song.Duration;
    public Artwork? Artwork => Song.Artwork;
    public bool IsVideo => Song.IsVideo;
}
