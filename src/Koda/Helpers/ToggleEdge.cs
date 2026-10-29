using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Koda.Helpers;

/// <summary>
/// Puts a ToggleSwitch's track on the right edge of its row, level with the buttons and
/// combos beside it. The template draws the track at its left and keeps room for On/Off
/// text after it (at least 154 wide, whatever the style says), so that room shows as a gap
/// before the edge. The track's own right side is measured after layout and the gap is
/// taken back with a negative margin: right whatever the template, the theme or the text
/// scale make that room.
///
/// <c>helpers:ToggleEdge.HugsRight="True"</c>, usually from a style setter.
/// </summary>
public static class ToggleEdge
{
    public static readonly DependencyProperty HugsRightProperty = DependencyProperty.RegisterAttached(
        "HugsRight", typeof(bool), typeof(ToggleEdge), new PropertyMetadata(false, OnHugsRightChanged));

    public static bool GetHugsRight(DependencyObject d) => (bool)d.GetValue(HugsRightProperty);
    public static void SetHugsRight(DependencyObject d, bool value) => d.SetValue(HugsRightProperty, value);

    private static void OnHugsRightChanged(DependencyObject d, DependencyPropertyChangedEventArgs args)
    {
        if (d is not ToggleSwitch toggle)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            toggle.SizeChanged += OnSizeChanged;
            toggle.Loaded += OnLoaded;
        }
        else
        {
            toggle.SizeChanged -= OnSizeChanged;
            toggle.Loaded -= OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Align((ToggleSwitch)sender);

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Align((ToggleSwitch)sender);

    private static void Align(ToggleSwitch toggle)
    {
        if (Find(toggle, "OuterBorder") is not FrameworkElement track || track.ActualWidth <= 0)
        {
            return;
        }

        // Measured inside the toggle, so the margin being set does not move the answer.
        var trackRight = track.TransformToVisual(toggle).TransformPoint(new Point(track.ActualWidth, 0)).X;
        var gap = Math.Max(0, toggle.ActualWidth - trackRight);
        var margin = toggle.Margin;
        if (Math.Abs(margin.Right + gap) > 0.5)
        {
            toggle.Margin = new Thickness(margin.Left, margin.Top, -gap, margin.Bottom);
        }
    }

    private static DependencyObject? Find(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var found } && found == name)
            {
                return child;
            }

            if (Find(child, name) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
