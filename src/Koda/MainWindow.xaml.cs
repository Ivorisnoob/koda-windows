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
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Koda;

public sealed partial class MainWindow : Window
{
    private static MainWindow? _current;

    private const int MinWindowDipWidth = 700;
    private const int MinWindowDipHeight = 520;

    private TrayIconHost? _tray;
    private readonly SmtcController _smtc = new();
    private nint _previousWndProc;
    private bool _scrubbing;
    private bool _suppressVolumePersistence;
    private bool _suppressNavSelection;
    private DispatcherQueueTimer? _volumeSaveTimer;
    private DispatcherQueueTimer? _statusTimer;
    private DispatcherQueueTimer? _suggestTimer;
    private CancellationTokenSource? _suggestCancel;

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
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Root.ActualThemeChanged += (_, _) => PaintCaptionButtons();
        PaintCaptionButtons();
        AppTitleBar.SizeChanged += OnTitleBarPartsChanged;
        Activated += OnActivated;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _previousWndProc = Win32.SetWindowLongPtr(
            hwnd, Win32.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(SubclassedProc));

        _smtc.Bind(hwnd, OnSmtcButton);

        _tray = new TrayIconHost(
            hwnd,
            Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"),
            () => Playback.IsPlaying ? "Pause" : "Play",
            OnTrayCommand);

        var dispatch = DispatcherQueue.GetForCurrentThread();
        Playback.Connect(dispatch, _smtc);
        Playback.TrackChanged += OnTrackChanged;
        Playback.QueueChanged += OnQueueModesChanged;
        Playback.PlayStateChanged += OnPlayStateChanged;
        Playback.PositionTick += OnPositionTick;
        Playback.StatusRaised += OnStatusRaised;
        App.Library.Changed += () => dispatch.TryEnqueue(UpdateLike);

        Scrubber.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnScrubberPressed), true);
        Scrubber.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnScrubberReleased), true);
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

        AddShortcuts();

        ContentFrame.Navigated += OnContentNavigated;
        NavigateTo(typeof(HomePage));

        OnTrackChanged();
        OnPlayStateChanged();

        Closed += (_, _) =>
        {
            Playback.Shutdown();
            _tray?.Dispose();
            _smtc.Dispose();
            _current = null;
        };
    }

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
    }

    /// <summary>The caption buttons' space is kept clear, and the back button and search field stay clickable.</summary>
    private void OnTitleBarPartsChanged(object sender, SizeChangedEventArgs args)
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        LeftInset.Width = new GridLength(AppWindow.TitleBar.LeftInset / scale);
        RightInset.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        if (AppTitleBar.XamlRoot is null)
        {
            return;
        }

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

        ContentFrame.Navigate(page, parameter, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavSelection)
        {
            return;
        }

        Type? page = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : (sender.SelectedItem as NavigationViewItem)?.Tag switch
            {
                "home" => typeof(HomePage),
                "search" => typeof(SearchPage),
                "library" => typeof(LibraryPage),
                _ => null,
            };
        if (page is null)
        {
            return;
        }

        if (page == typeof(SearchPage))
        {
            TitleSearch.Focus(FocusState.Programmatic);
        }

        if (ContentFrame.CurrentSourcePageType != page || ContentFrame.CanGoBack)
        {
            ContentFrame.Navigate(page, null, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void OnContentNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs args)
    {
        BackButton.IsEnabled = ContentFrame.CanGoBack;
        // Now Playing is the whole player: the bar steps aside while it is open.
        PlayerBar.Visibility = args.SourcePageType == typeof(NowPlayingPage) ? Visibility.Collapsed : Visibility.Visible;
        // The rail shows the section the page belongs to; detail pages keep their section lit.
        var tag = args.SourcePageType.Name switch
        {
            nameof(HomePage) or nameof(MoodPage) => "home",
            nameof(SearchPage) => "search",
            nameof(LibraryPage) => "library",
            _ => null,
        };
        _suppressNavSelection = true;
        if (args.SourcePageType == typeof(SettingsPage))
        {
            Nav.SelectedItem = Nav.SettingsItem;
        }
        else if (tag is not null)
        {
            Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag as string == tag);
        }

        _suppressNavSelection = false;
    }

    // MARK: search in the title bar

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _suggestTimer?.Stop();
        if (sender.Text.Trim().Length == 0)
        {
            sender.ItemsSource = App.Library.Searches.Take(8).ToList();
            return;
        }

        _suggestTimer?.Start();
    }

    private async System.Threading.Tasks.Task LoadSuggestionsAsync(string text)
    {
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
        var query = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
        _suggestTimer?.Stop();
        _suggestCancel?.Cancel();
        sender.ItemsSource = null;
        if (query.Length == 0)
        {
            return;
        }

        sender.Text = query;
        NavigateTo(typeof(SearchPage), query);
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
        QueueButton.IsEnabled = ShuffleButton.IsEnabled = RepeatButton.IsEnabled = song is not null;
        _smtc.SetTransport(Playback.CanPrev, Playback.CanNext);
        Scrubber.Maximum = Math.Max(1, Playback.DurationSeconds);
        Scrubber.Value = Math.Clamp(Playback.PositionSeconds, 0, Scrubber.Maximum);
        ElapsedText.Text = song is null ? "" : FormatTime(Scrubber.Value);
        TotalText.Text = Playback.DurationSeconds > 0 ? FormatTime(Playback.DurationSeconds) : "";
        OnQueueModesChanged();
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
    }

    private void OnPositionTick()
    {
        // The like state is read again each tick too: a cheap belt to the Changed braces.
        if (LikeButton.Visibility == Visibility.Visible) UpdateLike();
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

    private static string FormatTime(double seconds)
    {
        var total = (long)Math.Round(Math.Max(0, seconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:D2}:{secs:D2}" : $"{minutes}:{secs:D2}";
    }

    private void OnScrubberPressed(object sender, PointerRoutedEventArgs args)
    {
        if (Playback.CurrentSong is not null)
        {
            _scrubbing = true;
        }
    }

    private void OnScrubberReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_scrubbing)
        {
            return;
        }

        _scrubbing = false;
        if (Playback.CurrentSong is not null)
        {
            Playback.SeekSeconds(Scrubber.Value);
        }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => Playback.Toggle();

    private void OnPrevClick(object sender, RoutedEventArgs e) => _ = Playback.PreviousAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = Playback.NextAsync();

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        Playback.SetMuted(!Playback.IsMuted);
        MuteIcon.Glyph = Playback.IsMuted ? "" : "";
        ToolTipService.SetToolTip(MuteButton, Playback.IsMuted ? "Unmute" : "Mute");
    }

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
                ShowShell();
                break;
            case TrayIconHost.MenuExit:
                ExitApplication();
                break;
        }
    }

    private void ShowShell()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Win32.ShowWindowAsync(hwnd, Win32.SW_RESTORE);
        Win32.SetForegroundWindow(hwnd);
        AppWindow.Show();
        Activate();
    }

    private void ExitApplication()
    {
        Playback.Shutdown();
        _tray?.Dispose();
        _smtc.Dispose();
        Application.Current.Exit();
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
                if (App.CloseToTray && _current is not null)
                {
                    _current.AppWindow.Hide();
                    return 0;
                }

                break;

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
