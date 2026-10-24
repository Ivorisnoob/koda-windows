using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using EnginePlaylist = Koda.Core.PlaylistPage;

namespace Koda.Pages;

/// <summary>A playlist: hero header and the numbered track table. Pages of tracks after the
/// first load by themselves as the list nears its end, with placeholder rows meanwhile.</summary>
public sealed partial class PlaylistPage : Page
{
    private string _id = "";
    private EnginePlaylist? _playlist;
    private readonly ObservableCollection<TrackRow> _rows = [];
    private readonly List<Song> _songs = [];
    private string? _continuation;
    private bool _loadingMore;
    private ScrollViewer? _scroller;

    public PlaylistPage()
    {
        InitializeComponent();
        Tracks.ItemsSource = _rows;
        SongMenu.Attach(Tracks, () => _songs);
        App.Library.Changed += OnLibraryChanged;
        Unloaded += (_, _) => App.Library.Changed -= OnLibraryChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (args.Parameter is string id && id.Length > 0)
        {
            _id = id;
            _ = LoadAsync();
        }
        else if (args.Parameter is PlaylistRequest request)
        {
            _id = request.Id;
            _fallback = request;
            _ = LoadAsync();
        }
    }

    /// <summary>A playlist to open with what to show if YouTube serves it without a page
    /// header, as it does for OLAK5uy_ audio playlists (docs\parsing.md).</summary>
    public sealed record PlaylistRequest(string Id, string Title, string? Subtitle, Artwork? Artwork);

    private PlaylistRequest? _fallback;

    private async Task<EnginePlaylist> FetchAsync()
    {
        try
        {
            return await EngineHost.Music.PlaylistAsync(_id);
        }
        catch (MusicException error) when (error.Kind == MusicException.Reason.BadResponse && _id.StartsWith("OLAK5uy_"))
        {
            var songs = await EngineHost.Music.PlaylistSongsAsync("VL" + _id, limit: 500);
            var title = _fallback?.Title ?? "Songs";
            return new EnginePlaylist(new PlaylistSummary(_id, title, null, _fallback?.Artwork ?? songs.FirstOrDefault()?.Artwork),
                _fallback?.Subtitle, $"{songs.Length} songs", null, songs, null);
        }
    }

    private async Task LoadAsync()
    {
        Error.Visibility = Visibility.Collapsed;
        Tracks.Visibility = Visibility.Collapsed;
        var skeleton = new StackPanel { Spacing = 28 };
        skeleton.Children.Add(Skeletons.Hero());
        skeleton.Children.Add(Skeletons.Rows(10, numbered: true));
        SkeletonHost.Content = skeleton;
        try
        {
            var page = await FetchAsync();
            _playlist = page;
            _rows.Clear();
            _songs.Clear();
            Append(page.Songs);
            _continuation = page.Continuation;
            ShowHeader(page);
            Tracks.Visibility = Visibility.Visible;
            if (_songs.Count == 0 && _continuation is null)
            {
                Error.Show("This playlist is empty", "", "");
            }
        }
        catch (Exception error)
        {
            Error.ShowError(error, () => _ = LoadAsync());
        }
        finally
        {
            SkeletonHost.Content = null;
        }
    }

    private void ShowHeader(EnginePlaylist page)
    {
        HeroTitle.Text = page.Playlist.Title;
        HeroAuthor.Text = page.Author ?? "";
        HeroAuthor.Visibility = page.Author is null ? Visibility.Collapsed : Visibility.Visible;
        HeroMeta.Text = page.DetailLine ?? "";
        Art.SetSource(HeroArt, page.Playlist.Artwork);
        Description.Text = page.Description ?? "";
        Description.Visibility = page.Description is null ? Visibility.Collapsed : Visibility.Visible;
        DownloadButton.Title = page.Playlist.Title;
        DownloadButton.Songs = _songs.ToArray();
        // A long playlist arrives in pages; Download fetches the rest first.
        DownloadButton.AllSongs = AllSongsAsync;
        UpdateSave();
    }

    private void Append(IEnumerable<Song> songs)
    {
        foreach (var song in songs)
        {
            _songs.Add(song);
            _rows.Add(new TrackRow(_songs.Count, song));
        }
    }

    private void OnTracksLoaded(object sender, RoutedEventArgs e)
    {
        _scroller ??= FindScroller(Tracks);
        if (_scroller is not null)
        {
            _scroller.ViewChanged += (_, _) =>
            {
                if (_scroller.VerticalOffset > _scroller.ScrollableHeight - 900)
                {
                    _ = LoadMoreAsync();
                }
            };
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_loadingMore || _continuation is not { } token)
        {
            return;
        }

        _loadingMore = true;
        MoreError.Visibility = Visibility.Collapsed;
        MoreSkeleton.Content = Skeletons.Rows(4, numbered: true);
        try
        {
            var more = await EngineHost.Music.PlaylistMoreAsync(token);
            Append(more.Songs);
            _continuation = more.Continuation;
        }
        catch (Exception)
        {
            MoreError.Visibility = Visibility.Visible;
        }
        finally
        {
            MoreSkeleton.Content = null;
            _loadingMore = false;
        }
    }

    private void OnMoreRetryClick(object sender, RoutedEventArgs e) => _ = LoadMoreAsync();

    /// <summary>Every track, for Play and Shuffle: the pages not yet seen are fetched first.</summary>
    private async Task<IReadOnlyList<Song>> AllSongsAsync()
    {
        while (_continuation is not null && _songs.Count < 1000)
        {
            var before = _songs.Count;
            await LoadMoreAsync();
            if (_songs.Count == before) break;
        }

        return _songs.ToArray();
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_songs.Count == 0) return;
        _ = PlaybackService.Instance.PlayNowAsync(_songs[0], _songs.ToArray());
        // The queue grows to the whole playlist once the rest arrives.
        var known = _songs.Count;
        var all = await AllSongsAsync();
        if (all.Count > known && PlaybackService.Instance.Queue.Items.Count == known)
        {
            // Appended after the known tail, in order, without a note.
            PlaybackService.Instance.AddToQueue([.. all.Skip(known)], announce: false);
        }
    }

    private async void OnShuffleClick(object sender, RoutedEventArgs e) =>
        await PlaybackService.Instance.ShuffleAsync(await AllSongsAsync());

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackRow row)
        {
            _ = PlaybackService.Instance.PlayNowAsync(row.Song, _songs.ToArray());
        }
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(UpdateSave);

    private void UpdateSave()
    {
        var saved = _playlist is { } page && App.Library.IsSavedPlaylist(page.Playlist.Id);
        SaveIcon.Glyph = saved ? "" : "";
        SaveIcon.Foreground = (Brush)Application.Current.Resources[saved ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"];
        ToolTipService.SetToolTip(SaveButton, saved ? "Remove from Library" : "Save to Library");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_playlist is { } page)
        {
            App.Library.ToggleSave(page.Playlist);
        }
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller) return scroller;
            if (FindScroller(child) is { } deeper) return deeper;
        }

        return null;
    }
}
