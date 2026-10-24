using System;
using System.Collections.Generic;
using System.Linq;
using Koda.Core;
using Koda.Interop;
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
            case MusicItem.UserPlaylistRecord playlist:
                Go(typeof(Pages.UserPlaylistPage), playlist.Value.Id);
                break;
        }
    }
}

/// <summary>
/// The right-click menu of a song, the Windows form of the long-press menu:
/// play now, play next, start radio, like, add to a playlist, and the way to its album and artists.
/// </summary>
public static class SongMenu
{
    /// <summary>Hook a list whose items are songs (or rows/cards holding one); <paramref name="queue"/>
    /// is what "Play now" plays into. <paramref name="extra"/> adds the page's own actions at the foot.</summary>
    public static void Attach(UIElement list, Func<IReadOnlyList<Song>> queue,
        Func<Song, IEnumerable<MenuFlyoutItemBase>>? extra = null) =>
        list.ContextRequested += (sender, args) =>
        {
            if (args.OriginalSource is not FrameworkElement { DataContext: var context } || SongOf(context) is not { } song)
            {
                return;
            }

            var menu = Build(song, queue(), extra?.Invoke(song));
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

    public static MenuFlyout Build(Song song, IReadOnlyList<Song> queue, IEnumerable<MenuFlyoutItemBase>? extra = null)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Play now", "\uE768", () => _ = PlaybackService.Instance.PlayNowAsync(song, queue.Count > 0 ? queue : [song])));
        menu.Items.Add(Item("Add to queue", "\uE71C", () => PlaybackService.Instance.AddToQueue([song])));
        menu.Items.Add(Item("Play next", "\uE8A5", () => _ = PlaybackService.Instance.PlayNextAsync(song)));
        menu.Items.Add(Item("Start radio", "\uE8FE", () => _ = PlaybackService.Instance.StartRadioAsync(song)));
        menu.Items.Add(new MenuFlyoutSeparator());
        var liked = App.Library.IsLiked(song);
        menu.Items.Add(Item(liked ? "Remove from Liked" : "Add to Liked", liked ? "\uEB52" : "\uEB51", () => App.Library.ToggleLike(song)));
        menu.Items.Add(PlaylistSubmenu(song));
        menu.Items.Add(Item("Share…", "\uE72D", () =>
        {
            if (App.Shell is { } shell)
            {
                ShareLink.Show(shell.Handle, song);
            }
        }));
        if (extra is not null)
        {
            var actions = extra.ToArray();
            if (actions.Length > 0)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                foreach (var action in actions)
                {
                    menu.Items.Add(action);
                }
            }
        }

        if (song.Album is { } album || song.Artists.Any(a => a.Id is not null))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        if (song.Album is { } albumRef)
        {
            menu.Items.Add(Item("Go to album", "\uE93C", () => Navigator.Go(typeof(Pages.AlbumPage), albumRef.Id)));
        }

        foreach (var artist in song.Artists.Where(a => a.Id is not null).Take(3))
        {
            menu.Items.Add(Item($"Go to {artist.Name}", "\uE77B", () => Navigator.Go(typeof(Pages.ArtistPage), artist.Id)));
        }

        return menu;
    }

    /// <summary>The listener's playlists, as Music's own Add to Playlist submenu, with New Playlist first.</summary>
    public static MenuFlyoutSubItem PlaylistSubmenu(Song song) => PlaylistSubmenu([song]);

    public static MenuFlyoutSubItem PlaylistSubmenu(IReadOnlyList<Song> songs)
    {
        var submenu = new MenuFlyoutSubItem { Text = "Add to playlist", Icon = new FontIcon { Glyph = "\uE710" } };
        submenu.Items.Add(Item(songs.Count == 1 ? "New playlist…" : "New playlist…", "\uE710",
            () => _ = Dialogs.CreatePlaylistAsync(songs)));
        var playlists = App.Library.UserPlaylists;
        if (playlists.Length == 0)
        {
            return submenu;
        }

        submenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var playlist in playlists)
        {
            var known = playlist.Songs.Select(x => x.Id).ToHashSet();
            var containsAll = songs.All(x => known.Contains(x.Id));
            var entry = Item(playlist.Name, containsAll ? "\uE73E" : "\uE8FD", () =>
            {
                var added = App.Library.AddToPlaylist(songs, playlist.Id);
                App.Shell?.Note(added == 0
                    ? $"Those songs are already in {playlist.Name}."
                    : added == 1
                        ? $"Added to {playlist.Name}."
                        : $"Added {added} songs to {playlist.Name}.");
            });
            entry.IsEnabled = !containsAll;
            submenu.Items.Add(entry);
        }

        return submenu;
    }

    public static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }
}
