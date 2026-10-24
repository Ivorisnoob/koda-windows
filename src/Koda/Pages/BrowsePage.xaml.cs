using System;
using System.Linq;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Helpers;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace Koda.Pages;

/// <summary>
/// Browse: the searches the listener made lately, and every mood and genre YouTube Music
/// offers as a tile. Search itself lives in the title bar's field, on every page.
/// </summary>
public sealed partial class BrowsePage : Page
{
    private static MoodCard[]? _moods;

    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 0, 120, 212),
        Color.FromArgb(255, 135, 100, 184),
        Color.FromArgb(255, 202, 80, 16),
        Color.FromArgb(255, 16, 137, 62),
        Color.FromArgb(255, 194, 57, 75),
        Color.FromArgb(255, 73, 130, 5),
        Color.FromArgb(255, 0, 95, 184),
        Color.FromArgb(255, 180, 0, 158),
        Color.FromArgb(255, 142, 82, 0),
        Color.FromArgb(255, 49, 86, 160),
    ];

    public BrowsePage()
    {
        InitializeComponent();
        App.Library.Changed += OnLibraryChanged;
        Unloaded += (_, _) => App.Library.Changed -= OnLibraryChanged;
        SearchChips.ContextRequested += OnSearchContextRequested;
    }

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        RefreshSearches();
        if (_moods is { } cached)
        {
            ShowMoods(cached);
        }
        else
        {
            _ = LoadMoodsAsync();
        }
    }

    private void OnLibraryChanged() => DispatcherQueue.TryEnqueue(RefreshSearches);

    private void RefreshSearches()
    {
        var searches = App.Library.Searches.Take(12).ToArray();
        SearchChips.ItemsSource = searches;
        RecentSection.Visibility = searches.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadMoodsAsync()
    {
        Error.Visibility = Visibility.Collapsed;
        MoodGrid.Visibility = Visibility.Collapsed;
        SkeletonHost.Content = Skeletons.Shelves(1);
        try
        {
            var explore = await EngineHost.Music.ExploreAsync();
            // Music only: YouTube's podcast entries have no place here.
            var moods = explore.Moods
                .Where(m => m.Title.Length > 0 && !m.Title.Contains("Podcast", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _moods = [.. moods.Select((m, i) =>
            {
                var color = Palette[i % Palette.Length];
                return new MoodCard(m, m.Title, new SolidColorBrush(Color.FromArgb(0x34, color.R, color.G, color.B)));
            })];
            ShowMoods(_moods);
        }
        catch (Exception error)
        {
            Error.ShowError(error, () =>
            {
                _moods = null;
                _ = LoadMoodsAsync();
            });
        }
        finally
        {
            SkeletonHost.Content = null;
        }
    }

    private void ShowMoods(MoodCard[] moods)
    {
        MoodGrid.ItemsSource = moods;
        MoodGrid.Visibility = moods.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (moods.Length == 0)
        {
            Error.Show("No moods to show", "YouTube Music didn't send any this time. Try again in a moment.", "\uE8A9",
                "Try again", () =>
                {
                    _moods = null;
                    _ = LoadMoodsAsync();
                });
        }
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string query })
        {
            App.Shell?.SearchFor(query);
        }
    }

    private void OnClearSearchesClick(object sender, RoutedEventArgs e) => App.Library.ClearSearches();

    private void OnSearchContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement { DataContext: string query })
        {
            return;
        }

        var menu = new MenuFlyout();
        menu.Items.Add(SongMenu.Item("Search again", "\uE721", () => App.Shell?.SearchFor(query)));
        menu.Items.Add(SongMenu.Item("Remove from recent searches", "\uE74D", () => App.Library.RemoveSearch(query)));
        if (args.TryGetPosition(SearchChips, out var position))
        {
            menu.ShowAt(SearchChips, position);
        }
        else
        {
            menu.ShowAt(SearchChips);
        }

        args.Handled = true;
    }

    private void OnMoodClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MoodCard card)
        {
            Navigator.Go(typeof(MoodPage), card.Mood);
        }
    }
}
