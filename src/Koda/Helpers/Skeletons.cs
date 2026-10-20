using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Koda.Helpers;

/// <summary>
/// Loading placeholders in the shape of the layout that replaces them:
/// redacted placeholders, never spinners. They breathe slowly while
/// waiting; with Windows animations turned off they hold still.
/// </summary>
public static class Skeletons
{
    private static Brush Fill => (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];

    public static Border Block(double width, double height, double radius = 6) => new()
    {
        Width = width,
        Height = height,
        CornerRadius = new CornerRadius(radius),
        Background = Fill,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static Border Line(double height, double radius = 4) => new()
    {
        Height = height,
        CornerRadius = new CornerRadius(radius),
        Background = Fill,
    };

    /// <summary>Song rows: optional index column, thumb, two lines, a duration.</summary>
    public static StackPanel Rows(int count, bool numbered = false, bool thumbs = true)
    {
        var panel = new StackPanel { Spacing = 0 };
        var random = new Random(7);
        for (var i = 0; i < count; i++)
        {
            var row = new Grid { Height = 56, ColumnSpacing = 12, Padding = new Thickness(12, 0, 12, 0) };
            if (numbered) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            if (thumbs) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            var column = 0;
            if (numbered)
            {
                var n = Block(14, 12);
                n.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(n, column++);
                row.Children.Add(n);
            }

            if (thumbs)
            {
                var thumb = Block(40, 40, 6);
                thumb.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(thumb, column++);
                row.Children.Add(thumb);
            }

            var lines = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(Block(120 + random.Next(160), 12));
            lines.Children.Add(Block(70 + random.Next(90), 10));
            Grid.SetColumn(lines, column++);
            row.Children.Add(lines);
            var time = Block(32, 10);
            time.VerticalAlignment = VerticalAlignment.Center;
            time.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(time, column);
            row.Children.Add(time);
            panel.Children.Add(row);
        }

        return Breathe(panel);
    }

    /// <summary>Shelves of square covers, each with a title line above.</summary>
    public static StackPanel Shelves(int shelves, int cards = 7, double size = 160)
    {
        var panel = new StackPanel { Spacing = 28 };
        for (var s = 0; s < shelves; s++)
        {
            var shelf = new StackPanel { Spacing = 12 };
            shelf.Children.Add(Block(180 + s * 40 % 120, 18));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            for (var c = 0; c < cards; c++)
            {
                var card = new StackPanel { Spacing = 8, Width = size };
                card.Children.Add(Block(size, size, 12));
                card.Children.Add(Block(size * 0.8, 12));
                card.Children.Add(Block(size * 0.5, 10));
                row.Children.Add(card);
            }

            shelf.Children.Add(new ScrollViewer
            {
                Content = row,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
            });
            panel.Children.Add(shelf);
        }

        return Breathe(panel);
    }

    /// <summary>A collection header: square (or round) art beside title, byline, meta and buttons.</summary>
    public static Grid Hero(bool round = false, double size = 200)
    {
        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Block(size, size, round ? size / 2 : 16));
        var text = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Bottom };
        text.Children.Add(Block(300, 28, 6));
        text.Children.Add(Block(160, 14));
        text.Children.Add(Block(220, 12));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Block(96, 32, 6));
        buttons.Children.Add(Block(96, 32, 6));
        buttons.Children.Add(Block(32, 32, 6));
        text.Children.Add(buttons);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return Breathe(grid);
    }

    /// <summary>A slow opacity pulse on the element's visual, unless Windows has animations off.</summary>
    public static T Breathe<T>(T element) where T : UIElement
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            return element;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var pulse = compositor.CreateScalarKeyFrameAnimation();
        pulse.InsertKeyFrame(0f, 1f);
        pulse.InsertKeyFrame(0.5f, 0.45f);
        pulse.InsertKeyFrame(1f, 1f);
        pulse.Duration = TimeSpan.FromMilliseconds(1600);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Opacity", pulse);
        return element;
    }
}
