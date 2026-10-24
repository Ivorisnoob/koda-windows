using System.Collections.Generic;
using System.Linq;
using Koda.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        MusicItem.UserPlaylistRecord u => new(item, u.Value.Name,
            $"Your playlist · {(u.Value.Songs.Length == 1 ? "1 song" : $"{u.Value.Songs.Length} songs")}", u.Value.Songs.FirstOrDefault(s => s.Artwork is not null)?.Artwork, false),
        _ => new(item, item.Title, "", item.Artwork, false),
    };

    public static CardItem From(AlbumSummary album) => From(new MusicItem.AlbumRecord(album));

    public static CardItem From(ArtistSummary artist) => From(new MusicItem.ArtistRecord(artist));

    public static CardItem From(PlaylistSummary playlist) => From(new MusicItem.PlaylistRecord(playlist));

    public static CardItem From(UserPlaylist playlist) => From(new MusicItem.UserPlaylistRecord(playlist));

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

/// <summary>A day in History: a heading row, then one row per play under it.</summary>
public sealed record HistoryDayRow(DateTimeOffset Day, int Plays)
{
    public string Label => Format.Day(Day);
    public string Detail => Format.Plays(Plays);
}

/// <summary>One play in History, with the day's songs so playing from here queues the day.</summary>
public sealed record HistoryPlayRow(PlayEvent Event, Song[] DaySongs, int Index)
{
    public Song Song => Event.Song;
    public string Title => Song.Title;
    public string ArtistLine => Song.ArtistLine;
    public string Subtitle => $"{Song.ArtistLine} · {Format.Time(Event.Date)}";
    public Artwork? Artwork => Song.Artwork;
    public bool IsVideo => Song.IsVideo;
    public double? Duration => Song.Duration;
}

/// <summary>A song in the Stats' all-time list, with its rank and play count.</summary>
public sealed record RankedSongRow(int Rank, SongStats Stats)
{
    public Song Song => Stats.Song;
    public string RankText => Rank.ToString();
    public string Title => Song.Title;
    public string ArtistLine => Song.ArtistLine;
    public string Subtitle => $"{Song.ArtistLine} · {Format.Plays(Stats.Plays)}";
    public Artwork? Artwork => Song.Artwork;
    public bool IsVideo => Song.IsVideo;
    public double? Duration => Song.Duration;
}

/// <summary>A lead artist in the Stats' all-time list, with its rank and totals.</summary>
public sealed record TopArtistRow(int Rank, string Name, ArtistRef? Ref, Artwork? Artwork, double Seconds, int Plays)
{
    public string RankText => Rank.ToString();
    public string Detail => $"{Format.Listening(Seconds)} · {Format.Plays(Plays)}";
    public bool HasArtist => Ref?.Id is not null;
}

/// <summary>History's two row shapes in one flat list: a day's heading, and a play.</summary>
public sealed partial class HistoryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Day { get; set; }
    public DataTemplate? Play { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) =>
        (item is HistoryDayRow ? Day : Play)!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>A mood or genre on Browse: its name on a quiet wash of its own colour.</summary>
public sealed record MoodCard(Mood Mood, string Title, Microsoft.UI.Xaml.Media.Brush Background);

