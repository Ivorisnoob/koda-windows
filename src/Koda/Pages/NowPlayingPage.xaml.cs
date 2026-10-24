using System;
using System.Collections.ObjectModel;
using System.Linq;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Koda.Pages;

/// <summary>
/// Now Playing, ported from the Mac's NowPlayingView: the cover's colours as a slowly moving
/// background, the cover large (easing back while paused), title and artist with the like
/// button, scrubber, transport and volume in one column; the queue panel (Shuffle, Repeat,
/// Autoplay, Up Next) beside it when the window is wide, in the cover's place when narrow.
/// Always dark, as on the Mac. Esc, Close or the player bar's cover close it.
/// </summary>
public sealed partial class NowPlayingPage : Page
{
    /// <summary>Whether the queue panel was open, kept for the session as the Mac keeps its panel.</summary>
    private static bool _queueOpen;

    /// <summary>Whether the lyrics panel was open, kept for the session as the Mac keeps its panel.</summary>
    private static bool _lyricsOpen;

    /// <summary>Where a side panel is installed: the layout's column, or the narrow host.</summary>
    private enum PanelPlace
    {
        Layout,
        Narrow,
    }

    private PanelPlace _queuePlace = PanelPlace.Layout;
    private PanelPlace _lyricsPlace = PanelPlace.Layout;

    private PlaybackService Playback => PlaybackService.Instance;
    private readonly ObservableCollection<QueueItem> _upcoming = [];
    private string? _paletteFor;
    private bool _wide = true;
    private bool _scrubbing;
    private bool _dragging;
    private QueueItem? _dragged;
    private int _dragFrom;
    private Song[]? _cleared;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private ScrollViewer? _queueScroller;

    // MARK: lyrics

    /// <summary>The panel that shows the words and follows the song.</summary>
    private LyricsPanel? _lyrics;

    private string? _lyricsFor;

    public NowPlayingPage()
    {
        InitializeComponent();
        UpNext.ItemsSource = _upcoming;
        UpNext.ContextRequested += OnUpNextContext;

        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) =>
        {
            Close();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(escape);

        Scrubber.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _scrubbing = true), true);
        Scrubber.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) =>
        {
            if (!_scrubbing) return;
            _scrubbing = false;
            Playback.SeekSeconds(Scrubber.Value);
        }), true);
        Scrubber.ValueChanged += (_, _) =>
        {
            if (_scrubbing) UpdateTimes(Scrubber.Value);
        };

        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            _cleared = null;
            RenderQueueHeader();
        };

        // The lyrics: the panel follows the song, and the listener's own scrolling
        // takes over from it when they scroll away.
        _lyrics = new LyricsPanel(LyricsScroll, LyricsLines, DispatcherQueue);
        _lyrics.Seek = seconds => Playback.SeekSeconds(seconds);
        // The panel asks the player where the song is, every frame: the app's own
        // tick only comes twice a second, and a word that fills in half-second steps
        // reads as a sequence of keyframes rather than a word being sung.
        _lyrics.PositionSource = () => Playback.PositionSeconds;
        LyricsScroll.PointerPressed += (_, _) => _lyrics.UserScrolled = true;
        LyricsScroll.PointerWheelChanged += (_, _) => _lyrics.UserScrolled = true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (args.Parameter is "queue") _queueOpen = true;
        Playback.TrackChanged += OnTrackChanged;
        Playback.QueueChanged += OnQueueChanged;
        Playback.PlayStateChanged += OnPlayStateChanged;
        Playback.PositionTick += OnPositionTick;
        App.Library.Changed += OnLibraryChanged;
        App.Settings.Changed += OnSettingsChanged;
        Volume.Value = App.Volume;
        ApplySkipSettings();
        RenderTrack();
        RenderQueue();
        RenderPlayState();
        ApplyLayout();
        if (_lyricsOpen)
        {
            // The panel was left open when the page was closed: it starts following again.
            _lyrics?.PlayEntrance();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        base.OnNavigatedFrom(args);
        Playback.TrackChanged -= OnTrackChanged;
        Playback.QueueChanged -= OnQueueChanged;
        Playback.PlayStateChanged -= OnPlayStateChanged;
        Playback.PositionTick -= OnPositionTick;
        App.Library.Changed -= OnLibraryChanged;
        App.Settings.Changed -= OnSettingsChanged;
        _lyrics?.Pause();
        Drift.Stop();
    }

    private void OnTrackChanged() => DispatcherQueue.TryEnqueue(RenderTrack);
    private void OnQueueChanged() => DispatcherQueue.TryEnqueue(RenderQueue);
    private void OnPlayStateChanged() => DispatcherQueue.TryEnqueue(RenderPlayState);
    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(UpdateLike);
    private void OnSettingsChanged(Settings settings) => DispatcherQueue.TryEnqueue(ApplySkipSettings);

    // MARK: the song

    private void RenderTrack()
    {
        var song = Playback.CurrentSong;
        Idle.Visibility = song is null ? Visibility.Visible : Visibility.Collapsed;
        Layout.Visibility = song is null ? Visibility.Collapsed : Visibility.Visible;
        if (song is null)
        {
            return;
        }

        TitleText.Text = song.Title;
        ArtistLink.Content = song.ArtistLine;
        ArtistLink.IsEnabled = song.Artists.Any(a => a.Id is not null);
        SkipBackButton.IsEnabled = SkipForwardButton.IsEnabled = true;
        Art.SetSource(Cover, song.Artwork);
        Art.SetSource(CompactCoverImage, song.Artwork);
        Art.SetSource(BackdropArt, song.Artwork);
        FailureBanner.Visibility = Visibility.Collapsed;
        UpdateLike();
        OnPositionTick();
        _ = PaintAsync(song);
        _ = LoadLyricsAsync(song);
    }

    // MARK: lyrics

    /// <summary>
    /// The lyrics for this song, from the services that answer best. They are asked for
    /// when the song starts (the panel is not the only thing that wants them), so the
    /// words are usually already there by the time the panel opens.
    /// </summary>
    private async System.Threading.Tasks.Task LoadLyricsAsync(Song song)
    {
        if (_lyricsFor == song.Id)
        {
            return;
        }

        _lyricsFor = song.Id;
        LyricsEmpty.Visibility = Visibility.Collapsed;
        _lyrics?.Load(null);

        Lyrics? lyrics;
        try
        {
            lyrics = await LyricsService.For(song);
        }
        catch (Exception)
        {
            lyrics = null;
        }

        if (_lyricsFor != song.Id)
        {
            return; // another song took over while the services were answering
        }

        _lyrics?.Load(lyrics);
        _lyrics?.Offset = App.Library.LyricsOffset(song.Id);
        if (lyrics is null)
        {
            LyricsEmpty.Visibility = Visibility.Visible;
            return;
        }

        _lyrics?.PlayEntrance();
    }
    /// <summary>The background takes the cover's three strongest colours.</summary>
    private async System.Threading.Tasks.Task PaintAsync(Song song)
    {
        if (_paletteFor == song.Id) return;
        _paletteFor = song.Id;
        // The last song's tint goes at once; the blurred cover carries the page meanwhile.
        Fade(BlobA, 0);
        Fade(BlobB, 0);
        if (await ArtAccent.PaletteAsync(song.Artwork) is not { Length: >= 2 } palette || _paletteFor != song.Id)
        {
            return;
        }

        BlobAColor.Color = palette[0];
        BlobAFade.Color = Windows.UI.Color.FromArgb(0, palette[0].R, palette[0].G, palette[0].B);
        BlobBColor.Color = palette[1];
        BlobBFade.Color = Windows.UI.Color.FromArgb(0, palette[1].R, palette[1].G, palette[1].B);
        Fade(BlobA, 0.55);
        Fade(BlobB, 0.45);
    }

    /// <summary>A plain storyboard fade. [scar] An OpacityTransition on these drifting
    /// rectangles fails the whole page's XAML load (XamlParseException at InitializeComponent).</summary>
    private static void Fade(UIElement element, double to)
    {
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(800),
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
        var story = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        story.Children.Add(animation);
        story.Begin();
    }

    private void UpdateLike()
    {
        var liked = Playback.CurrentSong is { } song && App.Library.IsLiked(song);
        LikeIcon.Glyph = liked ? "" : "";
        LikeIcon.Foreground = new SolidColorBrush(liked ? Windows.UI.Color.FromArgb(255, 0xFF, 0x4F, 0x8B) : Colors.White);
        var label = liked ? "Remove from Liked" : "Add to Liked";
        ToolTipService.SetToolTip(LikeButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LikeButton, label);
    }

    private void RenderPlayState()
    {
        var playing = Playback.IsPlaying;
        var loading = Playback.IsLoading;
        PlayIcon.Glyph = playing ? "" : "";
        PlayIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(PlayButton, playing ? "Pause (Space)" : "Play (Space)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayButton, playing ? "Pause" : "Play");

        // The cover sits full size while sound plays, a little back while paused or loading.
        (playing && !loading ? CoverIn : CoverOut).Begin();
        // The cover's colours drift behind the player, unless the listener turned that off
        // or the system has animations turned off.
        var animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled &&
            Koda.Core.Settings.Current.AnimatesPlayerBackground;
        if (playing && animate) Drift.Begin();
        else Drift.Pause();
    }

    private void OnPositionTick()
    {
        var duration = Playback.DurationSeconds;
        if (_scrubbing || duration <= 0)
        {
            return;
        }

        Scrubber.Maximum = duration;
        Scrubber.Value = Math.Clamp(Playback.PositionSeconds, 0, duration);
        UpdateTimes(Scrubber.Value);
        _lyrics?.SetPosition(Playback.PositionSeconds);
        _lyrics?.Follow();
    }

    private void UpdateTimes(double position)
    {
        var duration = Playback.DurationSeconds;
        ElapsedText.Text = Format(position);
        RemainingText.Text = "-" + Format(Math.Max(0, duration - position));
    }

    private static string Format(double seconds)
    {
        var total = (long)Math.Floor(Math.Max(0, seconds));
        return total >= 3600 ? $"{total / 3600}:{total % 3600 / 60:D2}:{total % 60:D2}" : $"{total / 60}:{total % 60:D2}";
    }

    // MARK: the queue

    private void RenderQueue()
    {
        ShuffleToggle.IsChecked = Playback.Queue.IsShuffled;
        var repeat = Playback.Queue.Repeat;
        RepeatToggle.IsChecked = repeat != RepeatMode.Off;
        RepeatIcon.Glyph = repeat == RepeatMode.One ? "" : "";
        RepeatText.Text = repeat == RepeatMode.One ? "Repeat one" : "Repeat";
        AutoplayToggle.IsChecked = Playback.Autoplay;

        if (!_dragging)
        {
            var upcoming = Playback.Queue.Upcoming;
            // Only what changed: the list keeps its scroll while autoplay appends.
            var same = upcoming.Count >= _upcoming.Count && _upcoming.Select((item, i) => upcoming[i].Id == item.Id).All(x => x);
            if (same)
            {
                foreach (var item in upcoming.Skip(_upcoming.Count)) _upcoming.Add(item);
            }
            else
            {
                _upcoming.Clear();
                foreach (var item in upcoming) _upcoming.Add(item);
            }
        }

        MoreSkeleton.Content = Playback.IsExtending ? Skeletons.Rows(3) : null;
        EndNote.Visibility = !Playback.Autoplay && Playback.Queue.Repeat == RepeatMode.Off ? Visibility.Visible : Visibility.Collapsed;
        EndNote.Text = _upcoming.Count == 0
            ? "Turn on Autoplay to keep going with songs like this one."
            : "The queue ends here. Autoplay would keep going with songs like the last one.";
        RenderQueueHeader();
    }

    private void RenderQueueHeader()
    {
        UpNextTitle.Text = _upcoming.Count == 0 ? "Nothing up next" : "Up Next";
        UndoLink.Visibility = _cleared is not null ? Visibility.Visible : Visibility.Collapsed;
        ClearLink.Visibility = _cleared is null && _upcoming.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUpNextLoaded(object sender, RoutedEventArgs e)
    {
        _queueScroller ??= Descendant<ScrollViewer>(UpNext);
        if (_queueScroller is { } scroller)
        {
            // Near the end of the list, Autoplay fetches more (Mac: the queue never runs dry).
            scroller.ViewChanged += (_, _) =>
            {
                if (scroller.ScrollableHeight > 0 && scroller.VerticalOffset > scroller.ScrollableHeight - 400)
                {
                    _ = Playback.LoadMoreAsync();
                }
            };
        }
    }

    private void OnUpNextClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QueueItem item) _ = Playback.JumpAsync(item.Id);
    }

    private void OnUpNextContext(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement { DataContext: QueueItem item })
        {
            return;
        }

        var menu = SongMenu.Build(item.Song, [.. Playback.Queue.Items.Select(i => i.Song)]);
        var remove = new MenuFlyoutItem { Text = "Remove from queue", Icon = new FontIcon { Glyph = "" } };
        remove.Click += (_, _) => Playback.Remove(item.Id);
        menu.Items.Insert(0, remove);
        menu.Items.Insert(1, new MenuFlyoutSeparator());
        if (args.TryGetPosition(UpNext, out var at)) menu.ShowAt(UpNext, at);
        else menu.ShowAt(UpNext);
        args.Handled = true;
    }

    private void OnDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragging = true;
        _dragged = e.Items.FirstOrDefault() as QueueItem;
        _dragFrom = _dragged is null ? -1 : _upcoming.IndexOf(_dragged);
    }

    private void OnDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _dragging = false;
        if (_dragged is { } item && _dragFrom >= 0 && _upcoming.IndexOf(item) is var to and >= 0 && to != _dragFrom)
        {
            Playback.MoveUpcoming(_dragFrom, to);
        }

        _dragged = null;
        RenderQueue();
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e) => Playback.SetShuffle(ShuffleToggle.IsChecked == true);

    private void OnRepeatClick(object sender, RoutedEventArgs e) => Playback.CycleRepeat();

    private void OnAutoplayClick(object sender, RoutedEventArgs e) => Playback.SetAutoplay(AutoplayToggle.IsChecked == true);

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _cleared = [.. Playback.Queue.Upcoming.Select(i => i.Song)];
        Playback.ClearUpcoming();
        _undoTimer.Start();
        RenderQueueHeader();
    }

    /// <summary>Autoplay may have filled the queue again since; what was cleared comes back in its place.</summary>
    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_cleared is not { } songs) return;
        _undoTimer.Stop();
        _cleared = null;
        Playback.ClearUpcoming();
        Playback.AddToQueue(songs);
    }

    // MARK: controls

    private void OnPlayClick(object sender, RoutedEventArgs e) => Playback.Toggle();
    private void OnPrevClick(object sender, RoutedEventArgs e) => _ = Playback.PreviousAsync();
    private void OnNextClick(object sender, RoutedEventArgs e) => _ = Playback.NextAsync();
    private void OnSkipBackClick(object sender, RoutedEventArgs e) => Playback.Skip(-App.Settings.SkipSeconds);
    private void OnSkipForwardClick(object sender, RoutedEventArgs e) => Playback.Skip(App.Settings.SkipSeconds);

    /// <summary>The skip ends follow the listener's interval in Settings, or go away when
    /// they turned the buttons off; the labels carry the seconds and the shortcut.</summary>
    private void ApplySkipSettings()
    {
        var settings = App.Settings;
        var seconds = settings.SkipSeconds;
        var shown = settings.ShowsSkipButtons;
        var visibility = shown ? Visibility.Visible : Visibility.Collapsed;

        SkipBackButton.Visibility = SkipForwardButton.Visibility = visibility;
        SkipBackColumn.Width = SkipForwardColumn.Width = shown ? GridLength.Auto : new GridLength(0);
        SkipBackIcon.Seconds = SkipForwardIcon.Seconds = seconds;

        var back = $"Back {seconds} seconds";
        var ahead = $"Ahead {seconds} seconds";
        ToolTipService.SetToolTip(SkipBackButton, $"{back} (Ctrl+Alt+Left)");
        ToolTipService.SetToolTip(SkipForwardButton, $"{ahead} (Ctrl+Alt+Right)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SkipBackButton, back);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SkipForwardButton, ahead);
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Playback.SetVolumePercent(e.NewValue);
        App.SetVolume(e.NewValue);
    }

    private void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentSong is { } song) App.Library.ToggleLike(song);
    }

    private void OnArtistClick(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentSong?.Artists.FirstOrDefault(a => a.Id is not null) is { Id: { } id })
        {
            Navigator.Go(typeof(ArtistPage), id);
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentSong is { } song)
        {
            SongMenu.Build(song, [.. Playback.Queue.Items.Select(i => i.Song)]).ShowAt(MoreButton);
        }
    }

    private void OnOutputClick(object sender, RoutedEventArgs e) => AudioOutputMenu.Show(OutputButton);

    /// <summary>The overlay is the player in miniature: leaving the full page first keeps
    /// the library from sitting behind it in a second player view.</summary>
    private void OnMiniClick(object sender, RoutedEventArgs e)
    {
        App.Shell?.ShowMiniPlayer();
        Close();
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => Playback.Toggle();

    private void OnQueueToggleClick(object sender, RoutedEventArgs e)
    {
        _queueOpen = QueueToggle.IsChecked == true;
        if (_queueOpen)
        {
            _lyricsOpen = false;
        }

        ApplyLayout();
    }

    private void OnLyricsToggleClick(object sender, RoutedEventArgs e)
    {
        _lyricsOpen = LyricsToggle.IsChecked == true;
        if (_lyricsOpen)
        {
            _queueOpen = false;
        }

        ApplyLayout();
        if (_lyricsOpen)
        {
            _lyrics?.PlayEntrance();
            _lyrics?.SetPosition(Playback.PositionSeconds);
            _lyrics?.Follow();
        }
        else
        {
            _lyrics?.Pause();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Navigator.Go(typeof(HomePage));
    }

    // MARK: layout

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The blooms are larger than the page so their edges never show as they drift.
        var mx = e.NewSize.Width * 0.35;
        var my = e.NewSize.Height * 0.35;
        BlobA.Margin = BlobB.Margin = new Thickness(-mx, -my, -mx, -my);
        // The blurred cover a little larger than the page, so its soft edges stay out of view.
        BackdropArt.Margin = new Thickness(-e.NewSize.Width * 0.12, -e.NewSize.Height * 0.12, -e.NewSize.Width * 0.12, -e.NewSize.Height * 0.12);
        var wide = e.NewSize.Width >= 900 && e.NewSize.Height >= 560;
        if (wide != _wide)
        {
            _wide = wide;
            ApplyLayout();
        }
    }

    /// <summary>Wide: the player left, the queue panel beside it. Narrow: one column, the queue
    /// taking the cover's place with a compact header above it.</summary>
    private void ApplyLayout()
    {
        QueueToggle.IsChecked = _queueOpen;
        LyricsToggle.IsChecked = _lyricsOpen;
        var open = _queueOpen || _lyricsOpen;

        PlaceQueuePanel();
        PlaceLyricsPanel();

        if (_wide)
        {
            QueuePanel.Visibility = _queueOpen ? Visibility.Visible : Visibility.Collapsed;
            LyricsView.Visibility = _lyricsOpen ? Visibility.Visible : Visibility.Collapsed;
            PanelColumn.Width = open ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
            Layout.ColumnSpacing = open ? 64 : 0;
            CoverBox.Visibility = Visibility.Visible;
            CoverRow.Height = new GridLength(1, GridUnitType.Star);
            NarrowQueueRow.Height = new GridLength(0);
            ShowCompactCover(false);
        }
        else
        {
            QueuePanel.Visibility = _queueOpen ? Visibility.Visible : Visibility.Collapsed;
            LyricsView.Visibility = _lyricsOpen ? Visibility.Visible : Visibility.Collapsed;
            PanelColumn.Width = new GridLength(0);
            Layout.ColumnSpacing = 0;
            CoverBox.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            CoverRow.Height = open ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            NarrowQueueRow.Height = open ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ShowCompactCover(open);
        }

        PlayerPanel.MaxWidth = _wide ? 460 : 560;
    }

    /// <summary>
    /// Where the queue panel lives: in the layout's panel column when the window is wide,
    /// in the cover's place when it is narrow.
    ///
    /// The place is kept as state, not read from the element: an element that is a
    /// content presenter's content has no Parent, so asking the element where it is
    /// answers "nowhere" - which is how the panel used to be left behind in the narrow
    /// host after the window grew again. An element can also only be installed in one
    /// place at a time, so it is released before it is moved.
    /// </summary>
    private void PlaceQueuePanel()
    {
        var want = _wide ? PanelPlace.Layout : PanelPlace.Narrow;
        if (_queuePlace == want)
        {
            return;
        }

        if (_queuePlace == PanelPlace.Narrow)
        {
            NarrowQueueHost.Content = null;
        }
        else
        {
            Layout.Children.Remove(QueuePanel);
        }

        if (want == PanelPlace.Narrow)
        {
            NarrowQueueHost.Content = QueuePanel;
        }
        else
        {
            Layout.Children.Add(QueuePanel);
            Grid.SetRow(QueuePanel, 1);
            Grid.SetColumn(QueuePanel, 1);
        }

        _queuePlace = want;
    }

    /// <summary>The same two places for the lyrics panel, with a host of its own so the two
    /// panels never have to trade places.</summary>
    private void PlaceLyricsPanel()
    {
        var want = _wide ? PanelPlace.Layout : PanelPlace.Narrow;
        if (_lyricsPlace == want)
        {
            return;
        }

        if (_lyricsPlace == PanelPlace.Narrow)
        {
            NarrowLyricsHost.Content = null;
        }
        else
        {
            Layout.Children.Remove(LyricsView);
        }

        if (want == PanelPlace.Narrow)
        {
            NarrowLyricsHost.Content = LyricsView;
        }
        else
        {
            Layout.Children.Add(LyricsView);
            Grid.SetRow(LyricsView, 1);
            Grid.SetColumn(LyricsView, 1);
        }

        _lyricsPlace = want;
    }

    private void ShowCompactCover(bool show)
    {
        CompactCover.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CompactCoverColumn.Width = show ? new GridLength(64) : new GridLength(0);
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }

        return null;
    }
}



