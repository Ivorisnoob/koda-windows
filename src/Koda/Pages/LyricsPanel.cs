using System;
using Koda.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Koda.Pages;

/// <summary>
/// The lyrics panel: the lines, the one being sung lit, and the words inside it filling
/// from their left edge as they are sung.
///
/// The motion is the Mac's: a spring glide to the line being sung (it lands a third of
/// the way down, so the listener reads ahead), the words lighting as they are sung, and
/// the panel arriving with a short fade. The lines never jump - the glide is a tween
/// with an ease-out-back curve, the same spring the cover uses.
///
/// The listener's own correction for a song (the offset the Library keeps) is honoured,
/// and the audio leads the lyrics by a fraction of a second, so a line lights as it is
/// sung rather than after.
/// </summary>
internal sealed class LyricsPanel : IDisposable
{
    /// <summary>The audio leads the lyrics: a line lights as it is sung, not after.</summary>
    private const double Lead = 0.15;

    /// <summary>Where the line being sung sits in the panel.</summary>
    private const double ReadingAnchor = 0.32;

    /// <summary>How long the glide takes.</summary>
    private const int GlideMilliseconds = 550;

    /// <summary>A gap of this many seconds or more shows the interlude dots.</summary>
    private const double InterludeSeconds = 4.0;

    private readonly ScrollViewer _scroll;
    private readonly StackPanel _lines;
    private readonly DispatcherQueueTimer _tween;
    private readonly DispatcherQueueTimer _frame;

    private readonly List<Row> _rows = [];
    private Row? _current;
    private double _offset;
    private bool _userScrolled;
    private bool _loaded;

    /// <summary>One line in the panel: its text, its runs, and the line itself.</summary>
    private sealed class Row
    {
        public required LyricLine Line { get; init; }
        public required TextBlock Text { get; init; }
        public TextBlock? Background { get; set; }
        /// <summary>The runs of a word-timed line, one per word.</summary>
        public List<Run> Runs { get; init; } = [];
        /// <summary>The three dots of an interlude, when the gap after this line is long.</summary>
        public List<Ellipse> Dots { get; init; } = [];
    }

    public LyricsPanel(ScrollViewer scroll, StackPanel lines, DispatcherQueue queue)
    {
        _scroll = scroll;
        _lines = lines;

        // The glide: the panel moves to the line being sung, never jumps.
        _tween = queue.CreateTimer();
        _tween.Interval = TimeSpan.FromMilliseconds(16);
        _tween.Tick += OnTweenTick;

        // The words: they fill while they are sung, so the panel keeps a frame timer.
        _frame = queue.CreateTimer();
        _frame.Interval = TimeSpan.FromMilliseconds(33);
        _frame.Tick += (_, _) => Paint(_current, Position());
    }

    /// <summary>The listener's correction for this song, in seconds.</summary>
    public double Offset
    {
        get => _offset;
        set => _offset = value;
    }

    public bool HasLyrics => _loaded && _rows.Count > 0;

    /// <summary>Whether the listener has scrolled away; the panel stops following then.</summary>
    public bool UserScrolled
    {
        get => _userScrolled;
        set => _userScrolled = value;
    }

    private double Position() => _position;

    private double _position;

    /// <summary>The song's place in time, with the listener's correction and the lead.</summary>
    public void SetPosition(double positionSeconds) => _position = positionSeconds + Lead - _offset;

    /// <summary>Fills the panel with the lyrics of a song.</summary>
    public void Load(Lyrics? lyrics)
    {
        _tween.Stop();
        _lines.Children.Clear();
        _rows.Clear();
        _current = null;
        _loaded = lyrics is { IsSynced: true };

        if (lyrics is null)
        {
            return;
        }

        if (!lyrics.IsSynced)
        {
            // Untimed lyrics are shown as they came: one block, no timing.
            var plain = new TextBlock
            {
                Text = lyrics.Plain,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 18,
                Foreground = Brush(0xD9),
            };
            _lines.Children.Add(plain);
            return;
        }

        for (var index = 0; index < lyrics.Lines.Count; index++)
        {
            var line = lyrics.Lines[index];
            var text = new TextBlock
            {
                // A word-timed line is written as runs later, so its text stays empty:
                // WinUI does not let a TextBlock carry both Text and Inlines.
                Text = line.IsWordTimed ? string.Empty : line.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 17,
                Foreground = Brush(0x59),
            };

            var row = new Row { Line = line, Text = text };
            text.Tapped += (_, _) =>
            {
                _userScrolled = false;
                Seek?.Invoke(line.Time);
            };

            if (line.IsWordTimed)
            {
                foreach (var word in line.Words)
                {
                    var run = new Run
                    {
                        Text = word.SpaceAfter ? word.Text + " " : word.Text,
                        Foreground = Brush(0x59),
                    };
                    text.Inlines.Add(run);
                    row.Runs.Add(run);
                }
            }

            var host = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
            host.Children.Add(text);
            if (line.Background.Count > 0)
            {
                row.Background = new TextBlock
                {
                    Text = string.Join(' ', line.Background.Select(w => w.Text)),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                    Foreground = Brush(0x40),
                };
                host.Children.Add(row.Background);
            }

            _lines.Children.Add(host);
            _rows.Add(row);

            // A long silence between two lines shows three dots that fill until the next.
            var next = index + 1 < lyrics.Lines.Count ? lyrics.Lines[index + 1] : null;
            if (next is not null && next.Time - line.Time >= InterludeSeconds)
            {
                var dots = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                for (var dot = 0; dot < 3; dot++)
                {
                    var ellipse = new Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Fill = Brush(0x33),
                    };
                    dots.Children.Add(ellipse);
                    row.Dots.Add(ellipse);
                }

                _lines.Children.Add(dots);
            }
        }
    }

    /// <summary>Called when a line is tapped: the song jumps to that line.</summary>
    public Action<double>? Seek { get; set; }

    /// <summary>Puts the panel on screen with a short fade and a small rise.</summary>
    public void PlayEntrance()
    {
        // The fade is the app's own pattern: a storyboard on the element's Opacity.
        // (A transform created in code cannot be a storyboard's target - only a
        // named one, declared in XAML, can.)
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, _lines);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        _lines.Opacity = 0;
        storyboard.Begin();
    }

    /// <summary>Follows the song: lights the line being sung and glides to it.</summary>
    public void Follow()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var time = _position;
        var index = Lyrics.IndexOf(time, _rows.Select(r => r.Line).ToList()) ?? -1;
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        var row = _rows[index];
        if (!ReferenceEquals(row, _current))
        {
            _current = row;
            Light(row);
            if (!_userScrolled)
            {
                GlideTo(row);
            }
        }

        Paint(row, time);
    }

    /// <summary>The line being sung is lit and lifted; the rest step back.</summary>
    private void Light(Row row)
    {
        foreach (var other in _rows)
        {
            var current = ReferenceEquals(other, row);
            other.Text.Foreground = current ? Brush(0xFF) : Brush(0x59);
            other.Text.FontSize = current ? 20 : 17;
            if (other.Background is not null)
            {
                other.Background.Foreground = current ? Brush(0x80) : Brush(0x40);
            }
        }

        // The line arrives with a small spring, the way the cover does.
        var arrival = new DoubleAnimation
        {
            From = 0.5,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 8, EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(arrival, row.Text);
        Storyboard.SetTargetProperty(arrival, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(arrival);
        storyboard.Begin();
    }

    /// <summary>The words of the line being sung, filling from their left edge.</summary>
    private void Paint(Row? row, double time)
    {
        if (row is null || row.Runs.Count == 0)
        {
            return;
        }

        var words = row.Line.Words;
        for (var index = 0; index < words.Count && index < row.Runs.Count; index++)
        {
            var word = words[index];
            var run = row.Runs[index];
            if (time >= word.End)
            {
                run.Foreground = Brush(0xCC); // sung
                continue;
            }

            if (time <= word.Start)
            {
                run.Foreground = Brush(0x59); // still to come
                continue;
            }

            // The word being sung: it fills from its left edge.
            var progress = (time - word.Start) / Math.Max(0.001, word.End - word.Start);
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = Brush(0xFF).Color, Offset = 0 },
                    new GradientStop { Color = Brush(0xFF).Color, Offset = Math.Clamp(progress, 0, 1) },
                    new GradientStop { Color = Brush(0x59).Color, Offset = Math.Clamp(progress, 0, 1) },
                },
            };
            run.Foreground = gradient;
        }

        // The interlude dots fill while the silence lasts.
        foreach (var other in _rows)
        {
            if (other.Dots.Count == 0)
            {
                continue;
            }

            var next = other.Line.Time + InterludeSeconds;
            var fill = Math.Clamp((time - other.Line.Time) / InterludeSeconds, 0, 1);
            for (var dot = 0; dot < other.Dots.Count; dot++)
            {
                var threshold = (double)(dot + 1) / other.Dots.Count;
                other.Dots[dot].Fill = Brush(fill >= threshold ? (byte)0x99 : (byte)0x33);
            }
        }
    }

    /// <summary>The glide to the line being sung: a spring, never a jump.</summary>
    private void GlideTo(Row row)
    {
        if (_rows.IndexOf(row) < 0)
        {
            return;
        }

        var index = _rows.IndexOf(row);
        var host = _lines.Children.Count > index ? _lines.Children[index] : null;
        if (host is null)
        {
            return;
        }

        double target;
        try
        {
            var offset = host.TransformToVisual(_lines).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            target = Math.Max(0, offset - _scroll.ViewportHeight * ReadingAnchor);
        }
        catch (Exception)
        {
            return; // the panel is not on screen yet
        }

        var start = _scroll.VerticalOffset;
        if (Math.Abs(target - start) < 1)
        {
            return;
        }

        _tween.Stop();
        _tweenStart = start;
        _tweenTarget = target;
        _tweenElapsed = 0;
        _tween.Start();
    }

    private double _tweenStart;
    private double _tweenTarget;
    private int _tweenElapsed;

    private void OnTweenTick(object? sender, object args)
    {
        _tweenElapsed += 16;
        var k = Math.Min(1.0, _tweenElapsed / (double)GlideMilliseconds);
        var eased = EaseOutBack(k);
        _scroll.ChangeView(null, _tweenStart + (_tweenTarget - _tweenStart) * eased, null);
        if (k >= 1)
        {
            _tween.Stop();
        }
    }

    /// <summary>An ease-out-back curve: it settles like a spring, without the wobble.</summary>
    private static double EaseOutBack(double k)
    {
        const double c1 = 1.2;
        const double c3 = c1 + 1;
        var x = k - 1;
        return 1 + c3 * x * x * x + c1 * x * x;
    }

    private static SolidColorBrush Brush(byte alpha) =>
        new(Windows.UI.Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));

    public void Dispose()
    {
        _tween.Stop();
        _frame.Stop();
    }
}

