using System;
using System.Linq;
using Koda.Core;
using Koda.Core.Video;
using Koda.Pages;
using Koda.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using VideoModel = Koda.Core.Video.Video;

namespace Koda.Helpers;

/// <summary>A video as a card or row draws it.</summary>
public sealed record VideoItem(VideoModel Video)
{
    public string Title => Video.Title;

    public string Byline => Video.Byline;

    public string Duration => Video.DurationText;

    public bool HasDuration => Duration.Length > 0;

    public double Progress => App.Library.ProgressOf(Video.Id)?.Fraction ?? 0;

    public bool HasProgress => Progress > 0.01;
}

/// <summary>A channel as a round card draws it.</summary>
public sealed record ChannelItem(string? Id, string Name, string Detail, Artwork? Avatar)
{
    public static ChannelItem From(ChannelRef channel) => new(channel.Id, channel.Name, "", channel.Avatar);

    public static ChannelItem From(ChannelSummary channel) =>
        new(channel.Id, channel.Name, channel.Subscribers ?? channel.Handle ?? "", channel.Avatar);
}

/// <summary>A video's frame on an Image, with YouTube's smaller frame when the wide one is missing.</summary>
public static class VideoArt
{
    public static readonly DependencyProperty VideoProperty = DependencyProperty.RegisterAttached(
        "Video", typeof(object), typeof(VideoArt), new PropertyMetadata(null, OnVideoChanged));

    public static object? GetVideo(DependencyObject d) => d.GetValue(VideoProperty);

    public static void SetVideo(DependencyObject d, object? value) => d.SetValue(VideoProperty, value);

    /// <summary>YouTube's small frame, a fifth of the size: a card under its sharp one shows a
    /// picture almost at once, and the sharp frame fades in over it when it arrives.</summary>
    public static readonly DependencyProperty QuickProperty = DependencyProperty.RegisterAttached(
        "Quick", typeof(object), typeof(VideoArt), new PropertyMetadata(null, OnQuickChanged));

    public static object? GetQuick(DependencyObject d) => d.GetValue(QuickProperty);

    public static void SetQuick(DependencyObject d, object? value) => d.SetValue(QuickProperty, value);

    private static void OnQuickChanged(DependencyObject d, DependencyPropertyChangedEventArgs args)
    {
        if (d is Image image)
        {
            Show(image, args.NewValue as VideoModel, quick: true);
        }
    }

    private static void OnVideoChanged(DependencyObject d, DependencyPropertyChangedEventArgs args)
    {
        if (d is Image image)
        {
            Show(image, args.NewValue as VideoModel, quick: false);
        }
    }

    private static void Show(Image image, VideoModel? video, bool quick)
    {
        image.OpacityTransition ??= new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
        image.Opacity = 0;
        if (video is null)
        {
            image.Source = null;
            return;
        }

        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelWidth = quick ? 320 : 400,
        };
        bitmap.ImageOpened += (_, _) =>
        {
            if (image.Source == bitmap)
            {
                image.Opacity = 1;
            }
        };
        bitmap.ImageFailed += (_, _) =>
        {
            if (image.Source == bitmap && bitmap.UriSource != video.FallbackThumbnailUrl)
            {
                bitmap.UriSource = video.FallbackThumbnailUrl;
            }
        };
        bitmap.UriSource = quick ? video.FallbackThumbnailUrl : video.ThumbnailUrl;
        image.Source = bitmap;
    }
}

/// <summary>
/// A grid of videos that always fills the page: as many columns as fit at a comfortable
/// width, each stretched to share the row, with every frame kept at 16:9.
/// </summary>
public static class VideoGrid
{
    private const double CardWidth = 260;
    private const double TextHeight = 84;

    /// <summary>Marks the card's frame so it can follow the column width.</summary>
    public static readonly DependencyProperty FrameProperty = DependencyProperty.RegisterAttached(
        "Frame", typeof(bool), typeof(VideoGrid), new PropertyMetadata(false, OnFrameChanged));

    public static bool GetFrame(DependencyObject d) => (bool)d.GetValue(FrameProperty);

    public static void SetFrame(DependencyObject d, bool value) => d.SetValue(FrameProperty, value);

    private static void OnFrameChanged(DependencyObject d, DependencyPropertyChangedEventArgs args)
    {
        if (d is FrameworkElement frame)
        {
            frame.SizeChanged += (_, e) =>
            {
                var height = Math.Round(e.NewSize.Width * 9 / 16);
                if (Math.Abs(frame.Height - height) > 0.5 || double.IsNaN(frame.Height))
                {
                    frame.Height = height;
                }
            };
        }
    }

    /// <summary>Keeps a grid's cells sized to its width.</summary>
    public static void Fit(GridView grid, double cardWidth = CardWidth, double? fixedHeight = null)
    {
        void Apply()
        {
            if (grid.ItemsPanelRoot is not ItemsWrapGrid panel || grid.ActualWidth <= 0)
            {
                return;
            }

            var room = grid.ActualWidth - grid.Padding.Left - grid.Padding.Right - 2;
            var columns = Math.Max(1, (int)Math.Floor(room / cardWidth));
            var width = Math.Floor(room / columns);
            panel.ItemWidth = width;
            panel.ItemHeight = fixedHeight ?? Math.Round((width - 12) * 9 / 16) + TextHeight;
        }

        grid.SizeChanged += (_, _) => Apply();
        grid.Loaded += (_, _) => Apply();
        grid.LayoutUpdated += OnFirstLayout;

        void OnFirstLayout(object? sender, object e)
        {
            if (grid.ItemsPanelRoot is ItemsWrapGrid)
            {
                grid.LayoutUpdated -= OnFirstLayout;
                Apply();
            }
        }
    }

    /// <summary>
    /// Endless scrolling: asks for more whenever under three screens of list are left, and keeps
    /// asking until the window is full, because a list that fits on screen never scrolls and so
    /// would never ask at all. A list has no scroller until it is first shown, so that is
    /// waited for. <paramref name="more"/> answers whether it's worth asking again straight away.
    /// Returns the check, for a list that was just filled anew.
    /// </summary>
    public static Action KeepFilled(ListViewBase list, Func<System.Threading.Tasks.Task<bool>> more)
    {
        ScrollViewer? scroller = null;
        var asking = false;

        async void Check()
        {
            if (asking || scroller is null || !list.IsLoaded) return;
            // Three screens ahead: the next page should be there before it's scrolled to.
            if (scroller.ScrollableHeight - scroller.VerticalOffset >= scroller.ViewportHeight * 3) return;
            asking = true;
            bool again;
            try
            {
                again = await more();
            }
            finally
            {
                asking = false;
            }

            if (again) list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Check);
        }

        void OnLayout(object? sender, object e)
        {
            if (ScrollerOf(list) is not { } found) return;
            list.LayoutUpdated -= OnLayout;
            scroller = found;
            scroller.ViewChanged += (_, _) => Check();
            scroller.SizeChanged += (_, _) => Check();
            Check();
        }

        list.LayoutUpdated += OnLayout;
        return () => list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Check);
    }

    /// <summary>The scroller inside a list.</summary>
    public static ScrollViewer? ScrollerOf(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroller)
            {
                return scroller;
            }

            if (ScrollerOf(child) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    /// <summary>Right-click on a video: the same menu everywhere.</summary>
    public static void AttachMenu(ListViewBase list, Action<VideoModel>? onRemove = null)
    {
        list.RightTapped += (_, args) =>
        {
            if ((args.OriginalSource as FrameworkElement)?.DataContext is not VideoItem item)
            {
                return;
            }

            MenuFor(item.Video, onRemove).ShowAt(args.OriginalSource as FrameworkElement, args.GetPosition(args.OriginalSource as UIElement));
            args.Handled = true;
        };
    }

    public static MenuFlyout MenuFor(VideoModel video, Action<VideoModel>? onRemove = null)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(SongMenu.Item("Play", "", () => App.Shell?.OpenVideo(video)));
        if (video.Channel?.Id is { } channel)
        {
            menu.Items.Add(SongMenu.Item("Go to channel", "", () => App.Shell?.NavigateTo(typeof(VideoChannelPage), channel)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(SongMenu.Item("Copy link", "", () => CopyLink(video)));
        menu.Items.Add(SongMenu.Item("Open on YouTube", "", () => OpenOnYouTube(video)));
        if (onRemove is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(SongMenu.Item("Remove from history", "", () => onRemove(video)));
        }

        return menu;
    }

    public static Uri LinkOf(VideoModel video) => new($"https://www.youtube.com/watch?v={video.Id}");

    public static void CopyLink(VideoModel video)
    {
        var package = new DataPackage();
        package.SetText(LinkOf(video).ToString());
        Clipboard.SetContent(package);
        App.Shell?.Note("Link copied");
    }

    public static void OpenOnYouTube(VideoModel video) => _ = Windows.System.Launcher.LaunchUriAsync(LinkOf(video));

    /// <summary>
    /// The list's loading states, in the list's own shape. Cards are added without their entrance
    /// animation, which staggered each page in. While more is coming a row of placeholders stands
    /// where the next cards will land, sized exactly like them, so scrolling never meets an end
    /// that isn't one and new cards take the place of the row without anything jumping.
    /// </summary>
    public static Action<bool> PrepareLoading(GridView grid, ContentControl firstLoad)
    {
        grid.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();

        void SizeFirstLoad()
        {
            if (firstLoad.Content is null || firstLoad.ActualWidth <= 0) return;
            var columns = Math.Max(1, (int)Math.Floor(firstLoad.ActualWidth / CardWidth));
            var frame = Math.Round((firstLoad.ActualWidth / columns - 16) * 9 / 16);
            if (firstLoad.Tag is ValueTuple<int, double> shown && shown == (columns, frame)) return;
            firstLoad.Tag = (columns, frame);
            firstLoad.Content = Skeleton(4, columns, frame);
        }

        firstLoad.SizeChanged += (_, _) => SizeFirstLoad();
        firstLoad.RegisterPropertyChangedCallback(ContentControl.ContentProperty, (_, _) =>
        {
            if (firstLoad.Content is null) firstLoad.Tag = null;
            else if (firstLoad.Tag is null) SizeFirstLoad();
        });

        Grid? row = null;
        var rowShape = (Columns: 0, Width: 0d);

        void ShapeRow()
        {
            if (grid.Footer is null || grid.ItemsPanelRoot is not ItemsWrapGrid panel || panel.ItemWidth <= 0) return;
            var room = grid.ActualWidth - grid.Padding.Left - grid.Padding.Right;
            var columns = Math.Max(1, (int)Math.Round(room / panel.ItemWidth));
            if (rowShape == (columns, panel.ItemWidth)) return;
            rowShape = (columns, panel.ItemWidth);
            row = Skeleton(1, columns, Math.Round((panel.ItemWidth - 12) * 9 / 16));
            row.Margin = new Thickness(6, 6, 6, 0);
            row.ColumnSpacing = 12;
            grid.Footer = row;
        }

        grid.SizeChanged += (_, _) => ShapeRow();
        return more =>
        {
            if (!more)
            {
                grid.Footer = null;
                rowShape = (0, 0);
                return;
            }

            grid.Footer ??= new Grid();
            ShapeRow();
        };
    }

    /// <summary>A grid of frames in the shape of what will replace it.</summary>
    public static Grid Skeleton(int rows = 3, int columns = 3, double frameHeight = 170)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 20 };
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < rows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < columns; c++)
            {
                var card = new StackPanel { Spacing = 8 };
                card.Children.Add(new Border
                {
                    Height = frameHeight,
                    CornerRadius = new CornerRadius(8),
                    Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                });
                card.Children.Add(Skeletons.Block(200, 13));
                card.Children.Add(Skeletons.Block(130, 10));
                Grid.SetRow(card, r);
                Grid.SetColumn(card, c);
                grid.Children.Add(card);
            }
        }

        return Skeletons.Breathe(grid);
    }
}
