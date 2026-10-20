using System;
using System.Collections.Generic;
using System.Linq;
using Koda.Core;
using Koda.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Koda.Helpers;

/// <summary>Where a tap on any item goes, the same from every page.</summary>
public static class Navigator
{
    public static void Go(Type page, object? parameter = null) => App.Shell?.NavigateTo(page, parameter);

    /// <summary>A song plays inside <paramref name="queue"/>; a collection opens its page.</summary>
    public static void Open(MusicItem item, IReadOnlyList<Song>? queue = null)
    {
        switch (item)
        {
            case MusicItem.SongRecord song:
                _ = PlaybackService.Instance.PlayNowAsync(song.Value, queue is { Count: > 0 } ? queue : [song.Value]);
                break;
            case MusicItem.AlbumRecord album:
                Go(typeof(Pages.AlbumPage), album.Value.Id);
                break;
            case MusicItem.ArtistRecord artist:
                Go(typeof(Pages.ArtistPage), artist.Value.Id);
                break;
            case MusicItem.PlaylistRecord playlist:
                Go(typeof(Pages.PlaylistPage), playlist.Value.Id);
                break;
        }
    }
}

/// <summary>
/// The right-click menu of a song, the Windows form of the long-press menu:
/// play now, play next, start radio, like, and the way to its album and artists.
/// </summary>
public static class SongMenu
{
    /// <summary>Hook a list whose items are songs (or rows/cards holding one); <paramref name="queue"/>
    /// is what "Play now" plays into.</summary>
    public static void Attach(UIElement list, Func<IReadOnlyList<Song>> queue) =>
        list.ContextRequested += (sender, args) =>
        {
            if (args.OriginalSource is not FrameworkElement { DataContext: var context } || SongOf(context) is not { } song)
            {
                return;
            }

            var menu = Build(song, queue());
            var host = (FrameworkElement)sender;
            if (args.TryGetPosition(host, out var position))
            {
                menu.ShowAt(host, position);
            }
            else
            {
                menu.ShowAt(host);
            }

            args.Handled = true;
        };

    public static Song? SongOf(object? context) => context switch
    {
        Song song => song,
        TrackRow row => row.Song,
        Playback.QueueItem queued => queued.Song,
        CardItem { Item: MusicItem.SongRecord s } => s.Value,
        _ => null,
    };

    public static MenuFlyout Build(Song song, IReadOnlyList<Song> queue)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Play now", "", () => _ = PlaybackService.Instance.PlayNowAsync(song, queue.Count > 0 ? queue : [song])));
        menu.Items.Add(Item("Add to queue", "", () => PlaybackService.Instance.AddToQueue([song])));
        menu.Items.Add(Item("Play next","", () => _ = PlaybackService.Instance.PlayNextAsync(song)));
        menu.Items.Add(Item("Start radio", "", () => _ = PlaybackService.Instance.StartRadioAsync(song)));
        menu.Items.Add(new MenuFlyoutSeparator());
        var liked = App.Library.IsLiked(song);
        menu.Items.Add(Item(liked ? "Remove from Liked" : "Add to Liked", liked ? "" : "", () => App.Library.ToggleLike(song)));
        if (song.Album is { } album || song.Artists.Any(a => a.Id is not null))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        if (song.Album is { } albumRef)
        {
            menu.Items.Add(Item("Go to album", "", () => Navigator.Go(typeof(Pages.AlbumPage), albumRef.Id)));
        }

        foreach (var artist in song.Artists.Where(a => a.Id is not null).Take(3))
        {
            menu.Items.Add(Item($"Go to {artist.Name}", "", () => Navigator.Go(typeof(Pages.ArtistPage), artist.Id)));
        }

        return menu;
    }

    private static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }
}
