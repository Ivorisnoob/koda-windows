using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Core.Video;
using Koda.Helpers;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VideoModel = Koda.Core.Video.Video;

namespace Koda.Pages;

/// <summary>What a <see cref="VideoListPage"/> shows.</summary>
public sealed record VideoListRequest(VideoListKind Kind, string? Query = null);

public enum VideoListKind
{
    Home,
    Subscriptions,
    WatchLater,
    Liked,
    History,
    Channels,
    Search,
}

/// <summary>
/// Every list of videos: Home, Subscriptions, Watch Later, Liked, History, Channels and search.
/// One page with its four states, a page of results at a time as it is scrolled.
/// </summary>
public sealed partial class VideoListPage : Page, IVideoPage
{
    private readonly ObservableCollection<object> _items = [];
    private VideoListRequest _request = new(VideoListKind.Home);
    private CancellationTokenSource? _cancel;
    private string? _continuation;
    private YouTubeMusic.VideoListSource _source = YouTubeMusic.VideoListSource.Browse;
    private bool _loadingMore;
    private bool _pagesWatched;
    private int _seedOffset;
    private readonly Action _checkFilled;
    private readonly Action<bool> _moreComing;

    public VideoListPage()
    {
        InitializeComponent();
        Cards.ItemsSource = _items;
        VideoGrid.AttachMenu(Cards, null);
        _checkFilled = VideoGrid.KeepFilled(Cards, LoadMoreAsync);
        _moreComing = VideoGrid.PrepareLoading(Cards, LoadingView);
        Unloaded += (_, _) => _cancel?.Cancel();
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (args.Parameter is VideoListRequest request)
        {
            _request = request;
        }

        HeroTitle.Text = _request.Kind switch
        {
            VideoListKind.Subscriptions => "Subscriptions",
            VideoListKind.WatchLater => "Watch Later",
            VideoListKind.Liked => "Liked Videos",
            VideoListKind.History => "Watch History",
            VideoListKind.Channels => "Channels",
            VideoListKind.Search => _request.Query ?? "Search",
            _ => "Home",
        };

        var channels = _request.Kind == VideoListKind.Channels;
        Cards.ItemTemplate = (DataTemplate)Application.Current.Resources[channels ? "ChannelCardTemplate" : "VideoCardTemplate"];
        VideoGrid.Fit(Cards, channels ? 170 : 260, channels ? 180 : null);
        ClearButton.Visibility = _request.Kind == VideoListKind.History ? Visibility.Visible : Visibility.Collapsed;
        if (_request.Kind == VideoListKind.History)
        {
            Cards.RightTapped += OnHistoryRightTapped;
        }

        _ = LoadAsync();
    }

    public bool IsSearch => _request.Kind == VideoListKind.Search;

    /// <summary>A new search from the title bar while this page is open.</summary>
    public void Search(string query)
    {
        _request = new VideoListRequest(VideoListKind.Search, query);
        HeroTitle.Text = query;
        _ = LoadAsync();
    }

    private void OnHistoryRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs args)
    {
        if ((args.OriginalSource as FrameworkElement)?.DataContext is VideoItem item)
        {
            VideoGrid.MenuFor(item.Video, Remove).ShowAt(args.OriginalSource as FrameworkElement, args.GetPosition(args.OriginalSource as UIElement));
            args.Handled = true;
        }
    }

    private void Remove(VideoModel video)
    {
        App.Library.RemoveWatched(video.Id);
        _ = LoadAsync();
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.ConfirmAsync("Clear watch history?", "This also clears where each video got to, and your listening history.", "Clear"))
        {
            App.Library.ClearHistory();
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        _items.Clear();
        _continuation = null;
        _pagesWatched = false;
        _seedOffset = 0;
        _moreComing(false);
        ChannelsHeader.Visibility = Visibility.Collapsed;
        Status.Visibility = Visibility.Collapsed;
        Cards.Visibility = Visibility.Collapsed;
        LoadingView.Content = _request.Kind == VideoListKind.History ? null : VideoGrid.Skeleton();
        LoadingView.Visibility = Visibility.Visible;

        try
        {
            var (videos, channels) = await FetchAsync(cancel.Token);
            if (cancel.IsCancellationRequested) return;
            LoadingView.Visibility = Visibility.Collapsed;
            LoadingView.Content = null;
            foreach (var video in videos) _items.Add(new VideoItem(video));
            if (_request.Kind == VideoListKind.Channels)
            {
                foreach (var channel in channels) _items.Add(channel);
            }
            else if (channels.Length > 0)
            {
                ChannelRow.ItemsSource = channels;
                ChannelsHeader.Visibility = Visibility.Visible;
            }

            if (_items.Count > 0 || channels.Length > 0)
            {
                Cards.Visibility = Visibility.Visible;
                _moreComing(HasMore);
                _checkFilled();
                return;
            }

            ShowEmpty();
        }
        catch (OperationCanceledException)
        {
            // A newer load took over.
        }
        catch (Exception error)
        {
            if (cancel.IsCancellationRequested) return;
            LoadingView.Visibility = Visibility.Collapsed;
            LoadingView.Content = null;
            Status.ShowError(error, () => _ = LoadAsync());
        }
    }

    private void ShowEmpty()
    {
        switch (_request.Kind)
        {
            case VideoListKind.History:
                Status.Show("Nothing watched yet", "Videos you watch gather here, with where you got to.", "");
                break;
            case VideoListKind.Search:
                Status.Show("No videos found", "Try other words.", "");
                break;
            case VideoListKind.Subscriptions or VideoListKind.Channels:
                Status.Show("No subscriptions", "Channels you subscribe to on YouTube appear here.", "");
                break;
            default:
                Status.Show("Nothing here yet", "Videos will appear here.", "");
                break;
        }
    }

    private async Task<(VideoModel[] Videos, ChannelItem[] Channels)> FetchAsync(CancellationToken cancel)
    {
        var music = EngineHost.Music;
        _source = YouTubeMusic.VideoListSource.Browse;
        switch (_request.Kind)
        {
            case VideoListKind.Subscriptions:
                return Page(await music.VideoSubscriptionsAsync(cancel));
            case VideoListKind.WatchLater:
                return Page((await music.VideoPlaylistAsync("WL", cancel)).Videos);
            case VideoListKind.Liked:
                return Page((await music.VideoPlaylistAsync("LL", cancel)).Videos);
            case VideoListKind.History:
                return ([.. App.Library.Watched], []);
            case VideoListKind.Channels:
                var subscribed = await music.SubscribedChannelsAsync(cancel);
                return ([], [.. subscribed.Select(ChannelItem.From)]);
            case VideoListKind.Search:
                _source = YouTubeMusic.VideoListSource.Search;
                var results = await music.SearchVideosAsync(_request.Query ?? "", cancel);
                _continuation = results.Videos.Continuation;
                return (results.Videos.Videos, [.. results.Channels.Select(ChannelItem.From)]);
            default:
                return await HomeAsync(cancel);
        }
    }

    private (VideoModel[], ChannelItem[]) Page(VideoPage page)
    {
        _continuation = page.Continuation;
        return (page.Videos, []);
    }

    /// <summary>The account's Home; signed out, Koda's own: Up Next of what was watched, then
    /// the artists picked in taste setup, then what's popular.</summary>
    private async Task<(VideoModel[], ChannelItem[])> HomeAsync(CancellationToken cancel)
    {
        var music = EngineHost.Music;
        if (App.Account.IsSignedIn)
        {
            try
            {
                var page = await music.VideoHomeAsync(cancel);
                if (page.Videos.Length > 0) return Page(page);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Falls through to the built Home.
            }
        }

        var built = await RelatedToWatchedAsync(cancel);
        if (built.Length > 0)
        {
            _pagesWatched = true;
            return (built, []);
        }

        var results = await music.SearchVideosAsync(PopularQuery, cancel);
        _source = YouTubeMusic.VideoListSource.Search;
        _continuation = results.Videos.Continuation;
        return (results.Videos.Videos, []);
    }

    /// <summary>Up Next of a few watched videos, taken in turn, minus what's been watched.
    /// Each call moves on to the next few, which is how a signed-out Home keeps going.</summary>
    private async Task<VideoModel[]> RelatedToWatchedAsync(CancellationToken cancel)
    {
        const int SeedsPerPage = 4;
        var watched = App.Library.Watched;
        var seeds = watched.Skip(_seedOffset).Take(SeedsPerPage).ToArray();
        _seedOffset += SeedsPerPage;
        var seen = watched.Select(v => v.Id).ToHashSet();
        var lists = new List<VideoModel[]>();
        foreach (var seed in seeds)
        {
            try
            {
                lists.Add((await EngineHost.Music.WatchPageAsync(seed.Id, cancel)).Related.Videos);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // One seed less.
            }
        }

        return [.. Interleave(lists).Where(v => !seen.Contains(v.Id))];
    }

    private static IEnumerable<VideoModel> Interleave(List<VideoModel[]> lists)
    {
        var known = new HashSet<string>();
        for (var i = 0; lists.Any(l => i < l.Length); i++)
        {
            foreach (var list in lists)
            {
                if (i < list.Length && known.Add(list[i].Id)) yield return list[i];
            }
        }
    }

    private const string PopularQuery = "popular videos this week";

    private bool HasMore => _continuation is not null || _pagesWatched;

    /// <summary>The next page; true when there may be more worth asking for at once.</summary>
    private async Task<bool> LoadMoreAsync()
    {
        if (_loadingMore || !HasMore) return false;
        _loadingMore = true;
        var cancel = _cancel;
        try
        {
            VideoModel[] videos;
            if (_continuation is { } token)
            {
                var page = await EngineHost.Music.MoreVideosAsync(token, _source, cancel?.Token ?? default);
                if (cancel?.IsCancellationRequested == true) return false;
                _continuation = page.Continuation;
                videos = page.Videos;
            }
            else
            {
                videos = await RelatedToWatchedAsync(cancel?.Token ?? default);
                if (cancel?.IsCancellationRequested == true) return false;
                _pagesWatched = _seedOffset < App.Library.Watched.Length;

                // Everything watched has been used: Home carries on with what's popular, which
                // pages by itself, so signed out it never runs out either.
                if (!_pagesWatched)
                {
                    var popular = await EngineHost.Music.SearchVideosAsync(PopularQuery, cancel?.Token ?? default);
                    if (cancel?.IsCancellationRequested == true) return false;
                    _source = YouTubeMusic.VideoListSource.Search;
                    _continuation = popular.Videos.Continuation;
                    videos = [.. videos, .. popular.Videos.Videos];
                }
            }

            var known = _items.OfType<VideoItem>().Select(i => i.Video.Id).ToHashSet();
            foreach (var video in videos.Where(v => known.Add(v.Id))) _items.Add(new VideoItem(video));
            _moreComing(HasMore);
            return HasMore;
        }
        catch (Exception)
        {
            // The list keeps what it has; scrolling tries again.
            return false;
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private void OnCardClick(object sender, ItemClickEventArgs e)
    {
        switch (e.ClickedItem)
        {
            case VideoItem item:
                App.Shell?.OpenVideo(item.Video);
                break;
            case ChannelItem { Id: { } id }:
                App.Shell?.NavigateTo(typeof(VideoChannelPage), id);
                break;
        }
    }

    private void OnChannelClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ChannelItem { Id: { } id })
        {
            App.Shell?.NavigateTo(typeof(VideoChannelPage), id);
        }
    }
}

/// <summary>A page of video mode: the title bar searches YouTube while one is open.</summary>
public interface IVideoPage
{
}
