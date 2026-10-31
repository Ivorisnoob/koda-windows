using System;
using System.Linq;
using System.Threading.Tasks;
using Koda.Core.Video;
using Koda.Helpers;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using VirtualKey = Windows.System.VirtualKey;
using VideoModel = Koda.Core.Video.Video;

namespace Koda.Pages;

/// <summary>
/// The watch page. Everything drawn here is read from <see cref="VideoPlaybackService"/>, which
/// outlives the page: leaving keeps the video going in the bar. Beside the player sits Up Next,
/// under it the details; in theater and on a narrow window Up Next moves below, and full screen
/// is the same page with everything but the player out of the way.
/// </summary>
public sealed partial class VideoWatchPage : Page, IVideoPage
{
    private static readonly double[] Speeds = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];

    private readonly DispatcherQueueTimer _hideTimer;
    private string? _detailsFor;
    private string? _commentsToken;
    private bool _scrubbing;
    private bool _immersive;
    private bool _settingVolume;

    private static VideoPlaybackService Player => VideoPlaybackService.Instance;

    public VideoWatchPage()
    {
        InitializeComponent();
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(3);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideControls();
        Scrubbing.Attach(Scrubber, on => _scrubbing = on, Player.Seek, () => Player.HasVideo);
        Scrubber.ValueChanged += (_, _) =>
        {
            if (_scrubbing) DrawTime(Scrubber.Value);
        };

        // The controls give way in a small player, so full screen stays on the row.
        PlayerHost.SizeChanged += (_, e) =>
            VolumeSlider.Visibility = e.NewSize.Width < 620 ? Visibility.Collapsed : Visibility.Visible;

        _settingVolume = true;
        VolumeSlider.Value = App.Volume;
        _settingVolume = false;
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        Player.Changed += OnChanged;
        Player.Tick += OnTick;
        Player.Attach(Picture);
        UpNextLoading.Content = Skeletons.Rows(5);
        ApplyLayout();
        Draw();
        OnTick();
        ShowControls();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        base.OnNavigatedFrom(args);
        Player.Changed -= OnChanged;
        Player.Tick -= OnTick;
        _hideTimer.Stop();
        if (_immersive) SetImmersive(false);
        Player.Detach(Picture);
    }

    private void OnChanged() => DispatcherQueue.TryEnqueue(Draw);

    private void OnTick() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_scrubbing) return;
        var duration = Player.Duration;
        if (duration > 0)
        {
            Scrubber.Maximum = duration;
            Scrubber.Value = Math.Clamp(Player.Position, 0, duration);
        }

        DrawTime(Player.Position);
    });

    private void DrawTime(double position) =>
        TimeText.Text = $"{Format2(position)} / {Format2(Player.Duration)}";

    private static string Format2(double seconds)
    {
        var total = (long)Math.Max(0, seconds);
        return total >= 3600
            ? $"{total / 3600}:{total % 3600 / 60:D2}:{total % 60:D2}"
            : $"{total / 60}:{total % 60:D2}";
    }

    private void Draw()
    {
        var video = Player.Current;
        var playing = Player.IsPlaying;
        var busy = video is not null && Player.Error is null && (Player.IsLoading || Player.IsBuffering);

        Busy.IsActive = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PlayIcon.Glyph = playing ? "" : "";
        var playLabel = playing ? "Pause" : "Play";
        ToolTipService.SetToolTip(PlayButton, $"{playLabel} (K)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayButton, playLabel);
        NextButton.IsEnabled = Player.Next is not null;
        MuteIcon.Glyph = Player.IsMuted ? "" : "";

        CaptionsIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            Player.CaptionsOn ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"];
        var caption = Player.CurrentCaption;
        CaptionText.Text = caption;
        CaptionBox.Visibility = caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (Player.Error is { } error)
        {
            PlayerError.Show("This video didn't play", error, "", "Try again", () => _ = Player.RetryAsync());
            Controls.Visibility = Visibility.Collapsed;
        }
        else
        {
            PlayerError.Visibility = Visibility.Collapsed;
            Controls.Visibility = Visibility.Visible;
        }

        var seconds = Player.AutoplaySeconds;
        if (seconds > 0 && Player.Next is { } next)
        {
            AutoplayLabel.Text = $"Up next in {seconds}";
            AutoplayTitle.Text = next.Title;
            AutoplayCard.Visibility = Visibility.Visible;
        }
        else
        {
            AutoplayCard.Visibility = Visibility.Collapsed;
        }

        DrawDetails(video);
        if (!playing) ShowControls();
    }

    private void DrawDetails(VideoModel? video)
    {
        if (video is null) return;
        var watch = Player.Watch;
        var key = $"{video.Id}|{watch is not null}";
        if (key == _detailsFor) return;
        _detailsFor = key;

        VideoArt.SetVideo(Poster, video);
        var shown = watch?.Video ?? video;
        var channel = shown.Channel ?? video.Channel;
        TitleText.Text = shown.Title;
        ChannelText.Text = channel?.Name ?? "";
        SubscribersText.Text = watch?.Subscribers ?? "";
        SubscribersText.Visibility = SubscribersText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChannelButton.IsEnabled = channel?.Id is not null;
        Art.SetSource(ChannelAvatar, channel?.Avatar);

        var meta = string.Join(" · ", new[] { watch?.ViewCount ?? video.Views, watch?.Date ?? video.Published }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        MetaText.Text = meta.Length > 0 ? meta : "Description";
        DescriptionText.Text = watch?.Description ?? "";
        DescriptionExpander.Visibility = watch is not null && DescriptionText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DescriptionExpander.IsExpanded = false;

        CommentsList.Children.Clear();
        MoreCommentsButton.Visibility = Visibility.Collapsed;
        CommentsExpander.IsExpanded = false;
        _commentsToken = watch?.CommentsToken;
        CommentsHeader.Text = watch?.CommentCount is { Length: > 0 } count ? $"Comments · {count}" : "Comments";
        CommentsExpander.Visibility = _commentsToken is null ? Visibility.Collapsed : Visibility.Visible;

        if (watch is null)
        {
            UpNextLoading.Visibility = Visibility.Visible;
            UpNextList.ItemsSource = null;
            return;
        }

        UpNextLoading.Visibility = Visibility.Collapsed;
        UpNextLoading.Content = null;
        UpNextList.ItemsSource = watch.Related.Videos.Take(24).Select(BuildRow).ToArray();
    }

    /// <summary>An Up Next row: a native list row that takes hover, focus and right-click.</summary>
    private Button BuildRow(VideoModel video)
    {
        var row = new Button
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new VideoItem(video),
            ContentTemplate = (DataTemplate)Application.Current.Resources["VideoRowTemplate"],
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, video.Title);
        row.Click += (_, _) => App.Shell?.OpenVideo(video);
        row.ContextFlyout = VideoGrid.MenuFor(video);
        return row;
    }

    // MARK: layout

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    /// <summary>
    /// As on YouTube's site: Up Next beside the player, the details under it, and the page
    /// scrolling as one. Theater puts the video across the whole page on black, with the
    /// details and Up Next side by side under it. Only a window too narrow for two columns
    /// stacks them. The player always keeps 16:9.
    /// </summary>
    private void ApplyLayout()
    {
        var width = PageRoot.ActualWidth;
        var height = PageRoot.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var theater = App.Settings.VideoTheater && !_immersive;
        var wide = width >= 1020;
        var side = _immersive ? 0 : wide ? 380 : !theater && width >= 760 ? 300 : 0;
        var hasSide = side > 0;

        Layout.Padding = _immersive ? new Thickness(0)
            : theater ? new Thickness(0, 0, 0, 28)
            : new Thickness(24, 12, 24, 28);
        Layout.ColumnSpacing = hasSide ? 24 : 0;
        Layout.RowSpacing = _immersive ? 0 : 16;
        SideColumn.Width = new GridLength(side);
        PlayerHost.CornerRadius = _immersive || theater ? new CornerRadius(0) : new CornerRadius(8);
        Grid.SetColumnSpan(PlayerHost, _immersive || theater ? 2 : 1);

        if (Layout.RowDefinitions.Count < 3)
        {
            Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Grid.SetColumn(UpNext, hasSide ? 1 : 0);
        Grid.SetRow(UpNext, !hasSide ? 2 : theater ? 1 : 0);
        Grid.SetRowSpan(UpNext, hasSide && !theater ? 2 : 1);

        Details.Margin = theater ? new Thickness(24, 0, hasSide ? 0 : 24, 0) : new Thickness(0);
        UpNext.Margin = theater ? new Thickness(hasSide ? 0 : 24, 0, 24, 0) : new Thickness(0);
        Details.Visibility = UpNext.Visibility = _immersive ? Visibility.Collapsed : Visibility.Visible;

        Scroller.VerticalScrollMode = _immersive ? ScrollMode.Disabled : ScrollMode.Auto;
        Scroller.VerticalScrollBarVisibility = _immersive ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

        TheaterIcon.Glyph = theater ? "\uE9A8" : "\uE9A6";
        var label = theater ? "Default view" : "Theater mode";
        ToolTipService.SetToolTip(TheaterButton, $"{label} (T)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TheaterButton, label);

        if (_immersive)
        {
            PlayerHost.Height = height;
        }
        else if (theater)
        {
            PlayerHost.Height = Math.Round(Math.Max(240, Math.Min(width * 9 / 16, height * 0.78)));
        }
        else
        {
            var column = width - Layout.Padding.Left - Layout.Padding.Right - (hasSide ? side + 24 : 0);
            PlayerHost.Height = Math.Round(column * 9 / 16);
        }
    }
    private void ToggleTheater()
    {
        App.Settings.Update(s => s.VideoTheater = !s.VideoTheater);
        ApplyLayout();
    }

    private void SetImmersive(bool on)
    {
        _immersive = on;
        App.Shell?.SetVideoFullScreen(on);
        FullScreenIcon.Glyph = on ? "" : "";
        var label = on ? "Exit full screen" : "Full screen";
        ToolTipService.SetToolTip(FullScreenButton, $"{label} (F)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FullScreenButton, label);
        TheaterButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ApplyLayout();
        ShowControls();
    }

    // MARK: controls

    private void ShowControls()
    {
        Controls.Opacity = 1;
        Controls.IsHitTestVisible = true;
        _hideTimer.Stop();
        if (Player.IsPlaying) _hideTimer.Start();
    }

    private void HideControls()
    {
        if (!Player.IsPlaying || _scrubbing) return;
        Controls.Opacity = 0;
        Controls.IsHitTestVisible = false;
    }

    private void OnPlayerPointerMoved(object sender, PointerRoutedEventArgs e) => ShowControls();

    private void OnPlayerPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (Player.IsPlaying) HideControls();
    }

    /// <summary>A tap on the picture plays or pauses; one on the controls does what it's on.</summary>
    private void OnPlayerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsOnControl(e.OriginalSource)) Player.TogglePlay();
    }

    private void OnPlayerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!IsOnControl(e.OriginalSource))
        {
            // The double tap's first tap already toggled; put it back.
            Player.TogglePlay();
            SetImmersive(!_immersive);
        }
    }

    private bool IsOnControl(object source)
    {
        for (var element = source as DependencyObject; element is not null && element != PlayerHost;
             element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element == Controls || element == AutoplayCard || element == PlayerError) return true;
        }

        return false;
    }

    /// <summary>The shortcuts, while this page is open and nothing that types has focus.</summary>
    public bool HandleKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.K:
            case VirtualKey.Space:
                Player.TogglePlay();
                break;
            case VirtualKey.J:
                Player.Skip(-10);
                break;
            case VirtualKey.L:
                Player.Skip(10);
                break;
            case VirtualKey.Left:
                Player.Skip(-5);
                break;
            case VirtualKey.Right:
                Player.Skip(5);
                break;
            case VirtualKey.Up:
                VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                break;
            case VirtualKey.Down:
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                break;
            case VirtualKey.M:
                Player.IsMuted = !Player.IsMuted;
                break;
            case VirtualKey.F:
                SetImmersive(!_immersive);
                break;
            case VirtualKey.T when !_immersive:
                ToggleTheater();
                break;
            case VirtualKey.C:
                _ = Player.ToggleCaptionsAsync();
                break;
            case VirtualKey.Escape when _immersive:
                SetImmersive(false);
                break;
            default:
                return false;
        }

        ShowControls();
        return true;
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => Player.TogglePlay();

    private void OnBackTenClick(object sender, RoutedEventArgs e) => Player.Skip(-10);

    private void OnAheadTenClick(object sender, RoutedEventArgs e) => Player.Skip(10);

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (Player.Next is { } next) App.Shell?.OpenVideo(next);
    }

    private void OnMuteClick(object sender, RoutedEventArgs e) => Player.IsMuted = !Player.IsMuted;

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_settingVolume) return;
        Player.SetVolume(e.NewValue);
        App.SetVolume(e.NewValue);
    }

    private async void OnCaptionsClick(object sender, RoutedEventArgs e)
    {
        await Player.ToggleCaptionsAsync();
        if (Player.CaptionTracks.Length == 0) App.Shell?.Note("This video has no captions");
    }

    private void OnTheaterClick(object sender, RoutedEventArgs e) => ToggleTheater();

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => SetImmersive(!_immersive);

    private void OnAutoplayNowClick(object sender, RoutedEventArgs e)
    {
        if (Player.Next is { } next)
        {
            Player.CancelAutoplay();
            App.Shell?.OpenVideo(next);
        }
    }

    private void OnAutoplayCancelClick(object sender, RoutedEventArgs e) => Player.CancelAutoplay();

    private async void OnSettingsMenuOpening(object sender, object e)
    {
        SettingsMenu.Items.Clear();

        var quality = new MenuFlyoutSubItem { Text = "Quality", Icon = new FontIcon { Glyph = "" } };
        quality.Items.Add(QualityItem("Auto", 0));
        foreach (var height in Player.Qualities.Select(q => q.Height).Distinct())
        {
            quality.Items.Add(QualityItem($"{height}p", height));
        }

        SettingsMenu.Items.Add(quality);

        var speed = new MenuFlyoutSubItem { Text = "Speed", Icon = new FontIcon { Glyph = "" } };
        foreach (var value in Speeds)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = value == 1 ? "Normal" : $"{value}x",
                GroupName = "VideoSpeed",
                IsChecked = Math.Abs(Player.Speed - value) < 0.01,
            };
            item.Click += (_, _) => Player.SetSpeed(value);
            speed.Items.Add(item);
        }

        SettingsMenu.Items.Add(speed);

        if (Player.CaptionTracks.Length == 0) await Player.LoadCaptionTracksAsync();
        if (Player.CaptionTracks.Length > 0)
        {
            var captions = new MenuFlyoutSubItem { Text = "Captions", Icon = new FontIcon { Glyph = "" } };
            foreach (var track in Player.CaptionTracks)
            {
                var item = new RadioMenuFlyoutItem
                {
                    Text = track.IsAutomatic ? $"{track.Name} (auto)" : track.Name,
                    GroupName = "VideoCaptions",
                    IsChecked = Player.CaptionsOn && Player.CaptionTrack == track,
                };
                item.Click += (_, _) => _ = Player.SelectCaptionAsync(track);
                captions.Items.Add(item);
            }

            SettingsMenu.Items.Add(captions);
        }
    }

    private RadioMenuFlyoutItem QualityItem(string text, int height)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = text,
            GroupName = "VideoQuality",
            IsChecked = Player.Quality == height,
        };
        item.Click += (_, _) => _ = Player.SetQualityAsync(height);
        return item;
    }

    private void OnChannelClick(object sender, RoutedEventArgs e)
    {
        var channel = Player.Watch?.Video.Channel ?? Player.Current?.Channel;
        if (channel?.Id is { } id) App.Shell?.NavigateTo(typeof(VideoChannelPage), id);
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (Player.Current is { } video) VideoGrid.CopyLink(video);
    }

    private void OnOpenOnYouTubeClick(object sender, RoutedEventArgs e)
    {
        if (Player.Current is { } video) VideoGrid.OpenOnYouTube(video);
    }

    // MARK: comments

    private void OnCommentsExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (CommentsList.Children.Count == 0) _ = LoadCommentsAsync();
    }

    private void OnMoreCommentsClick(object sender, RoutedEventArgs e) => _ = LoadCommentsAsync();

    private async Task LoadCommentsAsync()
    {
        if (_commentsToken is not { } token) return;
        MoreCommentsButton.IsEnabled = false;
        var skeleton = Skeletons.Rows(3);
        CommentsList.Children.Add(skeleton);
        try
        {
            var page = await EngineHost.Music.CommentsAsync(token);
            CommentsList.Children.Remove(skeleton);
            foreach (var comment in page.Comments) CommentsList.Children.Add(BuildComment(comment));
            _commentsToken = page.Continuation;
            MoreCommentsButton.Visibility = _commentsToken is null ? Visibility.Collapsed : Visibility.Visible;
            if (CommentsList.Children.Count == 0)
            {
                CommentsList.Children.Add(new TextBlock { Text = "No comments yet." });
            }
        }
        catch (Exception error)
        {
            CommentsList.Children.Remove(skeleton);
            App.Shell?.Note($"{Errors.Title(error)}. {Errors.Describe(error)}");
            MoreCommentsButton.Content = "Try again";
            MoreCommentsButton.Visibility = Visibility.Visible;
        }
        finally
        {
            MoreCommentsButton.IsEnabled = true;
        }
    }

    private static Grid BuildComment(VideoComment comment)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var avatar = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            VerticalAlignment = VerticalAlignment.Top,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
        };
        if (comment.Avatar is { } url)
        {
            avatar.Child = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(url) { DecodePixelWidth = 72 } };
        }

        row.Children.Add(avatar);
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = $"{comment.Author}  ·  {comment.Published}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        text.Children.Add(new TextBlock { Text = comment.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }
}
