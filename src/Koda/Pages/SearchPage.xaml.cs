using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Helpers;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Koda.Pages;

/// <summary>
/// Search results. The query comes from the title bar's field.
/// "All" puts the top result beside its songs, then a shelf per kind; a filter tab
/// asks YouTube for that kind alone and lists it as a catalog. The newest query
/// always wins: a slow answer to an older one is dropped, never shown over it.
/// </summary>
public sealed partial class SearchPage : Page
{
    private string _query = "";
    private string _filter = "all";
    private int _generation;
    private SearchResults? _all;
    private MusicItem? _top;
    private IReadOnlyList<Song> _filterSongs = [];

    public SearchPage()
    {
        InitializeComponent();
        SongMenu.Attach(TopSongsList, () => _all?.Songs ?? []);
        SongMenu.Attach(VideosList, () => _all?.Videos ?? []);
        SongMenu.Attach(FilterList, () => _filterSongs);
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (args.Parameter is string query && query.Length > 0)
        {
            Search(query);
        }
        else if (_query.Length == 0)
        {
            ShowHistory();
        }
    }

    public void Search(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return;
        }

        App.Library.RecordSearch(query);
        App.Shell?.SetSearchText(query);
        _query = query;
        _all = null;
        Heading.Text = $"Results for “{query}”";
        Filters.Visibility = Visibility.Visible;
        if (_filter != "all")
        {
            Filters.SelectedItem = Filters.Items[0]; // runs the search through OnFilterChanged
        }
        else
        {
            _ = RunAsync();
        }
    }

    private void ShowHistory()
    {
        Heading.Text = "Search";
        Filters.Visibility = Visibility.Collapsed;
        AllScroll.Visibility = Visibility.Collapsed;
        FilterList.Visibility = Visibility.Collapsed;
        Error.Visibility = Visibility.Collapsed;
        var history = App.Library.Searches;
        HistoryChips.ItemsSource = history.ToList();
        HistoryHeader.Visibility = history.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Hint.Visibility = history.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        HistoryScroll.Visibility = Visibility.Visible;
        _ = LoadMoodsAsync();
    }

    private async Task LoadMoodsAsync()
    {
        if (MoodTiles.ItemsSource is not null)
        {
            return;
        }

        try
        {
            var explore = await EngineHost.Music.ExploreAsync();
            // Music only: YouTube's podcast entries have no place here.
            var moods = explore.Moods
                .Where(m => m.Title.Length > 0 && !m.Title.Contains("Podcast", StringComparison.OrdinalIgnoreCase))
                .ToList();
            MoodTiles.ItemsSource = moods;
            MoodsHeader.Visibility = moods.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception)
        {
            // The tiles are a way in, not the page; search works without them.
        }
    }

    private void OnMoodClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Mood mood })
        {
            Navigator.Go(typeof(MoodPage), mood);
        }
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string query })
        {
            Search(query);
        }
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        App.Library.ClearSearches();
        ShowHistory();
    }

    private void OnFilterChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _filter = sender.SelectedItem?.Tag as string ?? "all";
        if (_query.Length > 0)
        {
            _ = RunAsync();
        }
    }

    private void OnShowAllSongs(object sender, RoutedEventArgs e) =>
        Filters.SelectedItem = Filters.Items.First(i => i.Tag as string == "songs");

    private async Task RunAsync()
    {
        var generation = ++_generation;
        var filter = _filter;
        HistoryScroll.Visibility = Visibility.Collapsed;
        Error.Visibility = Visibility.Collapsed;
        AllScroll.Visibility = Visibility.Collapsed;
        FilterList.Visibility = Visibility.Collapsed;

        if (filter == "all" && _all is { } cached)
        {
            ShowAll(cached);
            return;
        }

        SkeletonHost.Content = filter == "all" ? AllSkeleton() : Skeletons.Rows(12, thumbs: true);
        try
        {
            var searchFilter = filter switch
            {
                "songs" => SearchFilter.Songs,
                "albums" => SearchFilter.Albums,
                "artists" => SearchFilter.Artists,
                "playlists" => SearchFilter.Playlists,
                _ => SearchFilter.All,
            };
            var results = await EngineHost.Music.SearchAsync(_query, searchFilter);
            if (generation != _generation)
            {
                return; // a newer query or tab took over
            }

            SkeletonHost.Content = null;
            if (filter == "all")
            {
                _all = results;
                ShowAll(results);
            }
            else
            {
                ShowFilter(filter, results);
            }
        }
        catch (Exception error)
        {
            if (generation != _generation)
            {
                return;
            }

            SkeletonHost.Content = null;
            Error.ShowError(error, () => _ = RunAsync());
        }
    }

    private static UIElement AllSkeleton()
    {
        var panel = new StackPanel { Spacing = 28 };
        var top = new Grid { ColumnSpacing = 24 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        var card = Skeletons.Breathe(new StackPanel { Spacing = 12 });
        card.Children.Add(Skeletons.Block(100, 18));
        card.Children.Add(Skeletons.Block(112, 112, 12));
        card.Children.Add(Skeletons.Block(180, 18));
        card.Children.Add(Skeletons.Block(120, 12));
        top.Children.Add(card);
        var rows = Skeletons.Rows(4);
        Grid.SetColumn(rows, 1);
        top.Children.Add(rows);
        panel.Children.Add(top);
        panel.Children.Add(Skeletons.Shelves(2));
        return panel;
    }

    private void ShowAll(SearchResults results)
    {
        if (results.IsEmpty)
        {
            Error.Show($"No results for “{_query}”", "Check the spelling, or try fewer or other words.", "");
            return;
        }

        _top = results.Top;
        TopSection.Visibility = _top is null ? Visibility.Collapsed : Visibility.Visible;
        if (_top is { } top)
        {
            var card = CardItem.From(top);
            TopTitle.Text = card.Title;
            TopDetail.Text = top switch
            {
                MusicItem.SongRecord s => $"Song · {s.Value.ArtistLine}",
                MusicItem.AlbumRecord => card.Subtitle,
                MusicItem.ArtistRecord a => a.Value.Subtitle is { } sub ? $"Artist · {sub}" : "Artist",
                MusicItem.PlaylistRecord p => p.Value.Subtitle is { } sub ? $"Playlist · {sub}" : "Playlist",
                _ => "",
            };
            TopArtFrame.CornerRadius = new CornerRadius(top is MusicItem.ArtistRecord ? 56 : 12);
            Art.SetSource(TopArt, card.Artwork);
        }

        // Beside an artist card YouTube offers that artist's songs; otherwise the plain song results.
        var songs = (results.TopSongs.Length > 0 ? results.TopSongs : results.Songs).Take(4).ToArray();
        TopSongsList.ItemsSource = songs;
        TopSongsSection.Visibility = songs.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllSongsLink.Visibility = results.Songs.Length > songs.Length ? Visibility.Visible : Visibility.Collapsed;

        var shelves = new List<ShelfView>();
        if (results.Albums.Length > 0) shelves.Add(ShelfView.Of("Albums", results.Albums.Select(a => (MusicItem)new MusicItem.AlbumRecord(a))));
        if (results.Artists.Length > 0) shelves.Add(ShelfView.Of("Artists", results.Artists.Select(a => (MusicItem)new MusicItem.ArtistRecord(a))));
        if (results.Playlists.Length > 0) shelves.Add(ShelfView.Of("Playlists", results.Playlists.Select(p => (MusicItem)new MusicItem.PlaylistRecord(p))));
        KindShelves.Shelves = shelves;

        VideosList.ItemsSource = results.Videos.Take(4).ToArray();
        VideosSection.Visibility = results.Videos.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllScroll.Visibility = Visibility.Visible;
        AllScroll.ChangeView(null, 0, null, true);
    }

    private void ShowFilter(string filter, SearchResults results)
    {
        object[] items;
        if (filter == "songs")
        {
            _filterSongs = results.Songs;
            FilterList.ItemTemplate = (DataTemplate)Application.Current.Resources["SongRowTemplate"];
            items = [.. results.Songs];
        }
        else
        {
            _filterSongs = [];
            FilterList.ItemTemplate = (DataTemplate)Application.Current.Resources["CardRowTemplate"];
            IEnumerable<MusicItem> kind = filter switch
            {
                "albums" => results.Albums.Select(a => (MusicItem)new MusicItem.AlbumRecord(a)),
                "artists" => results.Artists.Select(a => (MusicItem)new MusicItem.ArtistRecord(a)),
                _ => results.Playlists.Select(p => (MusicItem)new MusicItem.PlaylistRecord(p)),
            };
            items = [.. kind.Select(CardItem.From)];
        }

        if (items.Length == 0)
        {
            Error.Show($"No {filter} for “{_query}”", "Try the All tab, or other words.", "");
            return;
        }

        FilterList.ItemsSource = items;
        FilterList.Visibility = Visibility.Visible;
    }

    private void OnTopAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Side by side when there is room, stacked when narrow.
        var narrow = e.NewSize.Width < 760;
        Grid.SetColumn(TopSongsSection, narrow ? 0 : 1);
        Grid.SetRow(TopSongsSection, narrow ? 1 : 0);
        Grid.SetColumnSpan(TopSongsSection, narrow ? 2 : 1);
        Grid.SetColumnSpan(TopSection, narrow ? 2 : 1);
        TopSongsSection.Margin = new Thickness(0, narrow ? 24 : 0, 0, 0);
    }

    private void OnTopCardClick(object sender, RoutedEventArgs e)
    {
        if (_top is { } top)
        {
            Navigator.Open(top, _all?.TopSongs.Length > 0 ? _all.TopSongs : null);
        }
    }

    private void OnSongClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Song song)
        {
            var queue = sender == (object)VideosList ? _all?.Videos : (IReadOnlyList<Song>?)TopSongsList.ItemsSource;
            _ = Playback.PlaybackService.Instance.PlayNowAsync(song, queue ?? [song]);
        }
    }

    private void OnFilterItemClick(object sender, ItemClickEventArgs e)
    {
        switch (e.ClickedItem)
        {
            case Song song:
                _ = Playback.PlaybackService.Instance.PlayNowAsync(song, _filterSongs);
                break;
            case CardItem card:
                Navigator.Open(card.Item);
                break;
        }
    }
}
