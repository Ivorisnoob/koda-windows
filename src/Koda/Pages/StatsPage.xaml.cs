using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Koda.Core;
using Koda.Helpers;
using Koda.Playback;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace Koda.Pages;

/// <summary>
/// Listening Stats: a range in the header, four figures, minutes per day as bars, and the
/// all-time top songs and artists. Everything is worked out from the library's play counts
/// and daily listening, which the engine already keeps.
/// </summary>
public sealed partial class StatsPage : Page
{
    private enum Span
    {
        Week = 7,
        Month = 30,
        Year = 365,
    }

    private sealed record DayPoint(DateTime Day, double Seconds);

    private sealed class ArtistTotal
    {
        public ArtistRef? Ref { get; set; }
        public Artwork? Artwork { get; set; }
        public double Seconds { get; set; }
        public int Plays { get; set; }
    }

    private Span _range = Span.Week;
    private DayPoint[] _chart = [];
    private RankedSongRow[] _topSongs = [];
    private TopArtistRow[] _artists = [];
    private readonly List<Border> _bars = [];
    private int _hover = -1;
    private double _chartMax;

    public StatsPage()
    {
        InitializeComponent();
        App.Library.Changed += OnLibraryChanged;
        Unloaded += (_, _) => App.Library.Changed -= OnLibraryChanged;
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        Render();
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(Render);

    private void OnRangeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _range = (sender.SelectedItem?.Tag as string) switch
        {
            "month" => Span.Month,
            "year" => Span.Year,
            _ => Span.Week,
        };
        Render();
    }

    private void Render()
    {
        var days = (int)_range;
        var today = DateTime.Today;
        var points = new List<DayPoint>(days);
        for (var back = days - 1; back >= 0; back--)
        {
            var day = today.AddDays(-back);
            points.Add(new DayPoint(day, App.Library.ListeningOn(new DateTimeOffset(day))));
        }

        _chart = [.. points];
        _chartMax = points.Count == 0 ? 0 : points.Max(p => p.Seconds);

        var stats = App.Library.SongStats.Values.ToArray();
        var totalSeconds = points.Sum(p => p.Seconds);
        var totalPlays = stats.Sum(s => s.Plays);
        var streak = App.Library.Streak();
        var liked = App.Library.Liked.Length;

        _topSongs = [.. stats
            .OrderByDescending(s => s.Seconds)
            .ThenByDescending(s => s.Plays)
            .Take(25)
            .Select((s, i) => new RankedSongRow(i + 1, s))];

        var byArtist = new Dictionary<string, ArtistTotal>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var stat in stats)
        {
            var reference = stat.Song.Artists.FirstOrDefault();
            var name = reference?.Name ?? stat.Song.ArtistLine;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!byArtist.TryGetValue(name, out var total))
            {
                total = new ArtistTotal { Ref = reference, Artwork = stat.Song.Artwork };
                byArtist[name] = total;
            }

            total.Seconds += stat.Seconds;
            total.Plays += stat.Plays;
            if (total.Ref?.Id is null && reference?.Id is not null)
            {
                total.Ref = reference;
            }

            total.Artwork ??= stat.Song.Artwork;
        }

        _artists = [.. byArtist
            .OrderByDescending(pair => pair.Value.Seconds)
            .ThenByDescending(pair => pair.Value.Plays)
            .Take(10)
            .Select((pair, i) => new TopArtistRow(i + 1, pair.Key, pair.Value.Ref, pair.Value.Artwork, pair.Value.Seconds, pair.Value.Plays))];

        var any = stats.Length > 0;
        ResetButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Tiles.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Lists.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ChartCard.Visibility = any && _range != Span.Year ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any)
        {
            Empty.Show("No listening yet", "Your stats fill in as you play music.", "\uE9D9");
            return;
        }

        BuildTiles(Format.Listening(totalSeconds), Format.Days(streak), Format.Count(totalPlays), Format.Count(liked));

        if (_range != Span.Year)
        {
            ChartTitle.Text = _range == Span.Week ? "Last 7 days" : "Last 30 days";
            ChartHint.Text = $"Average {Format.Listening(totalSeconds / Math.Max(1, points.Count))} a day";
            BuildChart(points);
        }

        TopSongs.ItemsSource = _topSongs;
        TopArtists.ItemsSource = _artists;
        TopSongs.Visibility = _topSongs.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TopArtists.Visibility = _artists.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildTiles(string listened, string streak, string plays, string liked)
    {
        Tiles.ColumnDefinitions.Clear();
        Tiles.Children.Clear();
        var tiles = new (string Glyph, Brush Brush, string Value, string Label)[]
        {
            ("\uE7F6", (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], listened, "Listened"),
            ("\uE787", new SolidColorBrush(Color.FromArgb(255, 232, 137, 12)), streak, "Day streak"),
            ("\uE8D6", new SolidColorBrush(Color.FromArgb(255, 227, 76, 122)), plays, "Songs played"),
            ("\uEB52", new SolidColorBrush(Color.FromArgb(255, 209, 52, 56)), liked, "Liked songs"),
        };

        for (var i = 0; i < tiles.Length; i++)
        {
            Tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tile = Tile(tiles[i].Glyph, tiles[i].Brush, tiles[i].Value, tiles[i].Label);
            Grid.SetColumn(tile, i);
            Tiles.Children.Add(tile);
        }
    }

    private static Border Tile(string glyph, Brush tint, string value, string label)
    {
        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = tint,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new FontIcon { Glyph = glyph, FontSize = 14, Foreground = new SolidColorBrush(Colors.White) },
        };

        var words = new StackPanel { Spacing = 2 };
        words.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        words.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(icon);
        stack.Children.Add(words);

        var card = new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"{label}: {value}");
        return card;
    }

    private void BuildChart(IReadOnlyList<DayPoint> days)
    {
        Chart.Children.Clear();
        Chart.ColumnDefinitions.Clear();
        ChartLabels.Children.Clear();
        ChartLabels.ColumnDefinitions.Clear();
        _bars.Clear();
        _hover = -1;

        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var culture = CultureInfo.CurrentCulture;
        var month = _range == Span.Month;

        for (var i = 0; i < days.Count; i++)
        {
            var point = days[i];
            Chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ChartLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var height = _chartMax <= 0 ? 2 : Math.Max(3, point.Seconds / _chartMax * 150);
            var column = new Grid();
            var bar = new Border
            {
                Height = height,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                Background = accent,
                Opacity = point.Seconds > 0 ? 1 : 0.25,
            };
            ToolTipService.SetToolTip(bar, $"{Format.Day(point.Day)} · {Format.Listening(point.Seconds)}");
            var index = i;
            bar.PointerEntered += (_, _) => ShowDay(index);
            bar.PointerExited += (_, _) => ClearDay();
            column.Children.Add(bar);
            Grid.SetColumn(column, i);
            Chart.Children.Add(column);
            _bars.Add(bar);

            var showLabel = !month || i % 7 == 0 || i == days.Count - 1;
            var label = new TextBlock
            {
                Text = showLabel
                    ? month ? point.Day.ToString("d MMM", culture) : point.Day.ToString("ddd", culture)
                    : "",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            };
            Grid.SetColumn(label, i);
            ChartLabels.Children.Add(label);
        }

        var baseline = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        };
        Grid.SetColumnSpan(baseline, Math.Max(1, days.Count));
        Chart.Children.Add(baseline);
    }

    private void ShowDay(int index)
    {
        if (index < 0 || index >= _chart.Length)
        {
            return;
        }

        _hover = index;
        for (var i = 0; i < _bars.Count; i++)
        {
            _bars[i].Opacity = i == index ? 1 : 0.3;
        }

        var point = _chart[index];
        ChartHint.Text = $"{point.Day.ToString("dddd", CultureInfo.CurrentCulture)} · {Format.Listening(point.Seconds)}";
    }

    private void ClearDay()
    {
        if (_hover < 0)
        {
            return;
        }

        _hover = -1;
        for (var i = 0; i < _bars.Count; i++)
        {
            _bars[i].Opacity = _chart[i].Seconds > 0 ? 1 : 0.25;
        }

        var total = _chart.Sum(p => p.Seconds);
        ChartHint.Text = $"Average {Format.Listening(total / Math.Max(1, _chart.Length))} a day";
    }

    private void OnTopSongClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RankedSongRow row)
        {
            _ = PlaybackService.Instance.PlayNowAsync(row.Song, [.. _topSongs.Select(r => r.Song)]);
        }
    }

    private void OnTopArtistClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TopArtistRow { Ref.Id: { } id })
        {
            Navigator.Go(typeof(ArtistPage), id);
        }
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.ConfirmAsync("Reset your listening stats?",
                "Play counts and listening time start over. Liked songs and history stay.", "Reset Stats"))
        {
            App.Library.ClearStats();
        }
    }
}
