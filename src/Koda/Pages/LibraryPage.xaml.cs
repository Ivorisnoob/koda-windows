using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Koda.Pages;

/// <summary>
/// The library: liked songs, what was played, and the playlists,
/// albums and artists saved. Every tab is a dense catalog; a tab with nothing in it says
/// how to fill it, and a library with nothing at all opens on the Android import.
/// </summary>
public sealed partial class LibraryPage : Page
{
    /// <summary>A playlist made here, drawn like a collection row; it plays when tapped.</summary>
    private sealed record UserPlaylistRow(UserPlaylist Playlist)
    {
        public string Title => Playlist.Name;
        public string Subtitle => $"Your playlist · {Playlist.Songs.Length} songs";
        public Artwork? Artwork => Playlist.Songs.FirstOrDefault(s => s.Artwork is not null)?.Artwork;
    }

    private string _tab = "liked";
    private Song[] _songs = [];

    public LibraryPage()
    {
        InitializeComponent();
        SongMenu.Attach(Items, () => _songs);
        App.Library.Changed += OnLibraryChanged;
        Unloaded += (_, _) => App.Library.Changed -= OnLibraryChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        Render();
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(Render);

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _tab = sender.SelectedItem?.Tag as string ?? "liked";
        Render();
    }

    private void Render()
    {
        var library = App.Library;
        var nothing = library.Liked.Length == 0 && library.Recent.Length == 0 && library.Playlists.Length == 0
            && library.Albums.Length == 0 && library.Artists.Length == 0 && library.UserPlaylists.Length == 0;
        Welcome.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;
        Tabs.Visibility = nothing ? Visibility.Collapsed : Visibility.Visible;
        if (nothing)
        {
            Items.Visibility = Visibility.Collapsed;
            Empty.Visibility = Visibility.Collapsed;
            PlayLikedButton.Visibility = ShuffleButton.Visibility = Visibility.Collapsed;
            return;
        }

        var songTemplate = (DataTemplate)Application.Current.Resources["SongRowTemplate"];
        var rowTemplate = (DataTemplate)Application.Current.Resources["CardRowTemplate"];
        object[] items;
        (string Title, string Message, string Glyph) empty;
        switch (_tab)
        {
            case "recent":
                _songs = library.Recent;
                items = [.. _songs];
                Items.ItemTemplate = songTemplate;
                empty = ("Nothing played yet", "Songs you play show up here, newest first.", "");
                break;
            case "playlists":
                _songs = [];
                items = [.. library.UserPlaylists.Select(p => (object)new UserPlaylistRow(p)),
                    .. library.Playlists.Select(p => (object)CardItem.From(new MusicItem.PlaylistRecord(p)))];
                Items.ItemTemplate = rowTemplate;
                empty = ("No playlists yet", "Save a playlist with its heart button, or import yours from Koda for Android.", "");
                break;
            case "albums":
                _songs = [];
                items = [.. library.Albums.Select(a => (object)CardItem.From(new MusicItem.AlbumRecord(a)))];
                Items.ItemTemplate = rowTemplate;
                empty = ("No albums yet", "Save an album with the heart beside Play.", "");
                break;
            case "artists":
                _songs = [];
                items = [.. library.Artists.Select(a => (object)CardItem.From(new MusicItem.ArtistRecord(a)))];
                Items.ItemTemplate = rowTemplate;
                empty = ("No artists yet", "Follow an artist from their page.", "");
                break;
            default:
                _songs = library.Liked;
                items = [.. _songs];
                Items.ItemTemplate = songTemplate;
                empty = ("No liked songs yet", "Tap the heart in the player bar, or right-click any song.", "");
                break;
        }

        Items.ItemsSource = items;
        Items.Visibility = items.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Length == 0)
        {
            Empty.Show(empty.Title, empty.Message, empty.Glyph);
        }
        else
        {
            Empty.Visibility = Visibility.Collapsed;
        }

        var playable = _songs.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlayLikedButton.Visibility = ShuffleButton.Visibility = playable;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        switch (e.ClickedItem)
        {
            case Song song:
                _ = PlaybackService.Instance.PlayNowAsync(song, _songs);
                break;
            case CardItem card:
                Navigator.Open(card.Item);
                break;
            case UserPlaylistRow row when row.Playlist.Songs.Length > 0:
                _ = PlaybackService.Instance.PlayNowAsync(row.Playlist.Songs[0], row.Playlist.Songs);
                break;
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_songs.Length > 0) _ = PlaybackService.Instance.PlayNowAsync(_songs[0], _songs);
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e) => _ = PlaybackService.Instance.ShuffleAsync(_songs);

    private void OnSearchClick(object sender, RoutedEventArgs e) => Navigator.Go(typeof(SearchPage));

    private void OnImportClick(object sender, RoutedEventArgs e) => _ = AndroidImport.RunAsync();
}
