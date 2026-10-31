using System;
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

namespace Koda.Pages;

/// <summary>A channel: banner, name, its videos with their sort orders, and its About panel.</summary>
public sealed partial class VideoChannelPage : Page, IVideoPage
{
    private readonly ObservableCollection<VideoItem> _items = [];
    private readonly CancellationTokenSource _cancel = new();
    private string _channelId = "";
    private ChannelPage? _page;
    private string? _continuation;
    private bool _loadingMore;
    private bool _aboutLoaded;
    private readonly Action _checkFilled;
    private readonly Action<bool> _moreComing;

    public VideoChannelPage()
    {
        InitializeComponent();
        Cards.ItemsSource = _items;
        VideoGrid.Fit(Cards);
        VideoGrid.AttachMenu(Cards);
        _checkFilled = VideoGrid.KeepFilled(Cards, LoadMoreAsync);
        _moreComing = VideoGrid.PrepareLoading(Cards, LoadingView);
        Unloaded += (_, _) => _cancel.Cancel();
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (args.Parameter is string id)
        {
            _channelId = id;
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        Status.Visibility = Visibility.Collapsed;
        Cards.Visibility = Visibility.Collapsed;
        LoadingView.Content = VideoGrid.Skeleton(2, 3);
        LoadingView.Visibility = Visibility.Visible;
        try
        {
            var page = await EngineHost.Music.ChannelAsync(_channelId, cancellation: _cancel.Token);
            _page = page;
            NameText.Text = page.Channel.Name;
            DetailText.Text = string.Join(" · ", new[] { page.Channel.Handle, page.Channel.Subscribers, page.VideoCount }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            Art.SetSource(Avatar, page.Channel.Avatar);
            Art.SetSource(Banner, page.Banner);
            BannerFrame.Visibility = page.Banner is null ? Visibility.Collapsed : Visibility.Visible;
            AboutExpander.Visibility = page.AboutToken is null && string.IsNullOrWhiteSpace(page.Description)
                ? Visibility.Collapsed
                : Visibility.Visible;
            Show(page.Videos, page.Sorts);
            LoadingView.Visibility = Visibility.Collapsed;
            LoadingView.Content = null;
            Cards.Visibility = Visibility.Visible;
            _moreComing(_continuation is not null);
            _checkFilled();
        }
        catch (OperationCanceledException)
        {
            // The page closed.
        }
        catch (Exception error)
        {
            LoadingView.Visibility = Visibility.Collapsed;
            LoadingView.Content = null;
            Status.ShowError(error, () => _ = LoadAsync());
        }
    }

    private void Show(VideoPage videos, ChannelSort[] sorts)
    {
        _items.Clear();
        foreach (var video in videos.Videos) _items.Add(new VideoItem(video));
        _continuation = videos.Continuation;
        SortButton.Visibility = sorts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SortButton.Content = sorts.FirstOrDefault(s => s.IsSelected)?.Title ?? "Sort";
        var menu = new MenuFlyout();
        foreach (var sort in sorts)
        {
            var item = new RadioMenuFlyoutItem { Text = sort.Title, GroupName = "ChannelSort", IsChecked = sort.IsSelected };
            item.Click += (_, _) => _ = SortAsync(sort);
            menu.Items.Add(item);
        }

        SortButton.Flyout = menu;
    }

    private async Task SortAsync(ChannelSort sort)
    {
        if (_page is null) return;
        try
        {
            var (videos, sorts) = await EngineHost.Music.ChannelVideosAsync(sort, _page.Channel.Ref, _cancel.Token);
            Show(videos, sorts);
            _moreComing(_continuation is not null);
            _checkFilled();
        }
        catch (OperationCanceledException)
        {
            // The page closed.
        }
        catch (Exception error)
        {
            App.Shell?.Note($"{Errors.Title(error)}. {Errors.Describe(error)}");
        }
    }

    private async void OnAboutExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (_aboutLoaded || _page is null) return;
        _aboutLoaded = true;
        AboutPanel.Children.Clear();
        AddLine(_page.Description);
        if (_page.AboutToken is not { } token) return;
        try
        {
            var about = await EngineHost.Music.ChannelAboutAsync(token, _cancel.Token);
            AboutPanel.Children.Clear();
            AddLine(about.Description ?? _page.Description);
            AddLine(string.Join(" · ", new[] { about.Subscribers, about.VideoCount, about.Views, about.Joined, about.Country }
                .Where(s => !string.IsNullOrWhiteSpace(s))));
            foreach (var link in about.Links)
            {
                AboutPanel.Children.Add(new HyperlinkButton { Content = link.Title, NavigateUri = link.Url, Padding = new Thickness(0) });
            }
        }
        catch (Exception)
        {
            // The description already shown is the panel, then.
            _aboutLoaded = false;
        }
    }

    private void AddLine(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            AboutPanel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
    }

    private async Task<bool> LoadMoreAsync()
    {
        if (_loadingMore || _continuation is not { } token) return false;
        _loadingMore = true;
        try
        {
            var page = await EngineHost.Music.MoreVideosAsync(token, YouTubeMusic.VideoListSource.Browse, _cancel.Token);
            _continuation = page.Continuation;
            var known = _items.Select(i => i.Video.Id).ToHashSet();
            var channel = _page?.Channel.Ref;
            foreach (var video in page.Videos.Where(v => known.Add(v.Id)))
            {
                _items.Add(new VideoItem(video.Channel is null ? video with { Channel = channel } : video));
            }

            _moreComing(_continuation is not null);
            return _continuation is not null;
        }
        catch (Exception)
        {
            // Scrolling tries again.
            return false;
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private void OnCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VideoItem item) App.Shell?.OpenVideo(item.Video);
    }
}
