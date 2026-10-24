using System;
using System.Diagnostics;
using System.Reflection;
using Koda.Core;
using Koda.Helpers;
using Koda.Interop;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Koda.Pages;

/// <summary>
/// The settings, in the groups the Mac's settings page uses: what plays,
/// where the lyrics come from, how they look, what is remembered, what is kept on disk,
/// and what this is.
///
/// Every control here is wired to something that works: there is no switch that
/// pretends. The ones the family has and Windows has not yet (crossfade, sign-in,
/// Last.fm, Discord) are not here until they are.
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
    }

    /// <summary>Puts the listener's choices into the controls.</summary>
    private void Load()
    {
        var settings = App.Settings;
        _loaded = false;

        AutoplaySwitch.IsOn = settings.Autoplay;
        SkipButtonsSwitch.IsOn = settings.ShowsSkipButtons;

        SkipBox.Items.Clear();
        foreach (var seconds in Settings.SkipIntervals)
        {
            SkipBox.Items.Add($"{seconds} seconds");
        }

        SkipBox.SelectedIndex = Array.IndexOf(Settings.SkipIntervals, settings.SkipSeconds);

        CloseToTraySwitch.IsOn = settings.CloseToTray;
        OpenAtLoginSwitch.IsOn = Startup.IsEnabled;

        YouLyPlusSwitch.IsOn = settings.IsEnabled(LyricsSource.YouLyPlus);
        BetterLyricsSwitch.IsOn = settings.IsEnabled(LyricsSource.BetterLyrics);
        LrclibSwitch.IsOn = settings.IsEnabled(LyricsSource.Lrclib);

        WordMotionSwitch.IsOn = settings.LyricsMotion;
        BackingVocalsSwitch.IsOn = settings.ShowsBackingVocals;
        DuetSidesSwitch.IsOn = settings.DuetSides;
        AnimatedBackgroundSwitch.IsOn = settings.AnimatesPlayerBackground;
        KeepHistorySwitch.IsOn = settings.SavesListeningHistory;
        KeepSearchHistorySwitch.IsOn = settings.SavesSearchHistory;

        LyricsSizeBox.Items.Clear();
        foreach (var size in Enum.GetValues<LyricsSize>())
        {
            LyricsSizeBox.Items.Add(SizeLabel(size));
        }

        LyricsSizeBox.SelectedIndex = Array.IndexOf(Enum.GetValues<LyricsSize>(), settings.LyricsSize);

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
        VersionText.Text = $"Koda {version?.ToString(3) ?? "0.1.0"} for Windows 11";
        RepositoryLink.Content = "github.com/Ivorisnoob/Koda-Windows";

        _loaded = true;
        RefreshStorage();
    }

    private static string SizeLabel(LyricsSize size) => size switch
    {
        LyricsSize.Small => "Small",
        LyricsSize.Large => "Large",
        _ => "Default",
    };

    private static string ThemeLabel(Theme theme) => theme switch
    {
        Theme.Light => "Light",
        Theme.Dark => "Dark",
        _ => "Automatic",
    };

    private static string Size(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000.0:F1} GB" : $"{bytes / 1_000_000} MB";

    // MARK: playback

    private void OnAutoplayToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        // SetAutoplay saves the choice and lets the queue follow it (and top up).
        Playback.PlaybackService.Instance.SetAutoplay(AutoplaySwitch.IsOn);
    }

    private void OnCloseToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.SetCloseToTray(CloseToTraySwitch.IsOn);
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

    private void OnLyricsSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || LyricsSizeBox.SelectedIndex < 0) return;
        var size = Enum.GetValues<LyricsSize>()[LyricsSizeBox.SelectedIndex];
        App.Settings.Update(s => s.LyricsSize = size);
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
