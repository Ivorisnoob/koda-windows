using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Koda.Controls;

/// <summary>Which way the skip arrow turns.</summary>
public enum SkipDirection
{
    Back,
    Forward,
}

/// <summary>
/// The skip button's icon: a circular arrow with the distance it jumps inside it, the
/// shape Windows' own media surfaces draw. Segoe Fluent Icons ships only the ten-second
/// back and thirty-second forward pair, and the interval is the listener's, so the
/// drawing is Koda's: an arc, an arrowhead and the number, at any interval, either way.
/// </summary>
public sealed class SkipIcon : Grid
{
    private const double Design = 24;
    private const double Center = Design / 2;
    private const double Radius = 8.2;

    private readonly Grid _arrows = new();
    private readonly Path _arc = new();
    private readonly Path _head = new();
    private readonly TextBlock _number = new();

    public SkipIcon()
    {
        var design = new Grid { Width = Design, Height = Design };

        _arc.StrokeThickness = 1.8;
        _arc.StrokeStartLineCap = PenLineCap.Round;
        _arc.StrokeEndLineCap = PenLineCap.Round;
        _head.StrokeThickness = 0;
        _arrows.Children.Add(_arc);
        _arrows.Children.Add(_head);
        design.Children.Add(_arrows);

        _number.HorizontalAlignment = HorizontalAlignment.Center;
        _number.VerticalAlignment = VerticalAlignment.Center;
        _number.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _number.IsTextScaleFactorEnabled = false;
        design.Children.Add(_number);

        Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = design });

        ActualThemeChanged += (_, _) => Paint();
        Build();
    }

    public static readonly DependencyProperty DirectionProperty = DependencyProperty.Register(
        nameof(Direction),
        typeof(SkipDirection),
        typeof(SkipIcon),
        new PropertyMetadata(SkipDirection.Forward, (d, _) => ((SkipIcon)d).Build()));

    public static readonly DependencyProperty SecondsProperty = DependencyProperty.Register(
        nameof(Seconds),
        typeof(int),
        typeof(SkipIcon),
        new PropertyMetadata(10, (d, _) => ((SkipIcon)d).Build()));

    /// <summary>Back turns the arrow counter-clockwise, ahead turns it clockwise.</summary>
    public SkipDirection Direction
    {
        get => (SkipDirection)GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    /// <summary>The number drawn inside the ring, and what the button jumps.</summary>
    public int Seconds
    {
        get => (int)GetValue(SecondsProperty);
        set => SetValue(SecondsProperty, value);
    }

    /// <summary>The arc, the arrowhead and the number; the number sits in the ring.</summary>
    private void Build()
    {
        var start = ToRadians(-56);
        var sweep = ToRadians(292);
        var from = PointOn(start);
        var to = PointOn(start + sweep);

        var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = to,
            Size = new Size(Radius, Radius),
            IsLargeArc = true,
            SweepDirection = SweepDirection.Clockwise,
        });

        _arc.Data = new PathGeometry { Figures = { figure } };

        // The head sits at the arc's start, along its tangent: to the right at the top
        // for ahead, mirrored for back.
        var tangentX = -Math.Sin(start);
        var tangentY = Math.Cos(start);
        var normalX = -tangentY;
        var normalY = tangentX;
        var tip = Offset(from, tangentX, tangentY, 3.6);
        var root = Offset(from, tangentX, tangentY, -1.1);

        var head = new PathFigure { StartPoint = tip, IsClosed = true, IsFilled = true };
        head.Segments.Add(new LineSegment { Point = Offset(root, normalX, normalY, 2.4) });
        head.Segments.Add(new LineSegment { Point = Offset(root, normalX, normalY, -2.4) });
        _head.Data = new PathGeometry { Figures = { head } };

        _arrows.RenderTransform = Direction == SkipDirection.Forward
            ? null
            : new ScaleTransform { ScaleX = -1, CenterX = Center };

        _number.Text = Seconds.ToString();
        _number.FontSize = Seconds >= 100 ? 6.6 : 8.2;

        Paint();
    }

    /// <summary>White on a dark surface, near-black on a light one; the pages that host
    /// this icon can ask for the other theme, so the page decides, not the app.</summary>
    private void Paint()
    {
        var dark = ActualTheme == ElementTheme.Dark;
        var brush = new SolidColorBrush(dark ? Colors.White : Color.FromArgb(255, 0x20, 0x20, 0x20));
        _arc.Stroke = brush;
        _head.Fill = brush;
        _number.Foreground = brush;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    private static Point PointOn(double radians) =>
        new(Center + Radius * Math.Cos(radians), Center + Radius * Math.Sin(radians));

    private static Point Offset(Point from, double x, double y, double by) =>
        new(from.X + x * by, from.Y + y * by);
}
