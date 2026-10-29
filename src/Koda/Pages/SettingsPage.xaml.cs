using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Helpers;
using Koda.Interop;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Koda.Pages;

/// <summary>
/// The settings, in the groups the Mac's settings page uses: what plays,
/// where the lyrics come from, how they look, what is remembered, what is kept on disk,
/// what is connected, and what this is.
///
/// Every control here is wired to something that works: there is no switch that
/// pretends. The ones the family has and Windows has not yet (profiles, videos)
/// are not here until they are.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly Style _cardStyle;
    private readonly Style _rowStyle;
    private readonly Style _dividerStyle;
    private bool _loaded;
    private Brush? _highlightBrush;

    public SettingsPage()
    {
        InitializeComponent();
        _cardStyle = (Style)Resources["SettingsCard"];
        _rowStyle = (Style)Resources["SettingsRow"];
        _dividerStyle = (Style)Resources["SettingsDivider"];
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
        App.Account.Changed += OnAccountChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        EnergySaver.Changed -= OnEnergySaverChanged;
        App.Library.Changed -= OnLibraryChanged;
        App.Downloads.Changed -= OnDownloadsChanged;
        App.LastFm.Changed -= OnLastFmChanged;
        App.Discord.Changed -= OnDiscordChanged;
        App.Account.Changed -= OnAccountChanged;
    }

    private void OnDiscordChanged() => DispatcherQueue.TryEnqueue(RefreshDiscord);

    private void OnAccountChanged() => DispatcherQueue.TryEnqueue(RefreshYouTube);

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
        LosslessDownloadsSwitch.IsOn = settings.UsesLosslessServer;
        RefreshLosslessServer();
        GlobalContentSwitch.IsOn = settings.ShowsGlobalContent;
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
        YouTubeLibrarySwitch.IsOn = settings.SyncsYouTubeLibrary;
        YouTubePlaysSwitch.IsOn = settings.SendsPlaysToYouTube;

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
        RefreshYouTube();
        ApplySettingsSearch();
    }

    // MARK: search

    private void OnSettingsSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (SettingsRoot is null)
        {
            return;
        }

        ApplySettingsSearch();
        // Results start at the top, the way Windows Settings search does, instead of
        // leaving the page wherever it happened to be scrolled.
        SettingsScroll?.ChangeView(null, 0, null, true);
    }

    private void OnClearSettingsSearchClick(object sender, RoutedEventArgs e)
    {
        SettingsSearch.Text = "";
        ApplySettingsSearch();
        SettingsSearch.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Filters the page the way Windows Settings does: a row stays when its title or
    /// description matches, a section whose title matches keeps all its rows, and the
    /// matching letters are highlighted. Cards and dividers close up around the rest.
    /// </summary>
    private void ApplySettingsSearch()
    {
        // The box can raise TextChanged while the page is still being built.
        if (_cardStyle is null)
        {
            return;
        }

        var query = SettingsSearch.Text?.Trim() ?? "";
        ClearHighlights(SettingsRoot);

        if (query.Length == 0)
        {
            foreach (var section in IndexSections())
            {
                section.Header.Visibility = Visibility.Visible;
                section.Card.Visibility = Visibility.Visible;
                foreach (var row in section.Rows)
                {
                    row.Visibility = Visibility.Visible;
                }

                UpdateDividers(section.Card);
            }

            NoResults.Visibility = Visibility.Collapsed;
            return;
        }

        var found = false;
        foreach (var section in IndexSections())
        {
            // Only the section's title opens it whole. Its explanatory text is prose:
            // matching it as a subsequence ("volume" in "The services the lyrics are
            // asked for...") would keep a section whose settings have nothing to do
            // with the query, and light up scattered letters while doing it.
            var headerMatch = FuzzySearch.Match(section.Header.Text, query) is not null;
            var wholeSection = headerMatch;
            var anyRow = false;
            foreach (var row in section.Rows)
            {
                var visible = wholeSection || RowMatches(row, query);
                row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                anyRow |= visible;
            }

            UpdateDividers(section.Card);
            var visibleSection = anyRow || (wholeSection && section.Rows.Count == 0);
            section.Header.Visibility = visibleSection ? Visibility.Visible : Visibility.Collapsed;
            section.Card.Visibility = visibleSection ? Visibility.Visible : Visibility.Collapsed;
            found |= visibleSection;

            if (!visibleSection)
            {
                continue;
            }

            if (headerMatch)
            {
                Highlight(section.Header, query);
            }

            foreach (var row in section.Rows)
            {
                if (row.Visibility == Visibility.Visible)
                {
                    HighlightRow(row, query);
                }
            }
        }

        NoResults.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
        if (!found)
        {
            NoResultsTitle.Text = $"No results for \u201c{query}\u201d";
        }
    }

    /// <summary>The page's sections, in the order the XAML declares them.</summary>
    private List<SectionIndex> IndexSections()
    {
        var sections = new List<SectionIndex>();
        TextBlock? header = null;
        foreach (var child in SettingsRoot.Children)
        {
            if (child is TextBlock { Tag: "section" } title)
            {
                header = title;
                continue;
            }

            if (header is null || child is not Border card || card.Style != _cardStyle)
            {
                continue;
            }

            var rows = new List<Grid>();
            CollectRows(card, rows);
            sections.Add(new SectionIndex(header, card, rows));
            header = null;
        }

        return sections;
    }

    /// <summary>Rows are the grids the page uses for a setting. A card's other text is
    /// explanatory prose and does not take part in the search.</summary>
    private void CollectRows(DependencyObject node, List<Grid> rows)
    {
        if (node is Grid grid && IsRow(grid))
        {
            rows.Add(grid);
            return;
        }

        if (node is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                CollectRows(child, rows);
            }

            return;
        }

        if (node is Border border && border.Child is { } content)
        {
            CollectRows(content, rows);
        }
    }

    /// <summary>A divider is only shown when there are visible rows on both sides of it,
    /// so a card closes up cleanly when its rows are filtered out.</summary>
    private void UpdateDividers(DependencyObject node)
    {
        if (node is Grid grid && IsRow(grid))
        {
            return;
        }

        if (node is Panel panel)
        {
            for (var i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is not Border divider || !IsDivider(divider))
                {
                    continue;
                }

                var before = NeighborRow(panel, i, -1);
                var after = NeighborRow(panel, i, 1);
                var beforeShown = before is null || before.Visibility == Visibility.Visible;
                var afterShown = after is null || after.Visibility == Visibility.Visible;
                divider.Visibility = beforeShown && afterShown && (before is not null || after is not null)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            foreach (var child in panel.Children)
            {
                UpdateDividers(child);
            }

            return;
        }

        if (node is Border border && border.Child is { } content)
        {
            UpdateDividers(content);
        }
    }

    private Grid? NeighborRow(Panel panel, int index, int direction)
    {
        for (var i = index + direction; i >= 0 && i < panel.Children.Count; i += direction)
        {
            if (panel.Children[i] is Grid row && IsRow(row))
            {
                return row;
            }
        }

        return null;
    }

    private bool IsRow(Grid row) => row.Style == _rowStyle;

    private bool IsDivider(Border divider) => divider.Style == _dividerStyle;

    private static bool RowMatches(Grid row, string query)
    {
        var blocks = new List<TextBlock>();
        var pieces = new List<string>();
        CollectRowText(row, blocks, pieces);
        return FuzzySearch.Match(string.Join("\n", pieces), query) is not null;
    }

    /// <summary>The text a row shows: its labels and descriptions, plus a button's label
    /// and the chosen value of a combo. Control templates are not walked, so a switch's
    /// internal "On"/"Off" text never matches.</summary>
    private static void CollectRowText(DependencyObject node, List<TextBlock> blocks, List<string> pieces)
    {
        if (node is TextBlock text)
        {
            blocks.Add(text);
            if (!string.IsNullOrWhiteSpace(text.Text))
            {
                pieces.Add(text.Text);
            }

            return;
        }

        if (node is ButtonBase button)
        {
            if (button.Content is string label && !string.IsNullOrWhiteSpace(label))
            {
                pieces.Add(label);
            }

            return;
        }

        if (node is ComboBox combo)
        {
            if (combo.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
            {
                pieces.Add(selected);
            }

            return;
        }

        if (node is Control)
        {
            return;
        }

        if (node is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                CollectRowText(child, blocks, pieces);
            }

            return;
        }

        if (node is Border border && border.Child is { } content)
        {
            CollectRowText(content, blocks, pieces);
        }
    }

    private void HighlightRow(Grid row, string query)
    {
        var blocks = new List<TextBlock>();
        var pieces = new List<string>();
        CollectRowText(row, blocks, pieces);
        foreach (var block in blocks)
        {
            Highlight(block, query);
        }
    }

    private void Highlight(TextBlock block, string query)
    {
        block.TextHighlighters.Clear();
        var ranges = RangesFor(block.Text, query);
        if (ranges.Count == 0)
        {
            return;
        }

        var highlighter = new TextHighlighter { Background = HighlightBrush() };
        foreach (var range in ranges)
        {
            highlighter.Ranges.Add(new TextRange
            {
                StartIndex = range.Start,
                Length = range.Length,
            });
        }

        block.TextHighlighters.Add(highlighter);
    }

    /// <summary>Each word is highlighted where it matches, even when the other words in
    /// the query matched a different part of the row.</summary>
    private static List<SearchRange> RangesFor(string? text, string query)
    {
        var ranges = new List<SearchRange>();
        if (string.IsNullOrEmpty(text))
        {
            return ranges;
        }

        foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (FuzzySearch.Match(text, word) is { } match)
            {
                ranges.AddRange(match.Ranges);
            }
        }

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<SearchRange>(ranges.Count);
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = last with
                {
                    Length = Math.Max(last.Start + last.Length, range.Start + range.Length) - last.Start,
                };
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private Brush HighlightBrush()
    {
        if (_highlightBrush is not null)
        {
            return _highlightBrush;
        }

        var accent = Application.Current.Resources["SystemAccentColor"] is Windows.UI.Color color
            ? color
            : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        _highlightBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x4D, accent.R, accent.G, accent.B));
        return _highlightBrush;
    }

    private static void ClearHighlights(DependencyObject node)
    {
        if (node is TextBlock block)
        {
            block.TextHighlighters.Clear();
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            ClearHighlights(VisualTreeHelper.GetChild(node, i));
        }
    }

    private sealed record SectionIndex(TextBlock Header, Border Card, List<Grid> Rows);

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

    private async void OnLosslessDownloadsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        var on = LosslessDownloadsSwitch.IsOn;

        // Turning it on with no server yet asks for one; backing out leaves it off.
        if (on && string.IsNullOrWhiteSpace(App.Settings.LosslessServerUrl)
            && !await AskLosslessServerAsync())
        {
            _loaded = false;
            LosslessDownloadsSwitch.IsOn = false;
            _loaded = true;
            return;
        }

        App.Settings.Update(s => s.LosslessDownloads = on);
        RefreshLosslessServer();
    }

    private async void OnLosslessServerClick(object sender, RoutedEventArgs e)
    {
        if (await AskLosslessServerAsync())
        {
            RefreshLosslessServer();
        }
    }

    /// <summary>Asks for the server's address and secret; true when they were saved.</summary>
    private async Task<bool> AskLosslessServerAsync()
    {
        var server = await Dialogs.AskLosslessServerAsync(
            App.Settings.LosslessServerUrl, App.Settings.LosslessServerSecret);
        if (server is not { } chosen)
        {
            return false;
        }

        App.Settings.Update(s =>
        {
            s.LosslessServerUrl = chosen.Url;
            s.LosslessServerSecret = chosen.Secret.Length > 0 ? chosen.Secret : null;
        });
        RefreshLosslessServer();
        return true;
    }

    private void RefreshLosslessServer()
    {
        var url = App.Settings.LosslessServerUrl;
        var hasServer = !string.IsNullOrWhiteSpace(url);
        LosslessServerText.Text = hasServer && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? (App.Settings.LosslessServerSecret is { Length: > 0 } ? $"{uri.Host}, signed" : uri.Host)
            : "Not set up. Lossless downloads need the address of a server to take them from.";
        LosslessServerButton.Content = hasServer ? "Change…" : "Set up…";
        AutomationProperties.SetName(LosslessServerButton,
            hasServer ? "Change lossless server" : "Set up lossless server");
    }

    private void OnGlobalContentToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Update(s => s.ShowsGlobalContent = GlobalContentSwitch.IsOn);
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

    private void RefreshEnergySaver()
    {
        EnergySaverText.Text = EnergySaver.IsOn ? "On" : "Off";
        ApplySettingsSearch();
    }

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
        ApplySettingsSearch();
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

        ApplySettingsSearch();
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

    // MARK: youtube music

    /// <summary>Who is signed in, what the account is doing, and the one action that fits:
    /// sign in, sync, or sign in again after YouTube ended the session.</summary>
    private void RefreshYouTube()
    {
        var account = App.Account;
        var signedIn = account.IsSignedIn;
        var expired = account.IsExpired;

        YouTubeLibrarySwitch.IsEnabled = signedIn;
        YouTubePlaysSwitch.IsEnabled = signedIn;
        YouTubeSignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        YouTubeButton.Visibility = !signedIn || expired ? Visibility.Visible : Visibility.Collapsed;
        YouTubeButton.Content = signedIn ? "Sign in again…" : "Sign in…";
        YouTubeSyncButton.Visibility = signedIn && !expired && App.Settings.SyncsYouTubeLibrary
            ? Visibility.Visible
            : Visibility.Collapsed;
        YouTubeSyncButton.IsEnabled = !account.IsSyncing;

        if (!signedIn)
        {
            YouTubeTitle.Text = "Not signed in";
            YouTubeText.Text = "Sign in to bring your likes, saved albums, playlists and artists to Koda. Everything already here stays.";
            ApplySettingsSearch();
            return;
        }

        YouTubeTitle.Text = account.Info?.Handle ?? account.Info?.Name ?? "Signed in";
        YouTubeText.Text = expired
            ? "YouTube signed this session out. Sign in again to keep the library in step."
            : account.IsSyncing
                ? "Bringing in your library…"
                : !App.Settings.SyncsYouTubeLibrary
                    ? "Signed in. Library sync is off, so likes and saves stay on this PC."
                    : "Signed in. Your likes, saved albums, playlists and artists stay in step with YouTube Music.";
        ApplySettingsSearch();
    }

    private async void OnYouTubeClick(object sender, RoutedEventArgs e)
    {
        await YouTubeSignInDialog.ShowSignInAsync();
    }

    private async void OnYouTubeSyncClick(object sender, RoutedEventArgs e)
    {
        await App.Account.SyncAsync(force: true);
        RefreshYouTube();
    }

    private async void OnYouTubeSignOutClick(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.ConfirmAsync(
                "Sign out of YouTube Music?",
                "The library stays on this PC with its likes, playlists and history. Your YouTube Music account isn't affected.",
                "Sign Out"))
        {
            App.Account.SignOut();
            Notice("Signed out of YouTube Music. The library is still here.");
        }
    }

    private void OnYouTubeLibraryToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || YouTubeLibrarySwitch.IsOn == App.Settings.SyncsYouTubeLibrary) return;
        App.Settings.Update(s => s.SyncsYouTubeLibrary = YouTubeLibrarySwitch.IsOn);
        RefreshYouTube();
    }

    private void OnYouTubePlaysToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || YouTubePlaysSwitch.IsOn == App.Settings.SendsPlaysToYouTube) return;
        App.Settings.Update(s => s.SendsPlaysToYouTube = YouTubePlaysSwitch.IsOn);
    }

    // MARK: last.fm

    private void RefreshLastFm()
    {
        var lastFm = App.LastFm;
        LastFmTitle.Text = lastFm.IsConnected
            ? lastFm.IsEnabled ? $"Connected as {lastFm.User}" : $"Connected as {lastFm.User}, scrobbling off"
            : "Not connected";
        LastFmButton.Content = lastFm.IsConnected ? "Open" : "Connect…";
        ApplySettingsSearch();
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
        ApplySettingsSearch();
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
        ApplySettingsSearch();
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
