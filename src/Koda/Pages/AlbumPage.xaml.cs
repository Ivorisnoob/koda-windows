using System;
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
using EngineAlbum = Koda.Core.AlbumPage;

namespace Koda.Pages;

/// <summary>An album: the hero header (square art, the artist as a link, Play, Shuffle, Save), the
/// numbered track table, and the album's other versions below it.</summary>
public sealed partial class AlbumPage : Page
{
    private string _id = "";
    private EngineAlbum? _album;
    private Song[] _songs = [];

    public AlbumPage()
    {
        InitializeComponent();
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
            _album = await EngineHost.Music.AlbumAsync(_id);
            Show(_album);
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

    private void Show(EngineAlbum page)
    {
        var album = page.Album;
        HeroTitle.Text = album.Title;
        ArtistLink.Content = album.ArtistLine;
        ArtistLink.IsEnabled = album.Artists.Any(a => a.Id is not null);
        var meta = new[] { CardItem.AlbumLine(album with { ArtistLine = "" }), page.DetailLine }
            .Where(s => !string.IsNullOrEmpty(s));
        HeroMeta.Text = string.Join(" · ", meta);
        Art.SetSource(HeroArt, album.Artwork);
        Description.Text = page.Description ?? "";
        Description.Visibility = page.Description is null ? Visibility.Collapsed : Visibility.Visible;

        _songs = page.Songs;
        DownloadButton.Title = album.Title;
        DownloadButton.Songs = page.Songs;
        Tracks.ItemsSource = page.Songs.Select((s, i) => new TrackRow(i + 1, s)).ToArray();
        Versions.Shelves = page.OtherVersions.Length > 0
            ? [ShelfView.Of("Other versions", page.OtherVersions.Select(a => (MusicItem)new MusicItem.AlbumRecord(a)))]
            : [];
        UpdateSave();
        Tracks.Visibility = Visibility.Visible;
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(UpdateSave);

    private void UpdateSave()
    {
        var saved = _album is { } page && App.Library.IsSavedAlbum(page.Album.Id);
        SaveIcon.Glyph = saved ? "" : "";
        SaveIcon.Foreground = (Brush)Application.Current.Resources[saved ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"];
        ToolTipService.SetToolTip(SaveButton, saved ? "Remove from Library" : "Save to Library");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_album is { } page)
        {
            App.Library.ToggleSave(page.Album);
        }
    }

    private void OnArtistClick(object sender, RoutedEventArgs e)
    {
        if (_album?.Album.Artists.FirstOrDefault(a => a.Id is not null) is { Id: { } id })
        {
            Navigator.Go(typeof(ArtistPage), id);
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_songs.Length > 0) _ = PlaybackService.Instance.PlayNowAsync(_songs[0], _songs);
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e) => _ = PlaybackService.Instance.ShuffleAsync(_songs);

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackRow row)
        {
            _ = PlaybackService.Instance.PlayNowAsync(row.Song, _songs);
        }
    }
}
