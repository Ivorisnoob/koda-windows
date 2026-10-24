using System;
using System.Collections.Generic;
using System.Linq;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace Koda.Pages;

/// <summary>
/// A collection of songs from the library: Liked Songs, Recently Played, Downloads or
/// Ready Offline. One cover, the count and listening time, Play and Shuffle, a sort for
/// Liked Songs, and the songs themselves as dense rows.
/// </summary>
public sealed partial class SongsPage : Page
{
    private string _source = "liked";
    private Song[] _songs = [];

    public SongsPage()
    {
        InitializeComponent();
        SongMenu.Attach(Songs, () => _songs, ExtraActions);
        App.Library.Changed += OnLibraryChanged;
        App.AudioStore.Changed += OnLibraryChanged;
        Unloaded += (_, _) =>
        {
            App.Library.Changed -= OnLibraryChanged;
            App.AudioStore.Changed -= OnLibraryChanged;
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (args.Parameter is string source)
        {
            _source = source;
        }

        HeroTitle.Text = _source switch
        {
            "recent" => "Recently Played",
            "downloads" => "Downloads",
            "offline" => "Ready Offline",
            _ => "Liked Songs",
        };
        var (glyph, tint) = _source switch
        {
            "recent" => ("\uE823", Color.FromArgb(255, 232, 137, 12)),
            "downloads" => ("\uE896", Color.FromArgb(255, 43, 124, 211)),
            "offline" => ("\uE709", Color.FromArgb(255, 31, 146, 84)),
            _ => ("\uEB52", Color.FromArgb(255, 227, 76, 122)),
        };
        HeroGlyph.Glyph = glyph;
        HeroCover.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = tint, Offset = 0 },
                new GradientStop { Color = Color.FromArgb(255, (byte)(tint.R * 0.68), (byte)(tint.G * 0.68), (byte)(tint.B * 0.68)), Offset = 1 },
            },
        };

        Render();
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        _songs = _source switch
        {
            "recent" => App.Library.Recent,
            "downloads" => App.AudioStore.Downloads.Select(e => e.Song).OfType<Song>().ToArray(),
            "offline" => App.AudioStore.CompleteSongs,
            _ => SortedLiked(),
        };

        Songs.ItemsSource = _songs;
        HeroDetail.Text = _songs.Length == 0 ? "" : Format.SongList(_songs);

        var any = _songs.Length > 0;
        PlayButton.Visibility = ShuffleButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = any && _source == "recent" ? Visibility.Visible : Visibility.Collapsed;

        var sortable = _source == "liked" && _songs.Length > 1;
        SortButton.Visibility = sortable ? Visibility.Visible : Visibility.Collapsed;
        if (sortable)
        {
            SortButton.Content = SortTitle(App.Settings.SongSort);
            var menu = new MenuFlyout();
            foreach (var sort in Enum.GetValues<SongSort>())
            {
                var item = new RadioMenuFlyoutItem
                {
                    Text = SortTitle(sort),
                    GroupName = "SongSort",
                    IsChecked = App.Settings.SongSort == sort,
                };
                item.Click += (_, _) =>
                {
                    App.Settings.Update(s => s.SongSort = sort);
                    Render();
                };
                menu.Items.Add(item);
            }

            SortButton.Flyout = menu;
        }

        if (any)
        {
            Songs.Visibility = Visibility.Visible;
            Empty.Visibility = Visibility.Collapsed;
            return;
        }

        Songs.Visibility = Visibility.Collapsed;
        var (title, message, glyph) = _source switch
        {
            "recent" => ("Nothing played yet", "Songs you play appear here, newest first.", "\uE823"),
            "downloads" => ("No downloads yet", "Songs you download stay on this PC, ready without a connection.", "\uE896"),
            "offline" => ("Nothing ready offline",
                "Songs you download, and songs you play all the way through, are kept on this PC and play without a connection.", "\uE709"),
            _ => ("No liked songs yet", "Songs you like appear here. Right-click any song, or use the heart in the player bar.", "\uEB52"),
        };
        Empty.Show(title, message, glyph);
    }

    private Song[] SortedLiked()
    {
        var liked = App.Library.Liked;
        return App.Settings.SongSort switch
        {
            SongSort.Title => [.. liked.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)],
            SongSort.Artist => [.. liked.OrderBy(s => s.ArtistLine, StringComparer.CurrentCultureIgnoreCase)],
            // Most played first; OrderByDescending is stable, so ties keep the order they were liked in.
            SongSort.MostPlayed => [.. liked.OrderByDescending(App.Library.PlaysOf)],
            _ => liked,
        };
    }

    private static string SortTitle(SongSort sort) => sort switch
    {
        SongSort.Title => "Title",
        SongSort.Artist => "Artist",
        SongSort.MostPlayed => "Most played",
        _ => "Recent",
    };

    private IEnumerable<MenuFlyoutItemBase> ExtraActions(Song song)
    {
        if (_source == "downloads")
        {
            yield return SongMenu.Item("Remove download", "\uE74D", () =>
            {
                App.AudioStore.RemoveDownloads([song.Id]);
                App.Shell?.Note($"Removed “{song.Title}” from downloads.");
            });
        }
    }

    private void OnSongClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Song song)
        {
            _ = PlaybackService.Instance.PlayNowAsync(song, _songs);
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_songs.Length > 0)
        {
            _ = PlaybackService.Instance.PlayNowAsync(_songs[0], _songs);
        }
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e) => _ = PlaybackService.Instance.ShuffleAsync(_songs);

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.ConfirmAsync("Clear your listening history?",
                "Recently Played and Quick picks start over. Liked songs and stats stay.", "Clear History"))
        {
            App.Library.ClearHistory();
        }
    }
}
