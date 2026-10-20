using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using EngineArtist = Koda.Core.ArtistPage;

namespace Koda.Pages;

/// <summary>An artist: banner hero (Play, Radio, Follow), top songs, releases as shelves,
/// the playlists they're featured on, similar artists, and the About text.</summary>
public sealed partial class ArtistPage : Page
{
    private string _id = "";
    private EngineArtist? _artist;
    private Song[] _top = [];

    public ArtistPage()
    {
        InitializeComponent();
        SongMenu.Attach(TopSongs, () => _top);
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
    }

    private async Task LoadAsync()
    {
        Error.Visibility = Visibility.Collapsed;
        Scroll.Visibility = Visibility.Collapsed;
        var skeleton = new StackPanel { Spacing = 28 };
        var banner = Skeletons.Breathe(new Border
        {
            Height = 260,
            CornerRadius = new CornerRadius(20),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
        });
        skeleton.Children.Add(banner);
        skeleton.Children.Add(Skeletons.Rows(5, numbered: true));
        skeleton.Children.Add(Skeletons.Shelves(1));
        SkeletonHost.Content = skeleton;
        try
        {
            _artist = await EngineHost.Music.ArtistAsync(_id);
            Show(_artist);
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

    private void Show(EngineArtist page)
    {
        NameText.Text = page.Artist.Name;
        AudienceText.Text = page.Artist.Subtitle ?? "";
        Art.SetSource(Backdrop, page.Banner);
        Art.SetSource(Portrait, page.Banner);

        _top = page.TopSongs;
        TopSongs.ItemsSource = page.TopSongs.Select((s, i) => new TrackRow(i + 1, s)).ToArray();
        TopSection.Visibility = _top.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AllSongsLink.Visibility = page.AllSongsPlaylistId is null ? Visibility.Collapsed : Visibility.Visible;
        PlayButton.IsEnabled = _top.Length > 0;
        RadioButton.IsEnabled = _top.Length > 0;

        var shelves = new List<ShelfView>();
        if (page.Albums.Length > 0) shelves.Add(ShelfView.Of("Albums", page.Albums.Select(a => (MusicItem)new MusicItem.AlbumRecord(a))));
        if (page.Singles.Length > 0) shelves.Add(ShelfView.Of("Singles & EPs", page.Singles.Select(a => (MusicItem)new MusicItem.AlbumRecord(a))));
        if (page.FeaturedOn.Length > 0) shelves.Add(ShelfView.Of("Featured on", page.FeaturedOn.Select(p => (MusicItem)new MusicItem.PlaylistRecord(p))));
        if (page.Similar.Length > 0) shelves.Add(ShelfView.Of("Fans might also like", page.Similar.Select(a => (MusicItem)new MusicItem.ArtistRecord(a))));
        Releases.Shelves = shelves;

        AboutText.Text = page.Description ?? "";
        AboutSection.Visibility = page.Description is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateSave();
        Scroll.Visibility = Visibility.Visible;
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(UpdateSave);

    private void UpdateSave() =>
        SaveText.Text = _artist is { } page && App.Library.IsSavedArtist(page.Artist.Id) ? "Following" : "Follow";

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_artist is { } page)
        {
            App.Library.ToggleSave(page.Artist);
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_top.Length > 0) _ = PlaybackService.Instance.PlayNowAsync(_top[0], _top);
    }

    private void OnRadioClick(object sender, RoutedEventArgs e)
    {
        if (_top.Length > 0) _ = PlaybackService.Instance.StartRadioAsync(_top[0]);
    }

    private void OnAllSongsClick(object sender, RoutedEventArgs e)
    {
        if (_artist is { AllSongsPlaylistId: { } playlist } page)
        {
            Navigator.Go(typeof(PlaylistPage),
                new PlaylistPage.PlaylistRequest(playlist, $"{page.Artist.Name}: all songs", page.Artist.Name, page.Banner));
        }
    }

    private void OnSongClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackRow row)
        {
            _ = PlaybackService.Instance.PlayNowAsync(row.Song, _top);
        }
    }
}
