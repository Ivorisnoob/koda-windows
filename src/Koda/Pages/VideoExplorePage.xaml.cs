using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Koda.Core.Video;
using Koda.Helpers;
using Koda.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Koda.Pages;

/// <summary>YouTube's topic pages as shelves that scroll sideways. Live is left out for now.</summary>
public sealed partial class VideoExplorePage : Page, IVideoPage
{
    private CancellationTokenSource? _cancel;

    public VideoExplorePage()
    {
        InitializeComponent();
        Unloaded += (_, _) => _cancel?.Cancel();
    }

    private void OnTopicChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (Topics.SelectedItem?.Tag is not string browseId) return;
        _cancel?.Cancel();
        var cancel = _cancel = new CancellationTokenSource();
        Shelves.Children.Clear();
        Status.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Collapsed;
        LoadingView.Content = VideoGrid.Skeleton(2, 3);
        LoadingView.Visibility = Visibility.Visible;
        try
        {
            var shelves = await EngineHost.Music.ExploreAsync(browseId, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            foreach (var shelf in shelves.Where(s => s.Videos.Length > 0))
            {
                Shelves.Children.Add(Build(shelf));
            }

            LoadingView.Visibility = Visibility.Collapsed;
            LoadingView.Content = null;
            if (Shelves.Children.Count == 0)
            {
                Status.Show("Nothing here right now", "Try another topic.", "");
                return;
            }

            Scroller.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // Another topic took over.
        }
        catch (Exception error)
        {
            if (cancel.IsCancellationRequested) return;
            LoadingView.Visibility = Visibility.Collapsed;
            LoadingView.Content = null;
            Status.ShowError(error, () => _ = LoadAsync());
        }
    }

    private UIElement Build(VideoShelf shelf)
    {
        var section = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(shelf.Title))
        {
            section.Children.Add(new TextBlock
            {
                Text = shelf.Title,
                Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            });
        }

        var row = new GridView
        {
            Height = 274,
            Margin = new Thickness(-6, 0, 0, 0),
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            ItemTemplate = (DataTemplate)Application.Current.Resources["VideoCardTemplate"],
            ItemContainerStyle = (Style)Application.Current.Resources["VideoCardItemStyle"],
            ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<ItemsPanelTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">"
                + "<ItemsWrapGrid Orientation=\"Vertical\" ItemWidth=\"300\" ItemHeight=\"262\" MaximumRowsOrColumns=\"1\" /></ItemsPanelTemplate>"),
            ItemsSource = shelf.Videos.Select(v => new VideoItem(v)).ToArray(),
        };
        ScrollViewer.SetHorizontalScrollMode(row, ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(row, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollMode(row, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(row, ScrollBarVisibility.Disabled);
        row.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is VideoItem item) App.Shell?.OpenVideo(item.Video);
        };
        VideoGrid.AttachMenu(row);
        section.Children.Add(row);
        return section;
    }
}
