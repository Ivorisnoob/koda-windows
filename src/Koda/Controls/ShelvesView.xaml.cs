using System.Collections.Generic;
using System.Linq;
using Koda.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Koda.Controls;

/// <summary>Rows of covers (Home, a mood, an artist's releases): a tap opens or plays, right-click on a song offers its menu.</summary>
public sealed partial class ShelvesView : UserControl
{
    public ShelvesView()
    {
        InitializeComponent();
    }

    public IReadOnlyList<ShelfView> Shelves
    {
        get => ShelfList.ItemsSource as IReadOnlyList<ShelfView> ?? [];
        set => ShelfList.ItemsSource = value;
    }

    private void OnCardClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is CardItem card && sender is ListView { DataContext: ShelfView shelf })
        {
            Navigator.Open(card.Item, shelf.Songs);
        }
    }

    private void OnShelfLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is ListView list && list.Tag is null)
        {
            list.Tag = "menu";
            SongMenu.Attach(list, () => list.DataContext is ShelfView shelf ? shelf.Songs : []);
            list.AddHandler(PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnShelfWheel), true);
        }
    }

    /// <summary>
    /// [judgement] A shelf never steals the page's scroll: its own scrolling is off for pointer
    /// input, so the plain wheel passes to the page. Shift+wheel and a sideways swipe (a
    /// horizontal wheel) move the shelf instead, as in File Explorer and the Store.
    /// </summary>
    private static void OnShelfWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
    {
        if (sender is not ListView list || Descendant<ScrollViewer>(list) is not { } scroller)
        {
            return;
        }

        var point = args.GetCurrentPoint(list).Properties;
        var shift = (args.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
        if (!point.IsHorizontalMouseWheel && !shift)
        {
            return; // the page scrolls
        }

        var delta = point.MouseWheelDelta;
        // A horizontal wheel reports right as positive; a vertical one reports down as negative.
        var step = point.IsHorizontalMouseWheel ? delta : -delta;
        scroller.ChangeView(System.Math.Max(0, scroller.HorizontalOffset + step * 1.5), null, null, true);
        args.Handled = true;
    }

    private void OnStepLeft(object sender, RoutedEventArgs args) => Step(sender, -1);

    private void OnStepRight(object sender, RoutedEventArgs args) => Step(sender, 1);

    private static void Step(object sender, int direction)
    {
        if (sender is not FrameworkElement button || FindShelfList(button) is not { } list || Descendant<ScrollViewer>(list) is not { } scroller)
        {
            return;
        }

        var target = scroller.HorizontalOffset + direction * scroller.ViewportWidth * 0.85;
        scroller.ChangeView(System.Math.Max(0, target), null, null);
    }

    private static ListView? FindShelfList(FrameworkElement from)
    {
        DependencyObject? node = from;
        while (node is not null && node is not Grid { RowDefinitions.Count: 2 })
        {
            node = VisualTreeHelper.GetParent(node);
        }

        return node is Grid grid ? grid.Children.OfType<ListView>().FirstOrDefault() : null;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }

        return null;
    }
}
