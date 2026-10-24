using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Koda.Core;
using Koda.Helpers;
using Koda.Interop;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Koda.Pages;

/// <summary>
/// The settings, in the groups the Mac's settings page uses: what plays,
/// where the lyrics come from, how they look, what is remembered, what is kept on disk,
/// and what this is.
///
/// Every control here is wired to something that works: there is no switch that
/// pretends. The ones the family has and Windows has not yet (profiles and sign-in,
/// Discord, videos) are not here until they are.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private bool _loaded;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Load();
        EnergySaver.Changed += OnEnergySaverChanged;
        App.Library.Changed += OnLibraryChanged;
        App.Downloads.Changed += OnDownloadsChanged;
        App.LastFm.Changed += OnLastFmChanged;
        App.Discord.Changed += OnDiscordChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        EnergySaver.Changed -= OnEnergySaverChanged;
        App.Library.Changed -= OnLibraryChanged;
        App.Downloads.Changed -= OnDownloadsChanged;
        App.LastFm.Changed -= OnLastFmChanged;
        App.Discord.Changed -= OnDiscordChanged;
    }

    private void OnDiscordChanged() => DispatcherQueue.TryEnqueue(RefreshDiscord);

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(RefreshSuggestLess);

    private void OnDownloadsChanged() => DispatcherQueue.TryEnqueue(RefreshDownloads);

    private void OnLastFmChanged() => DispatcherQueue.TryEnqueue(RefreshLastFm);

    /// <summary>Puts the listener's choices into the controls.</summary>
    private void Load()
    {
        var settings = App.Settings;
        _loaded = false;

        AutoplaySwitch.IsOn = settings.Autoplay;
        NormalizeSwitch.IsOn = settings.NormalizesVolume;
        CrossfadeSwitch.IsOn = settings.Crossfades;
        CrossfadeSlider.Value = settings.CrossfadeSeconds;
        CrossfadeSlider.IsEnabled = settings.Crossfades;
        CrossfadeValue.Text = $"{settings.CrossfadeSeconds:F0} s";
        SkipButtonsSwitch.IsOn = settings.ShowsSkipButtons;

        SkipBox.Items.Clear();
        foreach (var seconds in Settings.SkipIntervals)
        {
            SkipBox.Items.Add($"{seconds} seconds");
        }

        SkipBox.SelectedIndex = Array.IndexOf(Settings.SkipIntervals, settings.SkipSeconds);

        CloseToTraySwitch.IsOn = settings.CloseToTray;
        OpenAtLoginSwitch.IsOn = Startup.IsEnabled;
        NotificationsSwitch.IsOn = settings.ShowsSongNotifications;
        RefreshEnergySaver();

        YouLyPlusSwitch.IsOn = settings.IsEnabled(LyricsSource.YouLyPlus);
        BetterLyricsSwitch.IsOn = settings.IsEnabled(LyricsSource.BetterLyrics);
        LrclibSwitch.IsOn = settings.IsEnabled(LyricsSource.Lrclib);

        WordMotionSwitch.IsOn = settings.LyricsMotion;
        BackingVocalsSwitch.IsOn = settings.ShowsBackingVocals;
        DuetSidesSwitch.IsOn = settings.DuetSides;
        AnimatedBackgroundSwitch.IsOn = settings.AnimatesPlayerBackground;
        KeepHistorySwitch.IsOn = settings.SavesListeningHistory;
        KeepSearchHistorySwitch.IsOn = settings.SavesSearchHistory;

        LyricsSizeSlider.Value = settings.LyricsFontSize;
        LyricsSizeValue.Text = $"{settings.LyricsFontSize:F0}";

        ThemeBox.Items.Clear();
        foreach (var theme in Enum.GetValues<Theme>())
        {
            ThemeBox.Items.Add(ThemeLabel(theme));
        }

        ThemeBox.SelectedIndex = Array.IndexOf(Enum.GetValues<Theme>(), settings.Theme);

        CacheLimitBox.Items.Clear();
        foreach (var limit in Enum.GetValues<CacheLimit>())
        {
            CacheLimitBox.Items.Add(Size((long)limit));
        }

        // The cache limit's values are byte counts, so the row is found by its place
        // in the list, not by its number.
        CacheLimitBox.SelectedIndex = Array.IndexOf(Enum.GetValues<CacheLimit>(), settings.CacheLimit);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Koda {version?.ToString(3) ?? "0.1.0"}";

        AndroidImportText.Text = AndroidImport.Explanation;

        _loaded = true;
        RefreshStorage();
        RefreshDownloads();
        RefreshSuggestLess();
        RefreshLastFm();
        RefreshDiscord();
    }

    private static string ThemeLabel(Theme theme) => theme switch
    {
        Theme.Light => "Light",
        Theme.Dark => "Dark",
        _ => "Automatic",
    };

    private static string Size(long bytes) => Format.Bytes(bytes);

    // MARK: playback

    private void OnAutoplayToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        // SetAutoplay saves the choice and lets the queue follow it (and top up).
        Playback.PlaybackService.Instance.SetAutoplay(AutoplaySwitch.IsOn);
    }

    private void OnNormalizeToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Update(s => s.NormalizesVolume = NormalizeSwitch.IsOn);
    }

    private void OnCrossfadeToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        CrossfadeSlider.IsEnabled = CrossfadeSwitch.IsOn;
        App.Settings.Update(s => s.Crossfades = CrossfadeSwitch.IsOn);
    }

    private void OnCrossfadeLengthChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Minimum="1" in the XAML moves the value while the page is still being built.
        if (CrossfadeValue is null) return;
        var seconds = Math.Round(e.NewValue);
        CrossfadeValue.Text = $"{seconds:F0} s";
        if (!_loaded || seconds == App.Settings.CrossfadeSeconds) return;
        App.Settings.Update(s => s.CrossfadeSeconds = seconds);
    }

    private void OnCloseToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.SetCloseToTray(CloseToTraySwitch.IsOn);
    }

    private void OnNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Update(s => s.ShowsSongNotifications = NotificationsSwitch.IsOn);
    }

    private void OnEnergySaverChanged() => RefreshEnergySaver();

    private void RefreshEnergySaver() =>
        EnergySaverText.Text = EnergySaver.IsOn ? "On" : "Off";

    private void OnEnergySaverSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:batterysaver") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // A locked-down Settings app is not a reason to fail the page.
        }
    }

    private void OnSkipButtonsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Update(s => s.ShowsSkipButtons = SkipButtonsSwitch.IsOn);
    }

    private void OnSkipChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || SkipBox.SelectedIndex < 0) return;
        var seconds = Settings.SkipIntervals[SkipBox.SelectedIndex];
        App.Settings.Update(s => s.SkipSeconds = seconds);
    }

    // MARK: Windows

    private void OnOpenAtLoginToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        var on = OpenAtLoginSwitch.IsOn;
        if (!Startup.Apply(on))
        {
            Notice("Windows wouldn't accept the login setting.");
            Load();
            return;
        }

        App.Settings.Update(s => s.OpenAtLogin = on);
    }

    private void OnChooseOutputClick(object sender, RoutedEventArgs e) =>
        AudioOutputMenu.Show(OutputSettingsButton);

    // MARK: lyrics

    private void OnSourceToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Update(s =>
        {
            s.SetEnabled(LyricsSource.YouLyPlus, YouLyPlusSwitch.IsOn);
            s.SetEnabled(LyricsSource.BetterLyrics, BetterLyricsSwitch.IsOn);
            s.SetEnabled(LyricsSource.Lrclib, LrclibSwitch.IsOn);
        });

        // The last service cannot be turned off, so the switch goes back where it was.
        LyricsService.Forget();
        Load();
    }

    private void OnWordMotionToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Update(s => s.LyricsMotion = WordMotionSwitch.IsOn);
    }

    private void OnBackingVocalsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Update(s => s.ShowsBackingVocals = BackingVocalsSwitch.IsOn);
    }

    private void OnDuetSidesToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Update(s => s.DuetSides = DuetSidesSwitch.IsOn);
    }

    private void OnLyricsSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        var size = Math.Round(e.NewValue);
        LyricsSizeValue.Text = $"{size:F0}";
        App.Settings.Update(s => s.LyricsFontSize = size);
    }

    private void OnRelookLyricsClick(object sender, RoutedEventArgs e)
    {
        LyricsService.Forget();
        Notice("The lyrics will be looked up again.");
    }

    // MARK: display

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || ThemeBox.SelectedIndex < 0) return;
        var theme = Enum.GetValues<Theme>()[ThemeBox.SelectedIndex];
        App.Settings.Update(s => s.Theme = theme);
        ShellChrome.ApplyTheme();
    }

    private void OnAnimatedBackgroundToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Update(s => s.AnimatesPlayerBackground = AnimatedBackgroundSwitch.IsOn);
    }

    // MARK: privacy

    private void OnKeepHistoryToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Update(s => s.SavesListeningHistory = KeepHistorySwitch.IsOn);
    }

    private void OnKeepSearchHistoryToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Update(s => s.SavesSearchHistory = KeepSearchHistorySwitch.IsOn);
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        App.Library.ClearHistory();
        Notice("The listening history is empty.");
    }

    private void OnClearSearchesClick(object sender, RoutedEventArgs e)
    {
        App.Library.ClearSearches();
        Notice("The search history is empty.");
    }

    private void OnClearStatsClick(object sender, RoutedEventArgs e)
    {
        App.Library.ClearStats();
        Notice("The listening stats are empty.");
    }

    // MARK: storage

    private void OnCacheLimitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || CacheLimitBox.SelectedIndex < 0) return;
        var limit = Enum.GetValues<CacheLimit>()[CacheLimitBox.SelectedIndex];
        App.Settings.Update(s => s.CacheLimit = limit);
        App.AudioStore.CacheLimit = (long)limit;
        RefreshStorage();
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        App.AudioStore.ClearCache();
        RefreshStorage();
        Notice("The listening cache is empty.");
    }

    /// <summary>What is kept on disk, said plainly.</summary>
    private void RefreshStorage()
    {
        var limit = (long)App.Settings.CacheLimit;
        CacheSizeText.Text = $"{Size(App.AudioStore.CacheBytes)} of {Size(limit)} used.";
    }

    // MARK: suggest less

    /// <summary>The songs and artists kept out of what Koda suggests, each with a way back.</summary>
    private void RefreshSuggestLess()
    {
        var hidden = App.Library.NotInterested;
        SuggestLessNote.Text = hidden.IsEmpty
            ? "Choose Suggest less on a song to keep it, or its artist, out of Home, mixes, radio and autoplay."
            : "Kept out of Home, mixes, radio and autoplay. Songs you pick yourself still play.";
        SuggestLessList.Children.Clear();
        foreach (var artist in hidden.Artists)
        {
            AddSuggestLessRow(artist.Name, "Artist", () => App.Library.Unhide(artist));
        }

        foreach (var song in hidden.Songs)
        {
            AddSuggestLessRow(song.Title, song.ArtistLine, () => App.Library.Unhide(song));
        }
    }

    private void AddSuggestLessRow(string title, string detail, Action again)
    {
        SuggestLessList.Children.Add(new Border { Style = (Style)Resources["SettingsDivider"] });
        var row = new Grid { Style = (Style)Resources["SettingsRow"] };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = detail,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(text);
        var button = new Button { Content = "Suggest again", VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Suggest {title} again");
        button.Click += (_, _) => again();
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        SuggestLessList.Children.Add(row);
    }

    // MARK: last.fm

    private void RefreshLastFm()
    {
        var lastFm = App.LastFm;
        LastFmTitle.Text = lastFm.IsConnected
            ? lastFm.IsEnabled ? $"Connected as {lastFm.User}" : $"Connected as {lastFm.User}, scrobbling off"
            : "Not connected";
        LastFmButton.Content = lastFm.IsConnected ? "Open" : "Connect…";
    }

    private void OnLastFmClick(object sender, RoutedEventArgs e) => Navigator.Go(typeof(LastFmPage));

    // MARK: discord

    /// <summary>The switch, and a line that says what Discord is doing with it.</summary>
    private void RefreshDiscord()
    {
        var settings = App.Settings;
        DiscordSwitch.IsOn = settings.SharesOnDiscord;
        DiscordText.Text = !settings.SharesOnDiscord
            ? "Your Discord profile shows \u201cListening to\u201d the song, with its cover and a link to it on YouTube Music. It needs the Discord app open on this PC."
            : !settings.SavesListeningHistory
                ? "Paused while listening history is off."
                : App.Discord.Status switch
                {
                    Koda.Core.Discord.DiscordPresence.Phase.Showing => "Showing on your profile.",
                    Koda.Core.Discord.DiscordPresence.Phase.Unavailable => "Discord isn\u2019t open. The song shows as soon as it is.",
                    Koda.Core.Discord.DiscordPresence.Phase.Refused => "Discord didn\u2019t accept this song\u2019s card.",
                    _ => "Shows the next song you play.",
                };
    }

    private void OnDiscordToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || DiscordSwitch.IsOn == App.Settings.SharesOnDiscord) return;
        App.Settings.Update(s => s.SharesOnDiscord = DiscordSwitch.IsOn);
        RefreshDiscord();
    }

    // MARK: downloads

    private void RefreshDownloads()
    {
        var count = App.Downloads.Songs.Length;
        DownloadsText.Text = count == 0
            ? "Downloaded songs play without a connection and stay until you remove them."
            : $"{Format.Songs(count)} · {Format.Bytes(App.Downloads.Bytes)}. They play without a connection and stay until you remove them.";
        RemoveDownloadsButton.IsEnabled = count > 0;
    }

    private void OnShowDownloadsClick(object sender, RoutedEventArgs e) => Navigator.Go(typeof(SongsPage), "downloads");

    private async void OnRemoveDownloadsClick(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.ConfirmAsync("Remove all downloads?",
                "They'll play from YouTube again, which needs a connection.", "Remove All"))
        {
            App.Downloads.RemoveAll();
        }
    }

    // MARK: backup

    private void OnAndroidImportClick(object sender, RoutedEventArgs e) => _ = AndroidImport.RunAsync();

    private async void OnExportLibraryClick(object sender, RoutedEventArgs e)
    {
        if (App.Shell is not { } shell)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"koda-library-{DateTime.Now:yyyy-MM-dd}",
        };
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(shell));
        picker.FileTypeChoices.Add("Koda library backup", new List<string> { ".json" });

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await File.WriteAllBytesAsync(file.Path, App.Library.BackupData());
            Notice("The library was exported.");
        }
        catch (Exception error)
        {
            Notice($"The library couldn't be written: {error.Message}");
        }
    }

    private async void OnRestoreLibraryClick(object sender, RoutedEventArgs e)
    {
        if (App.Shell is not { } shell)
        {
            return;
        }

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(shell));
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "Restore this backup?",
            Content = "Likes, playlists, saved albums, history, stats and lyrics offsets here are replaced with the file's. This cannot be undone.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            App.Library.Restore(await File.ReadAllBytesAsync(file.Path));
            Notice("The library was restored from the backup.");
        }
        catch (LibraryBackupException)
        {
            Notice("That file isn't a Koda library backup.");
        }
        catch (Exception error)
        {
            Notice($"The backup couldn't be read: {error.Message}");
        }
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{App.DataDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            Notice($"Koda's data is in {App.DataDirectory}.");
        }
    }

    // MARK: about

    private void OnRepositoryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/Ivorisnoob/Koda-Windows") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // A machine without a browser still has the page in the README.
        }
    }

    /// <summary>A short note that something happened, the way the shell says it.</summary>
    private void Notice(string message)
    {
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }
}
