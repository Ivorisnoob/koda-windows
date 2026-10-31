using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Koda.Pages;

/// <summary>
/// Home, ported from the iOS/Mac HomeModel: the listener's own page first.
/// Your Mix (radios of the top artists' favourite songs, woven by Taste) beside Quick
/// picks (songs like the last one played), then Recently played, Your top artists,
/// Charts, and YouTube Music's own shelves below. Someone new gets trending songs in
/// both places ("Start Here"). The last Home is a snapshot on F:, shown at once on a
/// cold start and refreshed in place; one under half an hour old is not refetched.
/// </summary>
public sealed partial class HomePage : Page
{
    private sealed record Mix(string[] Artists, Song[] Songs, bool IsPersonal);

    private sealed record Saved(
        Mix? Mix, Song[] Picks, bool PicksPersonal, PlaylistSummary[] Charts, Song[] Trending,
        Shelf[] YouTube, Dictionary<string, Artwork> ArtistPhotos, DateTimeOffset LoadedAt);

    private static string SnapshotPath => Path.Combine(Koda.Services.Profiles.ActiveFolder, "home-snapshot.json");
    private static readonly JsonSerializerOptions SnapshotJson = new() { PropertyNameCaseInsensitive = true };

    private Saved? _state;
    private bool _loading;
    private Mix? _mix;
    private int _pickCount = 12;
    private double _topWidth;

    public HomePage()
    {
        InitializeComponent();
        SongMenu.Attach(Picks, () => _state?.Picks ?? []);
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        Greeting.Text = DateTime.Now.Hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 17 => "Good afternoon",
            >= 17 and < 22 => "Good evening",
            _ => "Up late",
        };

        _state ??= ReadSnapshot();
        if (_state is not null)
        {
            Show(_state);
        }

        var fresh = _state is { } s && DateTimeOffset.Now - s.LoadedAt < TimeSpan.FromMinutes(30);
        if (!fresh)
        {
            _ = RefreshAsync();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        EngineHost.Music.ClearPageCache();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        RefreshButton.IsEnabled = false;
        var hadContent = _state is not null;
        Error.Visibility = Visibility.Collapsed;
        if (!hadContent)
        {
            MixSkeleton.Content = Skeletons.Breathe(Skeletons.Block(380, 300, 20));
            PicksSkeleton.Content = Skeletons.Rows(4);
            SkeletonHost.Content = Skeletons.Shelves(2);
        }

        var music = EngineHost.Music;
        var library = App.Library;
        var recent = library.Recent;
        var taste = Taste.From(library);
        try
        {
            var explore = Try(music.ExploreAsync());
            var home = Try(music.HomeAsync());
            var charts = Try(music.ChartPlaylistsAsync());
            var picks = QuickPicksAsync(recent);
            await Task.WhenAll(explore, home, charts, picks);
            if (explore.Result is null && home.Result is null && picks.Result.Length == 0)
            {
                throw MusicException.Network("home");
            }

            var trending = Trending(explore.Result);
            var youtube = (home.Result?.Shelves ?? []).Concat(
                (explore.Result?.Shelves ?? []).Where(s => s.Title != "Trending")).ToArray();
            var personal = picks.Result.Length > 0;
            var state = new Saved(_state?.Mix, personal ? picks.Result : trending, personal, charts.Result ?? [],
                trending, DedupeShelves(youtube), _state?.ArtistPhotos ?? [], DateTimeOffset.Now);
            if (state.Mix is null && taste.IsEmpty && trending.Length > 0)
            {
                state = state with { Mix = StartHere(trending) };
            }

            _state = state;
            Show(state);
            ClearSkeletons();

            // The mix and the artist photos come after the page is up.
            var mix = await BuildMixAsync(taste, recent);
            var photos = await ArtistPhotosAsync(state.ArtistPhotos);
            _state = _state with
            {
                Mix = mix ?? _state.Mix ?? (trending.Length > 0 ? StartHere(trending) : null),
                ArtistPhotos = photos,
            };
            Show(_state);
            WriteSnapshot(_state);
        }
        catch (Exception error)
        {
            if (hadContent)
            {
                App.Shell?.Note($"Home didn't refresh. {Errors.Describe(error)}");
            }
            else
            {
                Error.ShowError(error, () => _ = RefreshAsync());
            }
        }
        finally
        {
            ClearSkeletons();
            RefreshButton.IsEnabled = true;
            _loading = false;
        }
    }

    private void ClearSkeletons()
    {
        MixSkeleton.Content = null;
        PicksSkeleton.Content = null;
        SkeletonHost.Content = null;
    }

    private static async Task<T?> Try<T>(Task<T> task) where T : class
    {
        try
        {
            return await task;
        }
        catch (MusicException)
        {
            return null;
        }
    }

    // MARK: building

    /// <summary>Songs like the last one played; with no history yet, nothing (trending fills in).</summary>
    private static async Task<Song[]> QuickPicksAsync(Song[] recent)
    {
        if (recent.Length == 0)
        {
            return [];
        }

        try
        {
            var radio = await EngineHost.Music.RadioAsync(recent[0].Id);
            // What the listener asked to hear less of stays out of everything Home suggests.
            return [.. App.Library.Offerable(radio.Songs.Where(s => s.Id != recent[0].Id)).Take(20)];
        }
        catch (MusicException)
        {
            return [];
        }
    }

    /// <summary>Your Mix: a radio from each top artist's favourite song, woven together; the seeds
    /// and the last few songs played are left out, so the mix is something to go on to.</summary>
    private static async Task<Mix?> BuildMixAsync(Taste taste, Song[] recent)
    {
        if (taste.IsEmpty)
        {
            return null;
        }

        var radios = await Task.WhenAll(taste.Seeds.Select(async seed =>
        {
            try
            {
                return (await EngineHost.Music.RadioAsync(seed.Id)).Songs;
            }
            catch (MusicException)
            {
                return Array.Empty<Song>();
            }
        }));
        var skip = taste.Seeds.Select(s => s.Id).Concat(recent.Take(10).Select(s => s.Id));
        var songs = App.Library.Offerable(Taste.Weave(radios, skip));
        return songs.Length == 0 ? null : new Mix([.. taste.Artists.Take(3)], songs, true);
    }

    private static Mix StartHere(Song[] trending) =>
        new([.. trending.Select(Taste.LeadArtist).Distinct().Order().Take(3)], trending, false);

    private static Song[] Trending(ShelfPage? explore) =>
        App.Library.Offerable(explore?.Shelves.FirstOrDefault(s => s.Title == "Trending")?.Items
            .OfType<MusicItem.SongRecord>().Select(s => s.Value) ?? []);

    private static Shelf[] DedupeShelves(Shelf[] shelves)
    {
        var seen = new HashSet<string>();
        return [.. shelves.Where(s => seen.Add(s.Title))];
    }

    /// <summary>The listener's most-played artists that can be opened, strongest first.</summary>
    private static ArtistRef[] TopArtists(int limit = 10)
    {
        var library = App.Library;
        var refs = new Dictionary<string, ArtistRef>();
        foreach (var stats in library.SongStats.Values)
        {
            if (stats.Song.Artists.FirstOrDefault() is { Id: not null } artist) refs[artist.Name] = artist;
        }

        return [.. Taste.From(library, maxSeeds: 0).Artists.Where(refs.ContainsKey).Select(n => refs[n]).Take(limit)];
    }

    private static async Task<Dictionary<string, Artwork>> ArtistPhotosAsync(Dictionary<string, Artwork> known)
    {
        var photos = new Dictionary<string, Artwork>(known);
        var missing = TopArtists().Select(a => a.Id!).Where(id => !photos.ContainsKey(id)).ToArray();
        var found = await Task.WhenAll(missing.Select(async id =>
        {
            try
            {
                return (id, (await EngineHost.Music.ArtistAsync(id)).Artist.Artwork);
            }
            catch (MusicException)
            {
                return (id, (Artwork?)null);
            }
        }));
        foreach (var (id, art) in found)
        {
            if (art is not null) photos[id] = art;
        }

        return photos;
    }

    // MARK: drawing

    private void Show(Saved state)
    {
        Error.Visibility = Visibility.Collapsed;
        ShowMix(state.Mix);
        var showGlobal = App.Settings.ShowsGlobalContent;
        PicksStrapline.Text = state.PicksPersonal ? "Start with these" : "Trending now";
        var showPicks = state.Picks.Length > 0 && (state.PicksPersonal || showGlobal);
        PicksPanel.Visibility = showPicks ? Visibility.Visible : Visibility.Collapsed;
        Picks.ItemsSource = showPicks ? state.Picks.Take(_pickCount).ToArray() : [];

        var shelves = new List<ShelfView>();
        var recent = App.Library.Recent;
        if (recent.Length > 0)
        {
            shelves.Add(ShelfView.Of("Recently played", recent.Take(12).Select(s => (MusicItem)new MusicItem.SongRecord(s))));
        }

        var yours = TopArtists();
        if (yours.Length >= 3)
        {
            shelves.Add(new ShelfView("Your top artists", "Based on your listening", [.. yours.Select(a => CardItem.From(
                new MusicItem.ArtistRecord(new ArtistSummary(a.Id!, a.Name, null, state.ArtistPhotos.GetValueOrDefault(a.Id!)))) with { Subtitle = "" })]));
        }

        if (App.Settings.ShowsGlobalContent && state.Charts.Length > 0)
        {
            shelves.Add(new ShelfView("Charts", "Global and near you", [.. state.Charts.Select(p => CardItem.From(new MusicItem.PlaylistRecord(p)))]));
        }

        var ytShelves = state.YouTube;
        if (!App.Settings.ShowsGlobalContent)
        {
            ytShelves = ytShelves.Where(s => !s.Title.Contains("trend", StringComparison.OrdinalIgnoreCase)
                && !s.Title.Contains("chart", StringComparison.OrdinalIgnoreCase)
                && !s.Title.Contains("global", StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        shelves.AddRange(ytShelves.Select(ShelfView.From));
        Shelves.Shelves = shelves;
    }

    private void ShowMix(Mix? mix)
    {
        _mix = mix;
        MixCard.Visibility = mix is null ? Visibility.Collapsed : Visibility.Visible;
        if (mix is null)
        {
            return;
        }

        MixSkeleton.Content = null;
        MixTitle.Text = mix.IsPersonal ? "Your Mix" : "Start Here";
        MixArtists.Text = mix.Artists.Length switch
        {
            0 => $"{mix.Songs.Length} songs",
            1 => $"With {mix.Artists[0]}",
            2 => $"With {mix.Artists[0]} and {mix.Artists[1]}",
            _ => $"With {mix.Artists[0]}, {mix.Artists[1]} and {mix.Artists[2]}",
        };
        var seen = new HashSet<string>();
        var covers = mix.Songs.Select(s => s.Artwork).OfType<Artwork>().Where(a => seen.Add(a.Resized(120).ToString())).Take(3).ToArray();
        MixCovers.ItemsSource = covers;
        _ = TintMixAsync(covers);
    }

    /// <summary>The card in its covers' colours, darkened so white text holds.</summary>
    private async Task TintMixAsync(Artwork[] covers)
    {
        var stops = new[] { MixStop0, MixStop1, MixStop2 };
        for (var i = 0; i < Math.Min(3, covers.Length); i++)
        {
            if (await ArtAccent.FromAsync(covers[i]) is { } c)
            {
                stops[i].Color = Windows.UI.Color.FromArgb(255, (byte)(c.R * 0.72), (byte)(c.G * 0.72), (byte)(c.B * 0.72));
            }
        }
    }

    private void OnTopSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _topWidth = e.NewSize.Width;
        LayoutTop();
    }

    /// <summary>
    /// Your Mix beside Quick picks whenever both fit (the card needs about 340 for its three
    /// covers, a column of picks 280), above them only when the window is narrower than that.
    /// The picks' columns stretch to the room they have, so neither layout leaves an empty strip
    /// down the right-hand side.
    /// </summary>
    private void LayoutTop()
    {
        var width = _topWidth;
        if (width <= 0)
        {
            return;
        }

        const double gap = 28;
        var wide = width >= 340 + gap + 280;
        var mix = wide ? Math.Clamp(width * 0.4, 340, 380) : width;
        MixColumn.Width = wide ? new GridLength(mix) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(PicksPanel, wide ? 1 : 0);
        Grid.SetRow(PicksPanel, wide ? 0 : 1);
        MixCard.MaxWidth = double.PositiveInfinity;
        MixCard.HorizontalAlignment = HorizontalAlignment.Stretch;

        // As many columns of at least 280 as fit, at most three, sharing the room evenly; four
        // rows beside the card, as tall as it is.
        var room = wide ? width - mix - gap : width;
        var columns = Math.Clamp((int)(room / 280), 1, 3);
        if (Picks.ItemsPanelRoot is ItemsWrapGrid grid)
        {
            // Each item keeps a 4px gap on its right.
            grid.ItemWidth = Math.Max(200, Math.Floor(room / columns) - 4);
        }

        var count = columns * 4;
        if (count != _pickCount)
        {
            _pickCount = count;
            if (_state is { } state) Picks.ItemsSource = state.Picks.Take(_pickCount).ToArray();
        }
    }

    /// <summary>The picks' panel exists only once the list is loaded; its columns are sized then.</summary>
    private void OnPicksLoaded(object sender, RoutedEventArgs e) => LayoutTop();

    private void OnPickClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Song song && _state is { } state)
        {
            _ = PlaybackService.Instance.PlayNowAsync(song, state.Picks);
        }
    }

    private void OnMixPlayClick(object sender, RoutedEventArgs e)
    {
        if (_state?.Mix is { Songs.Length: > 0 } mix) _ = PlaybackService.Instance.PlayNowAsync(mix.Songs[0], mix.Songs);
    }

    private void OnMixShuffleClick(object sender, RoutedEventArgs e)
    {
        if (_state?.Mix is { } mix) _ = PlaybackService.Instance.ShuffleAsync(mix.Songs);
    }

    // The card opens the mix's songs; its Play and Shuffle buttons handle their own taps.
    private void OnMixCardTapped(object sender, TappedRoutedEventArgs e) => OpenMix();

    private void OnMixSongsClick(object sender, RoutedEventArgs e) => OpenMix();

    private void OpenMix()
    {
        if (_mix is { Songs.Length: > 0 } mix)
        {
            App.MixSongs = mix.Songs;
            Navigator.Go(typeof(SongsPage), "mix");
        }
    }

    // MARK: snapshot

    private static Saved? ReadSnapshot()
    {
        try
        {
            var saved = File.Exists(SnapshotPath)
                ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(SnapshotPath), SnapshotJson)
                : null;
            // A snapshot from an older Home lacks these; it is ignored rather than half-drawn.
            return saved is { Picks: not null, Charts: not null, Trending: not null, YouTube: not null, ArtistPhotos: not null }
                ? saved
                : null;
        }
        catch (Exception)
        {
            return null; // an old or damaged snapshot only costs the fast start
        }
    }

    private static void WriteSnapshot(Saved state)
    {
        try
        {
            File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(state, SnapshotJson));
        }
        catch (Exception)
        {
            // Next launch loads from the network instead.
        }
    }
}
