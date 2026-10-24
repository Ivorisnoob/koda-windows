using System;
using System.Collections.Generic;
using System.Linq;
using Koda.Core;
using Koda.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Koda.Pages;

/// <summary>
/// Saved albums, playlists or artists as one grid, newest first by default and
/// sortable by title or artist. Playlists gathers the ones made here first, then
/// the ones saved from YouTube Music; New Playlist sits in the header.
/// </summary>
public sealed partial class LibraryGridPage : Page
{
    private string _kind = "albums";
    private CardItem[] _cards = [];

    public LibraryGridPage()
    {
        InitializeComponent();
        App.Library.Changed += OnLibraryChanged;
        Unloaded += (_, _) => App.Library.Changed -= OnLibraryChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (args.Parameter is string kind)
        {
            _kind = kind;
        }

        HeroTitle.Text = _kind switch
        {
            "playlists" => "Playlists",
            "artists" => "Artists",
            _ => "Albums",
        };
        NewPlaylistButton.Visibility = _kind == "playlists" ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        var library = App.Library;
        var sort = App.Settings.LibrarySort;
        _cards = _kind switch
        {
            "playlists" => [.. SortedUserPlaylists(sort).Select(CardItem.From), .. SortedPlaylists(sort).Select(CardItem.From)],
            "artists" => [.. SortedArtists(sort).Select(CardItem.From)],
            _ => [.. SortedAlbums(sort).Select(CardItem.From)],
        };

        Cards.ItemsSource = _cards;
        SortButton.Content = SortTitle(sort);
        var menu = new MenuFlyout();
        foreach (var value in Enum.GetValues<LibrarySort>())
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = SortTitle(value),
                GroupName = "LibrarySort",
                IsChecked = sort == value,
            };
            item.Click += (_, _) =>
            {
                App.Settings.Update(s => s.LibrarySort = value);
                Render();
            };
            menu.Items.Add(item);
        }

        SortButton.Flyout = menu;

        HeroDetail.Text = _kind switch
        {
            "playlists" => library.UserPlaylists.Length switch
            {
                0 => library.Playlists.Length == 0 ? "" : $"{library.Playlists.Length} saved",
                1 when library.Playlists.Length == 0 => "1 yours",
                var own => library.Playlists.Length == 0
                    ? $"{own} yours"
                    : $"{own} yours · {library.Playlists.Length} saved",
            },
            "artists" => library.Artists.Length == 1 ? "1 artist" : $"{library.Artists.Length} artists",
            _ => library.Albums.Length == 1 ? "1 album" : $"{library.Albums.Length} albums",
        };

        if (_cards.Length > 0)
        {
            Cards.Visibility = Visibility.Visible;
            Empty.Visibility = Visibility.Collapsed;
            return;
        }

        Cards.Visibility = Visibility.Collapsed;
        switch (_kind)
        {
            case "playlists":
                Empty.Show("No playlists yet",
                    "Make one here, or save a playlist from YouTube Music with its heart.",
                    "\uE8FD", "New playlist", () => _ = Dialogs.CreatePlaylistAsync([]));
                break;
            case "artists":
                Empty.Show("No artists yet", "Follow an artist from their page and they gather here.", "\uE77B",
                    "Search music", () => App.Shell?.SearchFor(string.Empty));
                break;
            default:
                Empty.Show("No albums yet", "Save an album with the heart beside Play.", "\uE93C",
                    "Search music", () => App.Shell?.SearchFor(string.Empty));
                break;
        }
    }

    private static AlbumSummary[] SortedAlbums(LibrarySort sort)
    {
        var albums = App.Library.Albums;
        return sort switch
        {
            LibrarySort.Title => [.. albums.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)],
            LibrarySort.Artist => [.. albums
                .OrderBy(a => a.ArtistLine, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)],
            _ => albums,
        };
    }

    private static PlaylistSummary[] SortedPlaylists(LibrarySort sort)
    {
        var playlists = App.Library.Playlists;
        return sort == LibrarySort.Recent
            ? playlists
            : [.. playlists.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static UserPlaylist[] SortedUserPlaylists(LibrarySort sort)
    {
        var playlists = App.Library.UserPlaylists;
        return sort == LibrarySort.Recent
            ? [.. playlists.OrderByDescending(p => p.Modified)]
            : [.. playlists.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static ArtistSummary[] SortedArtists(LibrarySort sort)
    {
        var artists = App.Library.Artists;
        return sort == LibrarySort.Recent
            ? artists
            : [.. artists.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static string SortTitle(LibrarySort sort) => sort switch
    {
        LibrarySort.Title => "Title",
        LibrarySort.Artist => "Artist",
        _ => "Recent",
    };

    private void OnCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CardItem card)
        {
            Navigator.Open(card.Item);
        }
    }

    private void OnNewPlaylistClick(object sender, RoutedEventArgs e) => _ = Dialogs.CreatePlaylistAsync([]);
}
