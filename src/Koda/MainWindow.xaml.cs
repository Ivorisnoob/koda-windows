using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Koda.Helpers;
using Koda.Interop;
using Koda.Pages;
using Koda.Playback;
using Koda.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Media;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;
using YouTubeLink = Koda.Core.YouTubeLink;

namespace Koda;

public sealed partial class MainWindow : Window
{
    private static MainWindow? _current;

    private const int MinWindowDipWidth = 700;
    private const int MinWindowDipHeight = 520;

    /// <summary>The steps Ctrl + and Ctrl - walk through; 1 is 100%. The bottom of the
    /// range is for very large displays, where the window has room for a lot more.</summary>
    private static readonly double[] ZoomSteps = [0.2, 0.25, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5];

    /// <summary>The title bar is the standard Windows height. Its caption buttons are
    /// drawn by Windows and cannot be made smaller, so this is the floor.</summary>
    private const double TitleBarHeight = 32;

    /// <summary>The title bar's own content follows the UI zoom, but not below 75%:
    /// a back button and a search field still have to be usable.</summary>
    private const double TitleBarMinScale = 0.75;

    private TrayIconHost? _tray;
    private Taskbar? _taskbar;
    private MiniPlayerWindow? _mini;
    private readonly SmtcController _smtc = new();
    private nint _previousWndProc;
    private bool _placementApplied;
    private bool _scrubbing;
    private bool _suppressVolumePersistence;
    private string? _currentTag;
    private string? _notifiedSongId;
    private string _playlistSignature = "";
    private bool _hasUpNext;
    private readonly List<NavigationViewItem> _playlistItems = [];
    private DispatcherQueueTimer? _volumeSaveTimer;
    private DispatcherQueueTimer? _statusTimer;
    private DispatcherQueueTimer? _suggestTimer;
    private CancellationTokenSource? _suggestCancel;
    private YouTubeLink? _linkSuggestion;
    private DispatcherQueueTimer? _zoomTimer;
    private double _zoom = 1;
    private double _titleScale = 1;
    private bool _shutdown;

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);
    private static readonly WndProc SubclassedProc = OnWndProc;

    private PlaybackService Playback => PlaybackService.Instance;

    public MainWindow()
    {
        InitializeComponent();
        _current = this;

        Title = "Koda";
        SystemBackdrop = new MicaBackdrop();

        var appWindow = AppWindow;
        appWindow.Resize(new SizeInt32(1180, 780));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));

        // The title bar is ours: Mica runs to the top edge, caption buttons float on it.
        // Windows' standard height is the thin one; the bar's content shrinks with the UI zoom.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        Root.RowDefinitions[0].Height = new GridLength(TitleBarHeight);
        Root.ActualThemeChanged += (_, _) => PaintCaptionButtons();
        PaintCaptionButtons();
        AppTitleBar.SizeChanged += OnTitleBarPartsChanged;
        TitleBarContent.SizeChanged += OnTitleBarPartsChanged;
        Activated += OnActivated;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowPlacement.Restore(hwnd);
        _previousWndProc = Win32.SetWindowLongPtr(
            hwnd, Win32.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(SubclassedProc));

        _smtc.Bind(hwnd, OnSmtcButton);
        _taskbar = new Taskbar(hwnd,
            () => _ = Playback.PreviousAsync(),
            Playback.Toggle,
            () => _ = Playback.NextAsync());

        _tray = new TrayIconHost(
            hwnd,
            Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"),
            () => Playback.IsPlaying ? "Pause" : "Play",
            OnTrayCommand);

        // The taskbar's Jump List: the transport without opening the window.
        JumpList.Apply();

        var dispatch = DispatcherQueue.GetForCurrentThread();
        // Energy Saver quietly stills the decorative motion; the pages that move follow it.
        EnergySaver.Watch(dispatch);
        Playback.Connect(dispatch, _smtc);
        Playback.TrackChanged += OnTrackChanged;
        Playback.QueueChanged += OnQueueModesChanged;
        Playback.PlayStateChanged += OnPlayStateChanged;
        Playback.PositionTick += OnPositionTick;
        Playback.StatusRaised += OnStatusRaised;
        App.Library.Changed += () => dispatch.TryEnqueue(RefreshSidebar);
        App.AudioStore.Changed += () => dispatch.TryEnqueue(RefreshSidebar);
        App.Library.Changed += () => dispatch.TryEnqueue(UpdateLike);
        App.Settings.Changed += _ => dispatch.TryEnqueue(ApplySkipSettings);

        // A toast clicked while Koda was closed waited for the playback service and the
        // bar's events; now its action can run against a working app.
        Notifications.Attach(this);

        Scrubbing.Attach(Scrubber, on => _scrubbing = on, Playback.SeekSeconds, () => Playback.CurrentSong is not null);
        Scrubber.ValueChanged += (_, _) =>
        {
            if (_scrubbing) ElapsedText.Text = FormatTime(Scrubber.Value);
        };

        _suppressVolumePersistence = true;
        VolumeSlider.Value = App.Volume;
        _suppressVolumePersistence = false;

        _volumeSaveTimer = dispatch.CreateTimer();
        _volumeSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _volumeSaveTimer.IsRepeating = false;
        _volumeSaveTimer.Tick += (_, _) => App.SetVolume(VolumeSlider.Value);

        _statusTimer = dispatch.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(6);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick += (_, _) => StatusRibbon.IsOpen = false;

        _suggestTimer = dispatch.CreateTimer();
        _suggestTimer.Interval = TimeSpan.FromMilliseconds(220);
        _suggestTimer.IsRepeating = false;
        _suggestTimer.Tick += (_, _) => _ = LoadSuggestionsAsync(TitleSearch.Text);

        _zoomTimer = dispatch.CreateTimer();
        _zoomTimer.Interval = TimeSpan.FromSeconds(1.4);
        _zoomTimer.IsRepeating = false;
        _zoomTimer.Tick += (_, _) => ZoomBadge.Visibility = Visibility.Collapsed;

        ZoomHost.SizeChanged += (_, _) => ApplyZoom();
        RestoreZoom();
        ApplyTitleBarScale();

        AddShortcuts();
        ApplySkipSettings();

        ContentFrame.Navigated += OnContentNavigated;
        Nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdatePaneChrome());
        NavigateRoot(typeof(HomePage), null, "home");
        RefreshSidebar();

        OnTrackChanged();
        OnPlayStateChanged();

        Closed += (_, _) =>
        {
            // A WinUI app keeps its message loop (and its XAML heap) alive after the last
            // window closes; Application.Exit alone still left a windowless process behind,
            // growing by 10-24 MB a minute. Closing the window is the listener quitting:
            // clean up once, then end the process.
            try
            {
                Shutdown();
            }
            catch (Exception error)
            {
                App.WriteError("closing Koda", error);
            }
            finally
            {
                Environment.Exit(0);
            }
        };
    }

    /// <summary>The window handle, for the shell integrations that need one.</summary>
    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    // MARK: title bar

    private void PaintCaptionButtons()
    {
        var bar = AppWindow.TitleBar;
        var dark = Root.ActualTheme == ElementTheme.Dark;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x80, 0, 0, 0);
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x08, 0, 0, 0);
        bar.ButtonPressedForegroundColor = bar.ButtonForegroundColor;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // Windows dims an inactive window's title; ours follows.
        AppTitle.Foreground = (Brush)Application.Current.Resources[
            args.WindowActivationState == WindowActivationState.Deactivated ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush"];

        // SetWindowPlacement remembers the normal rectangle before the window is shown,
        // but WinUI activates it normal; the saved maximised state is applied once the
        // window is really on screen, so tearing it down starts from the same place.
        if (!_placementApplied)
        {
            _placementApplied = true;
            if (App.Settings.WindowMaximized)
            {
                (AppWindow.Presenter as OverlappedPresenter)?.Maximize();
            }
        }
    }

    /// <summary>The caption buttons' space is kept clear, and the back button and search field stay clickable.</summary>
    private void OnTitleBarPartsChanged(object sender, SizeChangedEventArgs args) => ApplyTitleBarScale();

    private void UpdateTitleBarRegions()
    {
        if (AppTitleBar.XamlRoot is null)
        {
            return;
        }

        var scale = AppTitleBar.XamlRoot.RasterizationScale;
        // The title bar content lays out at the room divided by the title scale, then is
        // scaled back up, so its insets are in that layout space too.
        LeftInset.Width = new GridLength(AppWindow.TitleBar.LeftInset / scale / _titleScale);
        RightInset.Width = new GridLength(AppWindow.TitleBar.RightInset / scale / _titleScale);

        var rects = new List<RectInt32>();
        foreach (var element in new FrameworkElement[] { BackButton, TitleSearch })
        {
            var bounds = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            rects.Add(new RectInt32(
                (int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale)));
        }

        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
    }

    // MARK: navigation

    public void NavigateTo(Type page, object? parameter = null)
    {
        if (page == typeof(SearchPage) && ContentFrame.Content is SearchPage open && parameter is string query)
        {
            open.Search(query);
            return;
        }

        try
        {
            ContentFrame.Navigate(page, parameter, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
        }
        catch (Exception error)
        {
            App.WriteError($"Could not open {page.Name}", error);
            Note($"{page.Name} couldn't open. The reason was written to crash.txt.");
            if (_currentTag is { } current)
            {
                SelectTag(current);
            }
        }
    }

    /// <summary>A sidebar section's own page: what is behind it is dropped, so Back stops here.</summary>
    public void NavigateRoot(Type page, object? parameter = null, string? tag = null)
    {
        if (tag is not null && tag == _currentTag && !ContentFrame.CanGoBack && ContentFrame.CurrentSourcePageType == page)
        {
            return;
        }

        try
        {
            ContentFrame.Navigate(page, parameter, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
            ContentFrame.BackStack.Clear();
            ContentFrame.ForwardStack.Clear();
            // Navigated is raised during Navigate, before the stack is cleared: without this
            // the button stays lit on a section's first page with nowhere to go.
            UpdateBackButton();
        }
        catch (Exception error)
        {
            App.WriteError($"Could not open {page.Name}", error);
            Note($"{page.Name} couldn't open. The reason was written to crash.txt.");
            if (_currentTag is { } current)
            {
                SelectTag(current);
            }
        }
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            NavigateRoot(typeof(SettingsPage), null, "settings");
            return;
        }

        var tag = (args.InvokedItemContainer as NavigationViewItem)?.Tag as string;
        switch (tag)
        {
            case "home":
                NavigateRoot(typeof(HomePage), null, tag);
                break;
            case "browse":
                NavigateRoot(typeof(BrowsePage), null, tag);
                break;
            case "search":
                ShowSearch();
                break;
            case "recent":
            case "liked":
            case "downloads":
            case "offline":
                NavigateRoot(typeof(SongsPage), tag, tag);
                break;
            case "history":
                NavigateRoot(typeof(HistoryPage), null, tag);
                break;
            case "albums":
            case "playlists":
            case "artists":
                NavigateRoot(typeof(LibraryGridPage), tag, tag);
                break;
            case "stats":
                NavigateRoot(typeof(StatsPage), null, tag);
                break;
            case "glance":
                NavigateRoot(typeof(StatsPage), null, "stats");
                break;
            case "lastfm":
                NavigateRoot(typeof(LastFmPage), null, tag);
                break;
            case "up-next":
                ShowQueue();
                break;
            case "new-playlist":
                _ = Dialogs.CreatePlaylistAsync([]);
                break;
            case not null when tag.StartsWith("playlist:", StringComparison.Ordinal)
                                && Guid.TryParse(tag["playlist:".Length..], out var id):
                NavigateRoot(typeof(UserPlaylistPage), id, tag);
                break;
        }
    }

    /// <summary>Search results is a place only once there is a search; the sidebar gains it then.</summary>
    public void SearchFor(string query)
    {
        query = query.Trim();
        SearchResultsItem.Visibility = Visibility.Visible;
        TitleSearch.Text = query;
        if (ContentFrame.Content is SearchPage open)
        {
            if (query.Length > 0)
            {
                open.Search(query);
            }

            SelectTag("search");
        }
        else
        {
            NavigateRoot(typeof(SearchPage), query.Length > 0 ? query : null, "search");
        }

        TitleSearch.Focus(FocusState.Programmatic);
    }

    private void ShowSearch()
    {
        var query = TitleSearch.Text.Trim();
        if (query.Length > 0)
        {
            SearchFor(query);
            return;
        }

        SearchResultsItem.Visibility = Visibility.Visible;
        NavigateRoot(typeof(SearchPage), null, "search");
        TitleSearch.Focus(FocusState.Programmatic);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void UpdateBackButton() => BackButton.IsEnabled = ContentFrame.CanGoBack;

    private void OnContentNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs args)
    {
        UpdateBackButton();
        // Now Playing is the whole player: the bar steps aside while it is open.
        PlayerBar.Visibility = args.SourcePageType == typeof(NowPlayingPage) ? Visibility.Collapsed : Visibility.Visible;
        if (args.SourcePageType == typeof(SearchPage))
        {
            SearchResultsItem.Visibility = Visibility.Visible;
        }

        // The pane shows the section the page belongs to; detail pages keep their section lit.
        _currentTag = TagFor(args.SourcePageType, args.Parameter);
        if (_currentTag is null)
        {
            return;
        }

        if (_currentTag == "settings")
        {
            Nav.SelectedItem = Nav.SettingsItem;
        }
        else
        {
            SelectTag(_currentTag);
        }
    }

    private static string? TagFor(Type page, object? parameter) => page.Name switch
    {
        nameof(HomePage) => "home",
        nameof(BrowsePage) or nameof(MoodPage) => "browse",
        nameof(SearchPage) => "search",
        nameof(HistoryPage) => "history",
        nameof(StatsPage) => "stats",
        nameof(LastFmPage) => "lastfm",
        nameof(SongsPage) => (parameter as string) switch
        {
            "recent" => "recent",
            "downloads" => "downloads",
            "offline" => "offline",
            _ => "liked",
        },
        nameof(LibraryGridPage) => (parameter as string) switch
        {
            "playlists" => "playlists",
            "artists" => "artists",
            _ => "albums",
        },
        nameof(UserPlaylistPage) => parameter is Guid id ? $"playlist:{id}" : null,
        nameof(AlbumPage) => "albums",
        nameof(PlaylistPage) => "playlists",
        nameof(ArtistPage) => "artists",
        nameof(SettingsPage) => "settings",
        _ => null,
    };

    private void SelectTag(string tag)
    {
        if (tag == "settings")
        {
            Nav.SelectedItem = Nav.SettingsItem;
            return;
        }

        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => i.Tag as string == tag);
        if (item is not null)
        {
            Nav.SelectedItem = item;
        }
    }

    // MARK: sidebar counts and foot

    private void RefreshSidebar()
    {
        SetBadge(LikedItem, App.Library.Liked.Length);
        SetBadge(DownloadsItem, App.AudioStore.Downloads.Length);
        RebuildPlaylists();
        UpdateGlance();
        UpdateUpNext();
    }

    private static void SetBadge(NavigationViewItem item, int count)
    {
        if (count <= 0)
        {
            item.InfoBadge = null;
        }
        else if (item.InfoBadge is InfoBadge badge)
        {
            badge.Value = count;
        }
        else
        {
            item.InfoBadge = new InfoBadge { Value = count };
        }
    }

    /// <summary>The listener's playlists in the pane, between their header and New Playlist.</summary>
    private void RebuildPlaylists()
    {
        var playlists = App.Library.UserPlaylists;
        var signature = string.Join("|", playlists.Select(p => $"{p.Id}:{p.Name}:{p.Songs.Length}:{p.Modified.UtcTicks}"));
        if (signature == _playlistSignature)
        {
            return;
        }

        _playlistSignature = signature;
        foreach (var old in _playlistItems)
        {
            Nav.MenuItems.Remove(old);
        }

        _playlistItems.Clear();
        var at = Nav.MenuItems.IndexOf(NewPlaylistItem);
        if (at < 0)
        {
            at = Nav.MenuItems.Count;
        }

        foreach (var playlist in playlists)
        {
            var item = BuildPlaylistItem(playlist);
            Nav.MenuItems.Insert(at++, item);
            _playlistItems.Add(item);
        }

        // A new playlist from a song menu, or a rename: the pane follows the selection.
        if (_currentTag is { } tag && tag.StartsWith("playlist:", StringComparison.Ordinal))
        {
            SelectTag(tag);
        }
    }

    private NavigationViewItem BuildPlaylistItem(Koda.Core.UserPlaylist playlist)
    {
        var item = new NavigationViewItem
        {
            Content = playlist.Name,
            Tag = $"playlist:{playlist.Id}",
            Icon = new FontIcon { Glyph = "\uE8FD" },
        };

        var menu = new MenuFlyout();
        var play = SongMenu.Item("Play", "\uE768", () =>
        {
            if (playlist.Songs.Length > 0)
            {
                _ = Playback.PlayNowAsync(playlist.Songs[0], playlist.Songs);
            }
        });
        play.IsEnabled = playlist.Songs.Length > 0;
        menu.Items.Add(play);
        var queue = SongMenu.Item("Add to queue", "\uE71C", () => Playback.AddToQueue(playlist.Songs));
        queue.IsEnabled = playlist.Songs.Length > 0;
        menu.Items.Add(queue);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(SongMenu.Item("Rename…", "\uE70F", () => _ = Dialogs.RenamePlaylistAsync(playlist)));
        menu.Items.Add(SongMenu.Item("Delete playlist…", "\uE74D", () => _ = DeletePlaylistAsync(playlist)));
        item.ContextFlyout = menu;
        return item;
    }

    private async System.Threading.Tasks.Task DeletePlaylistAsync(Koda.Core.UserPlaylist playlist)
    {
        if (!await Dialogs.ConfirmAsync($"Delete “{playlist.Name}”?",
                "The songs stay wherever else you saved them.", "Delete Playlist"))
        {
            return;
        }

        if (_currentTag == $"playlist:{playlist.Id}")
        {
            NavigateRoot(typeof(HomePage), null, "home");
        }

        App.Library.DeletePlaylist(playlist.Id);
    }

    private void UpdateGlance()
    {
        GlanceListened.Text = $"{Format.Listening(App.Library.ListeningOn(DateTimeOffset.Now))} today";
        var streak = App.Library.Streak();
        GlanceStreak.Text = streak == 0 ? "Start a streak" : Format.StreakLine(streak);
        var label = $"{GlanceListened.Text}, {GlanceStreak.Text}. Open Listening Stats";
        ToolTipService.SetToolTip(GlanceItem, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GlanceItem, label);
    }

    /// <summary>The song after this one, as a row of the pane; invoked, it opens the queue.</summary>
    private void UpdateUpNext()
    {
        var next = Playback.Queue.Upcoming.FirstOrDefault();
        _hasUpNext = next is not null;
        UpNextTitle.Text = next?.Song.Title ?? "";
        UpNextArtist.Text = next is null || string.IsNullOrEmpty(next.Song.ArtistLine)
            ? "Up next"
            : $"Up next · {next.Song.ArtistLine}";
        var label = next is null ? "Up next" : $"Up next: {next.Song}. Open the queue";
        ToolTipService.SetToolTip(UpNextItem, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UpNextItem, label);
        UpdatePaneChrome();
    }

    private void UpdatePaneChrome() =>
        UpNextItem.Visibility = _hasUpNext ? Visibility.Visible : Visibility.Collapsed;

    private void ShowQueue()
    {
        if (Playback.CurrentSong is not null && ContentFrame.Content is not NowPlayingPage)
        {
            ContentFrame.Navigate(typeof(NowPlayingPage), "queue", new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
        }
    }

    // MARK: search in the title bar

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _suggestTimer?.Stop();
        _linkSuggestion = YouTubeLink.Parse(sender.Text);
        if (sender.Text.Trim().Length == 0)
        {
            sender.ItemsSource = App.Library.Searches.Take(8).ToList();
            return;
        }

        // A pasted YouTube link is offered instead of searching for the URL itself.
        if (_linkSuggestion is { } link)
        {
            _suggestCancel?.Cancel();
            sender.ItemsSource = new[] { link.OpenTitle };
            sender.IsSuggestionListOpen = true;
            return;
        }

        _suggestTimer?.Start();
    }

    private async System.Threading.Tasks.Task LoadSuggestionsAsync(string text)
    {
        // The link row is the only suggestion for a link, even if a tick was already queued.
        if (YouTubeLink.Parse(text) is not null)
        {
            return;
        }

        _suggestCancel?.Cancel();
        var cancel = _suggestCancel = new CancellationTokenSource();
        try
        {
            var suggestions = await EngineHost.Music.SuggestionsAsync(text.Trim(), cancel.Token);
            if (!cancel.IsCancellationRequested && TitleSearch.Text == text)
            {
                TitleSearch.ItemsSource = suggestions.Take(8).ToList();
            }
        }
        catch (Exception)
        {
            // Suggestions are a nicety; the search itself still works.
        }
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var chosen = args.ChosenSuggestion as string;
        var query = (chosen ?? args.QueryText).Trim();
        var link = chosen is not null && _linkSuggestion is { } pending && chosen == pending.OpenTitle
            ? pending
            : YouTubeLink.Parse(args.QueryText);
        _suggestTimer?.Stop();
        _suggestCancel?.Cancel();
        sender.ItemsSource = null;

        // A pasted YouTube or YouTube Music link plays (or opens) what it points at.
        if (link is not null)
        {
            _linkSuggestion = null;
            sender.Text = "";
            OpenLink(link);
            return;
        }

        if (query.Length == 0)
        {
            return;
        }

        sender.Text = query;
        SearchFor(query);
    }

    /// <summary>A link from search: a song plays with its radio, anything else opens its page.</summary>
    private void OpenLink(YouTubeLink link)
    {
        switch (link)
        {
            case YouTubeLink.Song song:
                _ = Playback.PlayLinkedSongAsync(song.VideoId);
                break;
            case YouTubeLink.Album album:
                NavigateTo(typeof(AlbumPage), album.BrowseId);
                break;
            case YouTubeLink.Playlist playlist:
                NavigateTo(typeof(PlaylistPage), playlist.Id);
                break;
            case YouTubeLink.Artist artist:
                NavigateTo(typeof(ArtistPage), artist.ChannelId);
                break;
        }
    }

    public void SetSearchText(string query) => TitleSearch.Text = query;

    private void AddShortcuts()
    {
        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                action();
                args.Handled = true;
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }

        Add(VirtualKey.F, VirtualKeyModifiers.Control, () => TitleSearch.Focus(FocusState.Keyboard));
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, GoBack);
        Add(VirtualKey.GoBack, VirtualKeyModifiers.None, GoBack);
        Add(VirtualKey.Right, VirtualKeyModifiers.Control, () => _ = Playback.NextAsync());
        Add(VirtualKey.Left, VirtualKeyModifiers.Control, () => _ = Playback.PreviousAsync());
        Add(VirtualKey.Left, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, () => Playback.Skip(-App.Settings.SkipSeconds));
        Add(VirtualKey.Right, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, () => Playback.Skip(App.Settings.SkipSeconds));
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        // Space plays/pauses unless something that types or presses has focus.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key != VirtualKey.Space)
            {
                return;
            }

            var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
            if (focused is TextBox or AutoSuggestBox or ButtonBase or ListViewItem or Slider or ToggleSwitch)
            {
                return;
            }

            Playback.Toggle();
            args.Handled = true;
        }), true);

        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnZoomKeyDown), true);
    }

    // MARK: zoom

    /// <summary>Ctrl + and Ctrl - step the content's scale; Ctrl 0 puts it back to 100%.
    /// The main row's + and - arrive as the OEM keys, which the accelerator list cannot
    /// name, so the keys are read here instead.</summary>
    private void OnZoomKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)
            || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down))
        {
            return;
        }

        switch ((int)args.Key)
        {
            case 0xBB: // =
            case 0x6B: // the numeric keypad's +
                ZoomBy(1);
                args.Handled = true;
                break;

            case 0xBD: // -
            case 0x6D: // the numeric keypad's -
                ZoomBy(-1);
                args.Handled = true;
                break;

            case 0x30: // 0
                SetZoom(1);
                args.Handled = true;
                break;
        }
    }

    /// <summary>Puts the saved zoom back before the first layout; the window keeps its size,
    /// and the content inside is what changes.</summary>
    private void RestoreZoom()
    {
        var saved = App.Settings.UiZoom;
        _zoom = double.IsFinite(saved) && saved > 0
            ? Math.Clamp(saved, ZoomSteps[0], ZoomSteps[^1])
            : 1;
    }

    private void ZoomBy(int direction)
    {
        var index = NearestZoomIndex(_zoom);
        SetZoom(ZoomSteps[Math.Clamp(index + direction, 0, ZoomSteps.Length - 1)]);
    }

    private static int NearestZoomIndex(double zoom)
    {
        var index = 0;
        for (var i = 1; i < ZoomSteps.Length; i++)
        {
            if (Math.Abs(ZoomSteps[i] - zoom) < Math.Abs(ZoomSteps[index] - zoom))
            {
                index = i;
            }
        }

        return index;
    }

    private void SetZoom(double zoom)
    {
        var wanted = Math.Clamp(zoom, ZoomSteps[0], ZoomSteps[^1]);
        if (Math.Abs(wanted - _zoom) > 0.0001)
        {
            _zoom = wanted;
            App.Settings.Update(settings => settings.UiZoom = _zoom);
            ApplyZoom();
        }

        ZoomBadgeText.Text = $"{Math.Round(_zoom * 100)}%";
        ZoomBadge.Visibility = Visibility.Visible;
        _zoomTimer?.Stop();
        _zoomTimer?.Start();
    }

    /// <summary>The content lays out at the room divided by the zoom, then is scaled back up:
    /// text grows and less fits, as a browser zoom does. The title bar keeps Windows' caption
    /// buttons but scales its own content with the zoom, down to a usable floor.</summary>
    private void ApplyZoom()
    {
        var width = ZoomHost.ActualWidth;
        var height = ZoomHost.ActualHeight;
        if (width > 0 && height > 0)
        {
            ZoomContent.Width = width / _zoom;
            ZoomContent.Height = height / _zoom;
            ZoomScale.ScaleX = _zoom;
            ZoomScale.ScaleY = _zoom;
        }

        ApplyTitleBarScale();
    }

    /// <summary>The title bar content is laid out at the room divided by its scale, then
    /// scaled back up, the same way the page content is. Windows' caption buttons keep
    /// their native size, so the bar itself stays at the standard height.</summary>
    private void ApplyTitleBarScale()
    {
        _titleScale = Math.Clamp(_zoom, TitleBarMinScale, 1);
        var width = AppTitleBar.ActualWidth;
        var height = AppTitleBar.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var layoutWidth = width / _titleScale;
        var layoutHeight = height / _titleScale;
        if (!double.IsFinite(TitleBarContent.Width) || Math.Abs(TitleBarContent.Width - layoutWidth) > 0.5)
        {
            TitleBarContent.Width = layoutWidth;
        }

        if (!double.IsFinite(TitleBarContent.Height) || Math.Abs(TitleBarContent.Height - layoutHeight) > 0.5)
        {
            TitleBarContent.Height = layoutHeight;
        }

        TitleBarScale.ScaleX = _titleScale;
        TitleBarScale.ScaleY = _titleScale;
        UpdateTitleBarRegions();
    }

    // MARK: player bar

    private void OnTrackChanged()
    {
        var song = Playback.CurrentSong;
        TrackTitleText.Text = song?.Title ?? "Nothing playing";
        TrackArtistText.Text = song?.ArtistLine ?? "";
        Art.SetSource(CoverImage, song?.Artwork);
        CoverGlyph.Visibility = song?.Artwork is null ? Visibility.Visible : Visibility.Collapsed;
        LikeButton.Visibility = song is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateLike();

        NextButton.IsEnabled = Playback.CanNext;
        PreviousButton.IsEnabled = song is not null;
        QueueButton.IsEnabled = MiniButton.IsEnabled = ShuffleButton.IsEnabled = RepeatButton.IsEnabled = song is not null;
        SkipBackButton.IsEnabled = SkipForwardButton.IsEnabled = song is not null && !Playback.IsLoading;
        _smtc.SetTransport(Playback.CanPrev, Playback.CanNext);
        Scrubber.Maximum = Math.Max(1, Playback.DurationSeconds);
        Scrubber.Value = Math.Clamp(Playback.PositionSeconds, 0, Scrubber.Maximum);
        ElapsedText.Text = song is null ? "" : FormatTime(Scrubber.Value);
        TotalText.Text = Playback.DurationSeconds > 0 ? FormatTime(Playback.DurationSeconds) : "";
        OnQueueModesChanged();
        UpdateTaskbar();

        // One toast per song, and only when the track really changed: TrackChanged also
        // fires when the queue modes or the known duration change.
        if (song is null)
        {
            _notifiedSongId = null;
        }
        else if (song.Id != _notifiedSongId)
        {
            _notifiedSongId = song.Id;
            Notifications.SongChanged(song, Playback.IsPlaying);
        }
    }

    private void OnQueueModesChanged()
    {
        ShuffleButton.IsChecked = Playback.Queue.IsShuffled;
        var repeat = Playback.Queue.Repeat;
        RepeatButton.IsChecked = repeat != RepeatMode.Off;
        RepeatIcon.Glyph = repeat == RepeatMode.One ? "" : "";
        ToolTipService.SetToolTip(RepeatButton, repeat switch
        {
            RepeatMode.All => "Repeat all",
            RepeatMode.One => "Repeat one",
            _ => "Repeat off",
        });
        NextButton.IsEnabled = Playback.CanNext;
        UpdateUpNext();
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e) => Playback.SetShuffle(ShuffleButton.IsChecked == true);

    private void OnRepeatClick(object sender, RoutedEventArgs e) => Playback.CycleRepeat();

    private void OnQueueClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is NowPlayingPage)
        {
            GoBack();
        }
        else if (Playback.CurrentSong is not null)
        {
            ContentFrame.Navigate(typeof(NowPlayingPage), "queue", new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
        }
    }

    private void UpdateLike()
    {
        var liked = Playback.CurrentSong is { } song && App.Library.IsLiked(song);
        LikeIcon.Glyph = liked ? "" : "";
        LikeIcon.Foreground = liked
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var label = liked ? "Remove from Liked" : "Add to Liked";
        ToolTipService.SetToolTip(LikeButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LikeButton, label);
    }

    private void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (Playback.CurrentSong is { } song)
        {
            App.Library.ToggleLike(song);
        }
    }

    /// <summary>The cover or title opens Now Playing, drilling in; tapped again it closes.</summary>
    private void OnNowPlayingTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ContentFrame.Content is NowPlayingPage)
        {
            GoBack();
        }
        else if (Playback.CurrentSong is not null)
        {
            ContentFrame.Navigate(typeof(NowPlayingPage), null, new Microsoft.UI.Xaml.Media.Animation.DrillInNavigationTransitionInfo());
        }
    }

    private void OnArtistTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Playback.CurrentSong?.Artists.FirstOrDefault(a => a.Id is not null) is { Id: { } id })
        {
            NavigateTo(typeof(ArtistPage), id);
        }
    }

    private void OnPlayStateChanged()
    {
        var playing = Playback.IsPlaying;
        var loading = Playback.IsLoading;
        PlayPauseIcon.Glyph = playing ? "" : "";
        PlayPauseIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        BarLoading.IsActive = loading;
        BarLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        var label = playing ? "Pause (Space)" : "Play (Space)";
        ToolTipService.SetToolTip(PlayPauseButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayPauseButton, playing ? "Pause" : "Play");
        PlayPauseButton.IsEnabled = Playback.CurrentSong is not null;
        Scrubber.IsEnabled = Playback.CurrentSong is not null;
        NextButton.IsEnabled = Playback.CanNext;
        SkipBackButton.IsEnabled = SkipForwardButton.IsEnabled = Playback.CurrentSong is not null && !loading;
        UpdateTaskbar();
    }

    private void OnPositionTick()
    {
        // The like state is read again each tick too: a cheap belt to the Changed braces.
        if (LikeButton.Visibility == Visibility.Visible) UpdateLike();
        UpdateTaskbar();
        var duration = Playback.DurationSeconds;
        if (duration <= 0 || _scrubbing)
        {
            return;
        }

        Scrubber.Maximum = duration;
        Scrubber.Value = Math.Clamp(Playback.PositionSeconds, 0, duration);
        ElapsedText.Text = FormatTime(Scrubber.Value);
        TotalText.Text = FormatTime(duration);
    }

    /// <summary>The taskbar's transport buttons and progress bar follow the queue.</summary>
    private void UpdateTaskbar() => _taskbar?.Update(
        Playback.CurrentSong is not null,
        Playback.IsPlaying,
        Playback.IsLoading,
        Playback.PositionSeconds,
        Playback.DurationSeconds);

    private static string FormatTime(double seconds)
    {
        var total = (long)Math.Round(Math.Max(0, seconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:D2}:{secs:D2}" : $"{minutes}:{secs:D2}";
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => Playback.Toggle();

    private void OnMiniClick(object sender, RoutedEventArgs e) => ShowMiniPlayer();

    private void OnPrevClick(object sender, RoutedEventArgs e) => _ = Playback.PreviousAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = Playback.NextAsync();

    private void OnSkipBackClick(object sender, RoutedEventArgs e) => Playback.Skip(-App.Settings.SkipSeconds);

    private void OnSkipForwardClick(object sender, RoutedEventArgs e) => Playback.Skip(App.Settings.SkipSeconds);

    /// <summary>The skip buttons follow the listener's interval, or go away if they turned
    /// them off; the tooltips and the screen reader names carry the seconds and the keys.</summary>
    private void ApplySkipSettings()
    {
        var settings = App.Settings;
        var seconds = settings.SkipSeconds;
        var visible = settings.ShowsSkipButtons ? Visibility.Visible : Visibility.Collapsed;

        SkipBackButton.Visibility = SkipForwardButton.Visibility = visible;
        SkipBackIcon.Seconds = SkipForwardIcon.Seconds = seconds;

        var back = $"Back {seconds} seconds";
        var ahead = $"Ahead {seconds} seconds";
        ToolTipService.SetToolTip(SkipBackButton, $"{back} (Ctrl+Alt+Left)");
        ToolTipService.SetToolTip(SkipForwardButton, $"{ahead} (Ctrl+Alt+Right)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SkipBackButton, back);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SkipForwardButton, ahead);
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        Playback.SetMuted(!Playback.IsMuted);
        MuteIcon.Glyph = Playback.IsMuted ? "" : "";
        ToolTipService.SetToolTip(MuteButton, Playback.IsMuted ? "Unmute" : "Mute");
    }

    private void OnOutputClick(object sender, RoutedEventArgs e) => AudioOutputMenu.Show(OutputButton);

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        Playback.SetVolumePercent(e.NewValue);
        if (_suppressVolumePersistence)
        {
            return;
        }

        _volumeSaveTimer?.Stop();
        _volumeSaveTimer?.Start();
    }

    private void OnPlayerBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Under 760 the volume goes speaker-only.
        VolumeSlider.Visibility = e.NewSize.Width < 760 ? Visibility.Collapsed : Visibility.Visible;
    }

    // MARK: status ribbon

    private void OnStatusRaised(PlaybackService.StatusTone tone, string message)
    {
        StatusRibbon.Message = message;
        StatusRibbon.Title = string.Empty;
        StatusRibbon.Severity = tone == PlaybackService.StatusTone.Pacing ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
        StatusRibbon.IsOpen = true;
        _statusTimer?.Stop();
        _statusTimer?.Start();

        // A failure while the window is away deserves a toast; pacing notes do not.
        if (tone == PlaybackService.StatusTone.Fault)
        {
            Notifications.Fault(message);
        }
    }

    private void OnStatusClosed(InfoBar sender, object args) => _statusTimer?.Stop();

    /// <summary>A short note in the ribbon, for pages (import done, saved, and the like).</summary>
    public void Note(string message) => OnStatusRaised(PlaybackService.StatusTone.Pacing, message);

    // MARK: SMTC + tray

    private void OnSmtcButton(SystemMediaTransportControlsButton button)
    {
        switch (button)
        {
            case SystemMediaTransportControlsButton.Play:
            case SystemMediaTransportControlsButton.Pause:
                Playback.Toggle();
                break;
            case SystemMediaTransportControlsButton.Next:
                _ = Playback.NextAsync();
                break;
            case SystemMediaTransportControlsButton.Previous:
                _ = Playback.PreviousAsync();
                break;
        }
    }

    private void OnTrayCommand(uint command)
    {
        switch (command)
        {
            case TrayIconHost.MenuPlayPause:
                Playback.Toggle();
                break;
            case TrayIconHost.MenuShow:
                ShowFromTray();
                break;
            case TrayIconHost.MenuExit:
                ExitApplication();
                break;
        }
    }

    /// <summary>
    /// Brings the running window forward: from the tray, or because a second
    /// launch was redirected here. Restores a minimised/hidden window first.
    /// </summary>
    public void ShowFromTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.Show();
        Win32.ShowWindowAsync(hwnd, Win32.SW_RESTORE);
        Activate();
        if (!Win32.SetForegroundWindow(hwnd) || Win32.GetForegroundWindow() != hwnd)
        {
            Win32.SwitchToThisWindow(hwnd, altTab: true);
        }
    }

    /// <summary>
    /// Open the compact overlay, or bring the one that is already open forward. It is a
    /// second window so the library stays usable behind it; a second request focuses the
    /// same overlay instead of stacking another.
    /// </summary>
    /// <summary>Whether the listener can see what is playing: the window is up and not minimized,
    /// or the mini player is open. A song notification is for when they cannot.</summary>
    public bool ShowsPlayback =>
        _mini is not null ||
        (AppWindow.IsVisible && (AppWindow.Presenter as OverlappedPresenter)?.State != OverlappedPresenterState.Minimized);

    public void ShowMiniPlayer()
    {
        if (Playback.CurrentSong is null)
        {
            return;
        }

        if (_mini is not null)
        {
            _mini.Activate();
            return;
        }

        var mini = new MiniPlayerWindow();
        _mini = mini;
        mini.Closed += (_, _) =>
        {
            if (ReferenceEquals(_mini, mini))
            {
                _mini = null;
            }
        };
        mini.Activate();
    }

    /// <summary>Hiding the library hides the overlay with it; there is nothing left to see.</summary>
    public void CloseMiniPlayer()
    {
        var mini = _mini;
        _mini = null;
        mini?.Close();
    }

    private void ExitApplication()
    {
        try
        {
            WindowPlacement.Save(Handle);
            Shutdown();
        }
        catch (Exception error)
        {
            App.WriteError("exiting Koda", error);
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    /// <summary>Hands the shell integrations back once, whether the listener chose Exit
    /// or closed the window with close-to-tray off. The guard keeps the Closed handler
    /// from disposing the engine a second time after ExitApplication already did.</summary>
    private void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        CloseMiniPlayer();
        Playback.Shutdown();
        _taskbar?.Dispose();
        _tray?.Dispose();
        _smtc.Dispose();
        App.ForgetShell(this);
        _current = null;
    }

    /// <summary>The end of a Windows session: keep the queue, the library and the
    /// window placement, but do not stop playback in case the shutdown is cancelled.</summary>
    private static void FlushSession()
    {
        if (_current is { } window)
        {
            WindowPlacement.Save(window.Handle);
        }

        PlaybackService.Instance.SaveState();
        App.Library.Flush();
        App.AudioStore.Flush();
        App.Settings.Save();
    }

    private static nint OnWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_GETMINMAXINFO:
            {
                var scale = Win32.GetDpiForWindow(hwnd) / 96.0;
                var info = Marshal.PtrToStructure<Win32.MINMAXINFO>(lParam);
                info.ptMinTrackSize.X = (int)Math.Ceiling(MinWindowDipWidth * scale);
                info.ptMinTrackSize.Y = (int)Math.Ceiling(MinWindowDipHeight * scale);
                Marshal.StructureToPtr(info, lParam, false);
                return 0;
            }

            case Win32.WM_CLOSE:
                if (_current is not null)
                {
                    WindowPlacement.Save(hwnd);
                }

                if (App.CloseToTray && _current is not null)
                {
                    _current.CloseMiniPlayer();
                    _current.AppWindow.Hide();
                    return 0;
                }

                break;

            case Win32.WM_EXITSIZEMOVE:
                if (_current is not null)
                {
                    WindowPlacement.Save(hwnd);
                }

                break;

            case Win32.WM_COMMAND:
            {
                var id = (int)((long)wParam & 0xFFFF);
                var code = (int)(((long)wParam >> 16) & 0xFFFF);
                if (code == Taskbar.ThumbnailClicked && _current?._taskbar?.HandleCommand(id) == true)
                {
                    return 0;
                }

                break;
            }

            case Win32.WM_QUERYENDSESSION:
                FlushSession();
                return 1;

            case Win32.WM_ENDSESSION:
                if (wParam != 0)
                {
                    FlushSession();
                }

                return 0;

            case TrayIconHost.TrayCallbackMessage:
                _current?._tray?.HandleMessage(lParam);
                return 0;

            case Win32.WM_NCDESTROY:
                if (_current is not null && _current._previousWndProc != 0)
                {
                    Win32.SetWindowLongPtr(hwnd, Win32.GWLP_WNDPROC, _current._previousWndProc);
                    _current._previousWndProc = 0;
                }

                break;
        }

        if (_current is not null && _current._previousWndProc != 0)
        {
            return Win32.CallWindowProc(_current._previousWndProc, hwnd, msg, wParam, lParam);
        }

        return 0;
    }
}
