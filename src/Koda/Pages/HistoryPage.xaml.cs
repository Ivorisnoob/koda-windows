using System;
using System.Collections.Generic;
using System.Linq;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Koda.Pages;

/// <summary>
/// Every play, newest first, under the day it happened: Recently Played keeps one row per
/// song, History keeps each time. A row plays that day from where it sits; a play can be
/// removed on its own, and the whole history can be cleared with a question first.
/// </summary>
public sealed partial class HistoryPage : Page
{
    private Song[] _songs = [];

    public HistoryPage()
    {
        InitializeComponent();
        App.Library.Changed += OnLibraryChanged;
        Unloaded += (_, _) => App.Library.Changed -= OnLibraryChanged;
        Entries.ContextRequested += OnContextRequested;
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        Render();
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        var history = App.Library.History;
        _songs = [.. history.Select(p => p.Song)];

        var rows = new List<object>();
        var index = 0;
        while (index < history.Length)
        {
            var day = history[index].Date.ToLocalTime().Date;
            var plays = new List<PlayEvent>();
            while (index < history.Length && history[index].Date.ToLocalTime().Date == day)
            {
                plays.Add(history[index++]);
            }

            var songs = plays.Select(p => p.Song).ToArray();
            rows.Add(new HistoryDayRow(plays[0].Date, plays.Count));
            for (var i = 0; i < plays.Count; i++)
            {
                rows.Add(new HistoryPlayRow(plays[i], songs, i));
            }
        }

        Entries.ItemsSource = rows;
        HeroDetail.Text = history.Length switch
        {
            0 => "",
            1 => "1 play",
            _ => $"{Format.Count(history.Length)} plays",
        };

        var any = history.Length > 0;
        PlayButton.Visibility = ShuffleButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Entries.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        if (any)
        {
            Empty.Visibility = Visibility.Collapsed;
            return;
        }

        if (App.Settings.SavesListeningHistory)
        {
            Empty.Show("Nothing played yet", "Each song you play appears here, under the day you played it.", "\uE81C");
        }
        else
        {
            Empty.Show("Listening history is off",
                "Songs you play aren't kept while Save listening history is off. Turn it on in Settings under Privacy.",
                "\uE81C", "Open Settings", () => App.Shell?.NavigateRoot(typeof(SettingsPage)));
        }
    }

    private void OnEntryClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryPlayRow row)
        {
            _ = PlaybackService.Instance.PlayNowAsync(row.Song, row.DaySongs);
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_songs.Length > 0)
        {
            _ = PlaybackService.Instance.PlayNowAsync(_songs[0], _songs.Take(200).ToArray());
        }
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e) => _ = PlaybackService.Instance.ShuffleAsync(_songs.Take(200).ToArray());

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.ConfirmAsync("Clear your listening history?",
                "Recently Played and Quick picks start over. Liked songs and stats stay.", "Clear History"))
        {
            App.Library.ClearHistory();
        }
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement { DataContext: HistoryPlayRow row })
        {
            return;
        }

        var remove = SongMenu.Item("Remove from History", "\uE74D", () => App.Library.RemoveFromHistory(row.Event.Id));
        var menu = SongMenu.Build(row.Song, row.DaySongs, [remove]);
        if (args.TryGetPosition(Entries, out var position))
        {
            menu.ShowAt(Entries, position);
        }
        else
        {
            menu.ShowAt(Entries);
        }

        args.Handled = true;
    }
}
