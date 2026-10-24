using System;
using System.Collections.Generic;
using Koda.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Koda.Pages;

/// <summary>
/// The lyrics panel: the lines, the one being sung lit, and the words inside it filling
/// from their left edge as they are sung.
///
/// The motion is the Mac's, word for word. A word fills through a soft-edged mask that
/// runs from its left edge, rises a touch as it starts (so a line moves as a ripple),
/// and a note held long swells and glows. The lines around the one being sung step
/// back: dimmer, a little smaller. The panel glides to the line being sung with a
/// spring - it lands a third of the way down, so the listener reads ahead.
///
/// The listener's own correction for a song (the offset the Library keeps) is honoured,
/// and the audio leads the lyrics by a fraction of a second, so a line lights as it is
/// sung rather than after.
/// </summary>
internal sealed class LyricsPanel : IDisposable
{
    /// <summary>The audio leads the lyrics: a line lights as it is sung, not after.</summary>
    private const double Lead = 0.15;

    /// <summary>Where the line being sung sits in the panel: inside the top third, so the
    /// eye barely travels and the lines that follow are already in view.</summary>
    private const double ReadingAnchor = 0.25;

    /// <summary>How long the glide takes.</summary>
    private const int GlideMilliseconds = 550;

    /// <summary>A gap of this many seconds or more shows the interlude dots.</summary>
    private const double InterludeSeconds = 4.0;

    /// <summary>How long a word takes to rise, in seconds.</summary>
    private const double RiseSeconds = 0.65;

    /// <summary>How far a word rises, in pixels.</summary>
    private const double RisePixels = 2.5;

    /// <summary>A note held longer than this starts to swell.</summary>
    private const double SustainStart = 0.9;

    /// <summary>The soft front of the filling mask, as a part of the word's width.</summary>
    private const double Feather = 0.35;

    /// <summary>How far a swelling note grows.</summary>
    private const double SwellScale = 0.045;

    /// <summary>How wide the words sit from each other, as a part of the text size.</summary>
    private const double WordSpacing = 0.25;

    /// <summary>A little tracking, so the lines breathe rather than read thin, as the Mac's do.</summary>
    private const int Tracking = 20;

    /// <summary>The line height, as a part of the text size: Apple's lyrics are airy.</summary>
    private const double LineHeightFactor = 1.35;

    /// <summary>The Text optical size of Segoe UI Variable: wider letterforms at reading sizes
    /// than the Display cut, which is what made the lyrics look thin.</summary>
    private static readonly FontFamily LyricFont = new("Segoe UI Variable Text");

    private readonly ScrollViewer _scroll;
    private readonly StackPanel _lines;
    private readonly Border _spacer = new();
    private readonly DispatcherQueueTimer _tween;
    private readonly DispatcherQueueTimer _frame;

    private readonly List<Row> _rows = [];
    private readonly List<LyricLine> _lyricLines = [];
    private StackPanel? _leadingDots;
    private List<Ellipse> _leadingDotItems = [];
    private Row? _current;
    private double _offset;
    private bool _userScrolled;
    private bool _loaded;
    private bool _needsGlide;
    private double _position;
    private double _panelWidth;

    /// <summary>One word in the panel: the element that draws it and the timings behind it.</summary>
    private sealed class Word
    {
        public required LyricWord Data { get; init; }
        public required TextBlock Element { get; init; }
        /// <summary>The mask the word fills through.</summary>
        public required LinearGradientBrush Fill { get; init; }
        /// <summary>The rise and the swell of the word.</summary>
        public required CompositeTransform Transform { get; init; }
    }

    /// <summary>One line in the panel: its words, its backing vocals and the line itself.</summary>
    private sealed class Row
    {
        public required LyricLine Line { get; init; }
        /// <summary>The element that holds the line, for finding its place in the panel.</summary>
        public FrameworkElement Host { get; set; } = null!;
        public required StackPanel Words { get; init; }
        public required StackPanel Background { get; init; }
        /// <summary>The row's scale, kept apart from a word's own swell.</summary>
        public CompositeTransform? RowTransform { get; set; }
        public List<Word> WordItems { get; } = [];
        public List<Word> BackgroundItems { get; } = [];
        /// <summary>The three dots of the gap that follows this line, when the gap is long.</summary>
        public List<Ellipse> Dots { get; } = [];
        /// <summary>The dots' container; shown only while that gap is playing.</summary>
        public StackPanel? DotsHost { get; set; }
        /// <summary>How opaque the row is: the one being sung is full, the rest step back.</summary>
        public double Opacity { get; set; } = 0.36;
        /// <summary>How large the row is: the one being sung is full size.</summary>
        public double Scale { get; set; } = 0.99;
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
        // The position is read live, every frame: the app's own tick only comes
        // twice a second, and a word that fills in half-second steps reads as a
        // sequence of keyframes, not as a word being sung.
        _frame = queue.CreateTimer();
        _frame.Interval = TimeSpan.FromMilliseconds(16);
        _frame.Tick += (_, _) =>
        {
            if (PositionSource is { } source)
            {
                SetPosition(source());
            }

            // Following the song here, not on the app's twice-a-second tick: the line
            // being sung changes the moment the song reaches it.
            Follow();
        };

        // The words are laid out in rows that fit the panel, so a line breaks where it should.
        _scroll.SizeChanged += (_, args) =>
        {
            if (Math.Abs(args.NewSize.Width - _panelWidth) < 1)
            {
                return;
            }

            _panelWidth = args.NewSize.Width;
            Reflow();

            // The panel now knows how tall it is: put the line being sung where it belongs.
            if (_current is not null)
            {
                GlideTo(_current);
            }
        };
    }

    /// <summary>A line's right-click menu asked to share it: the lines, and the one it was opened on.</summary>
    public Action<IReadOnlyList<LyricLine>, int>? Share { get; set; }

    /// <summary>The timed lines on show, for sharing from elsewhere (the player's More menu).</summary>
    public IReadOnlyList<LyricLine> Lines => _lyricLines;

    /// <summary>The line being sung, or the first one before it starts.</summary>
    public int CurrentIndex => Math.Max(0, Lyrics.IndexOf(_position, _lyricLines) ?? 0);

    /// <summary>Called when a line is tapped: the song jumps to that line.</summary>
    public Action<double>? Seek { get; set; }

    /// <summary>Where the song is, read live: the panel asks every frame.</summary>
    public Func<double>? PositionSource { get; set; }

    /// <summary>The listener's correction for this song, in seconds.</summary>
    public double Offset
    {
        get => _offset;
        set => _offset = value;
    }

    /// <summary>Whether the listener has scrolled away; the panel stops following then.</summary>
    public bool UserScrolled
    {
        get => _userScrolled;
        set => _userScrolled = value;
    }

    /// <summary>The song's place in time, with the listener's correction and the lead.</summary>
    public void SetPosition(double positionSeconds) => _position = positionSeconds + Lead - _offset;

    /// <summary>Fills the panel with the lyrics of a song.</summary>
    public void Load(Lyrics? lyrics)
    {
        _tween.Stop();
        _lines.Children.Clear();
        _rows.Clear();
        _lyricLines.Clear();
        _leadingDots = null;
        _leadingDotItems = [];
        _current = null;
        _loaded = lyrics is { IsSynced: true };

        if (lyrics is null)
        {
            return;
        }

        if (!lyrics.IsSynced)
        {
            // Untimed lyrics are shown as they came: one block, no timing.
            var size = Koda.Core.Settings.Current.LyricsFontSize;
            var plain = new TextBlock
            {
                Text = lyrics.Plain,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(0xD9),
            };
            Shape(plain, size);
            _lines.Children.Add(plain);
            return;
        }

        // A long wait before the first line gets its dots too.
        if (lyrics.Lines.Count > 0 && lyrics.Lines[0].Time >= InterludeSeconds)
        {
            _leadingDotItems = [];
            _leadingDots = MakeDots(_leadingDotItems);
            _lines.Children.Add(_leadingDots);
        }

        for (var index = 0; index < lyrics.Lines.Count; index++)
        {
            var line = lyrics.Lines[index];
            var settings = Koda.Core.Settings.Current;
            var fontSize = settings.LyricsFontSize;
            var row = new Row
            {
                Line = line,
                Words = new StackPanel { Spacing = 4 },
                Background = new StackPanel { Spacing = 2, Opacity = 0.7 },
            };

            var host = new StackPanel { Margin = new Thickness(0, 8, 0, 8), Spacing = 6 };
            // The Mac's sides: a duet sings from the right when Duet Sides is on, and a
            // group sings together in the middle. Everything else sits on the left.
            var side = settings.DuetSides && line.Voice == LyricVoice.Duet
                ? HorizontalAlignment.Right
                : line.Voice == LyricVoice.Together ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            host.HorizontalAlignment = side;
            row.Words.HorizontalAlignment = side;
            row.Background.HorizontalAlignment = side;
            var textSide = side == HorizontalAlignment.Right
                ? TextAlignment.Right
                : side == HorizontalAlignment.Center ? TextAlignment.Center : TextAlignment.Left;

            host.Children.Add(row.Words);

            row.Host = host;

            // A tap on the line jumps the song to it, and hands the panel back to the song.
            host.Tapped += (_, _) =>
            {
                _userScrolled = false;
                Seek?.Invoke(line.Time);
            };

            if (line.Words.Count == 0)
            {
                // Line-timed lyrics: there are no words to fill, so the line is set as text
                // and lights as a whole when the song reaches it. Without this the row was
                // empty, which is how a song could show nothing but the interlude dots.
                var transform = new CompositeTransform();
                var lineText = new TextBlock
                {
                    Text = line.Text.Length == 0 ? "♪" : line.Text,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = textSide,
                    Foreground = Brush(0xFF),
                    RenderTransform = transform,
                };
                Shape(lineText, fontSize);
                row.RowTransform = transform;
                row.Words.Children.Add(lineText);
            }
            else
            {
                foreach (var word in line.Words)
                {
                    var item = MakeWord(word, fontSize);
                    row.WordItems.Add(item);
                }
            }

            foreach (var word in line.Background)
            {
                if (!settings.ShowsBackingVocals)
                {
                    break; // the listener turned the backing vocals off
                }

                // Backing vocals are drawn at 70% of the sung line, a little lighter.
                var item = MakeWord(word, fontSize * 0.7, soft: true);
                row.BackgroundItems.Add(item);
            }

            if (row.BackgroundItems.Count > 0)
            {
                host.Children.Add(row.Background);
            }

            _lines.Children.Add(host);
            _rows.Add(row);
            _lyricLines.Add(line);

            // A long silence after this line shows three dots that fill until the next one.
            // The gap runs from the line's end, as the Mac measures it: measuring from the
            // line's start put dots between ordinary consecutive lines.
            var next = index + 1 < lyrics.Lines.Count ? lyrics.Lines[index + 1] : null;
            if (next is not null && line.End is { } lineEnd && next.Time - lineEnd >= InterludeSeconds)
            {
                row.DotsHost = MakeDots(row.Dots);
                _lines.Children.Add(row.DotsHost);
            }
        }

        // The room below the last line, so the final lines of a song can still be scrolled
        // up to the reading position.
        _lines.Children.Add(_spacer);

        Reflow();
    }

    /// <summary>Three dots that fill in turn through an instrumental gap.</summary>
    private static StackPanel MakeDots(List<Ellipse> dots)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 4, 0, 4),
            Visibility = Visibility.Collapsed,
        };
        for (var dot = 0; dot < 3; dot++)
        {
            var ellipse = new Ellipse { Width = 6, Height = 6, Fill = Brush(0x4D) };
            host.Children.Add(ellipse);
            dots.Add(ellipse);
        }

        return host;
    }

    /// <summary>One word: its element, the mask it fills through, and its rise.</summary>
    private Word MakeWord(LyricWord word, double fontSize, bool soft = false)
    {
        var fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = Brush(0xFF).Color, Offset = 0 },
                new GradientStop { Color = Brush(0xFF).Color, Offset = 0 },
                new GradientStop { Color = Brush(0x61).Color, Offset = 0.0001 },
            },
        };

        var transform = new CompositeTransform();
        var element = new TextBlock
        {
            // The space between words is a margin, not a space in the text: a trailing
            // space in a text block does not draw, and two syllables of one word must
            // sit together.
            Text = word.Text,
            Margin = new Thickness(0, 0, word.SpaceAfter ? fontSize * WordSpacing : 0, 0),
            Foreground = fill,
            RenderTransform = transform,
            TextWrapping = TextWrapping.NoWrap,
        };
        Shape(element, fontSize, soft);

        element.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        return new Word
        {
            Data = word,
            Element = element,
            Fill = fill,
            Transform = transform,
        };
    }

    /// <summary>
    /// The words of a line are set left to right and wrapped like text, so a line breaks
    /// the same way whether it is being sung or resting.
    /// </summary>
    private void Reflow()
    {
        var width = _panelWidth - 24;
        if (width <= 0)
        {
            return;
        }

        foreach (var row in _rows)
        {
            // A line-timed row holds its line text directly; only word rows are laid out.
            if (row.WordItems.Count > 0)
            {
                LayOut(row.Words, row.WordItems, width);
            }

            if (row.BackgroundItems.Count > 0)
            {
                LayOut(row.Background, row.BackgroundItems, width);
            }
        }

        // Room below the last line, so the final lines of a song can still be scrolled up
        // to the reading position. Without it the scroll stops at the end and the line being
        // sung drifts down out of the top third, which is exactly where it should not be.
        _spacer.Height = Math.Max(0, _scroll.ViewportHeight);
    }

    private void LayOut(StackPanel panel, List<Word> words, double width)
    {
        panel.Children.Clear();
        StackPanel? line = null;
        var used = 0.0;
        foreach (var word in words)
        {
            var w = word.Element.DesiredSize.Width + word.Element.Margin.Right;
            if (line is null || used + w > width)
            {
                line = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(line);
                used = 0;
            }

            line.Children.Add(word.Element);
            used += w;
        }
    }

    /// <summary>Puts the panel on screen with a short fade, and starts it following the song.</summary>
    public void PlayEntrance()
    {
        _frame.Start();

        // The panel has just come into view: the line being sung is placed now that the
        // panel knows its height (before that, there was nothing to measure against).
        if (_current is not null)
        {
            GlideTo(_current);
        }

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

    /// <summary>The panel is closed: it stops reading the song.</summary>
    public void Pause() => _frame.Stop();

    /// <summary>Follows the song: lights the line being sung and glides to it.</summary>
    public void Follow()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var index = Lyrics.IndexOf(_position, _lyricLines) ?? -1;
        if (index < 0)
        {
            // Before the first line (the intro): still paint, so its dots fill.
            Paint();
            return;
        }

        if (index >= _rows.Count)
        {
            return;
        }

        var row = _rows[index];
        if (!ReferenceEquals(row, _current))
        {
            _current = row;
            foreach (var other in _rows)
            {
                var distance = Math.Abs(_rows.IndexOf(other) - index);
                other.Opacity = other == row ? 1 : distance <= 1 ? 0.5 : 0.36;
                other.Scale = other == row ? 1 : 0.99;
            }

            if (!_userScrolled)
            {
                GlideTo(row);
            }
        }

        Paint();
    }

    /// <summary>
    /// The words of the line being sung, redrawn every frame: the mask runs across the
    /// word, the word rises as it starts, a note held long swells and glows.
    /// </summary>
    private void Paint()
    {
        if (_needsGlide && _current is not null && _scroll.ViewportHeight > 1)
        {
            _needsGlide = false;
            GlideTo(_current);
        }

        var time = _position;
        PaintInterlude(time);

        foreach (var row in _rows)
        {
            // The rows step back into place with a spring of their own.
            row.Words.Opacity = Approach(row.Words.Opacity, row.Opacity);
            if (row.RowTransform is { } transform)
            {
                // A line-timed row has no words to scale one by one, so its whole line does it.
                transform.ScaleX = Approach(transform.ScaleX, row.Scale);
                transform.ScaleY = transform.ScaleX;
            }

            foreach (var word in row.WordItems)
            {
                word.Transform.ScaleX = Approach(word.Transform.ScaleX, row.Scale);
                word.Transform.ScaleY = word.Transform.ScaleX;
            }
        }

        var current = _current;
        if (current is null)
        {
            return;
        }

        var motion = Koda.Core.Settings.Current.LyricsMotion;
        foreach (var word in current.WordItems)
        {
            Paint(word, time, motion);
        }

        foreach (var word in current.BackgroundItems)
        {
            Paint(word, time, motion);
        }
    }

    /// <summary>
    /// The three dots of a gap: before the first line, or after the line that just ended,
    /// while the gap lasts. Only one gap is ever on screen, as on the Mac. The gap is
    /// measured from the line's end; measuring from its start put dots after every line.
    /// </summary>
    private void PaintInterlude(double time)
    {
        Row? active = null;
        var leading = false;
        var progress = 0.0;

        var current = Lyrics.IndexOf(time, _lyricLines);
        if (current is null)
        {
            if (_lyricLines.Count > 0 && _lyricLines[0].Time >= InterludeSeconds && time >= 0)
            {
                leading = true;
                progress = Math.Clamp(time / _lyricLines[0].Time, 0, 1);
            }
        }
        else
        {
            var index = current.Value;
            if (index + 1 < _lyricLines.Count &&
                _lyricLines[index].End is { } end &&
                _lyricLines[index + 1].Time - end >= InterludeSeconds &&
                time > end + 0.3)
            {
                active = _rows[index];
                progress = Math.Clamp((time - end) / (_lyricLines[index + 1].Time - end), 0, 1);
                // The line just sung rests while the gap plays, as it does on the Mac.
                active.Opacity = 0.5;
                active.Scale = 0.99;
            }
        }

        foreach (var row in _rows)
        {
            if (row.DotsHost is null || ReferenceEquals(row, active))
            {
                continue;
            }

            row.DotsHost.Visibility = Visibility.Collapsed;
        }

        if (active?.DotsHost is { } host)
        {
            host.Visibility = Visibility.Visible;
            FillDots(active.Dots, progress);
        }
        else if (current is { } index && index < _rows.Count)
        {
            // Coming back out of a gap (a seek, a replay) lights the line again.
            _rows[index].Opacity = 1;
            _rows[index].Scale = 1;
        }

        if (_leadingDots is not null)
        {
            _leadingDots.Visibility = leading ? Visibility.Visible : Visibility.Collapsed;
            if (leading)
            {
                FillDots(_leadingDotItems, progress);
            }
        }
    }

    private static void FillDots(IReadOnlyList<Ellipse> dots, double progress)
    {
        for (var dot = 0; dot < dots.Count; dot++)
        {
            var fill = Math.Clamp(progress * dots.Count - dot, 0, 1);
            var alpha = (byte)(0x4D + (0xFF - 0x4D) * fill);
            dots[dot].Fill = Brush(alpha);
        }
    }

    /// <summary>One word: how much of it is sung, how far it has risen, how much it glows.</summary>
    private void Paint(Word word, double time, bool motion)
    {
        var data = word.Data;
        var fill = Math.Clamp((time - data.Start) / Math.Max(0.001, data.End - data.Start), 0, 1);
        var rise = motion ? Math.Clamp((time - data.Start) / RiseSeconds, 0, 1) : 0;
        var eased = 1 - Math.Pow(1 - rise, 3); // quick at first, settling gently
        var sustainStrength = motion ? Math.Clamp((data.End - data.Start - SustainStart) / SustainStart, 0, 1) : 0;
        var sustain = sustainStrength * Math.Sin(Math.PI * fill);

        // The mask runs from -feather to 1, so the word is clear at the start and full at the end.
        var front = fill * (1 + Feather);
        var stops = word.Fill.GradientStops;
        stops[0].Offset = 0;
        stops[1].Offset = Math.Max(0, front - Feather);
        stops[1].Color = Brush(0xFF).Color;
        stops[2].Offset = Math.Min(1, Math.Max(front, 0.0001));
        stops[2].Color = Brush(0x61).Color;

        // The word rises as it is sung: a ripple through the line.
        word.Transform.TranslateY = -RisePixels * eased;

        // A note held long swells.
        var swell = 1 + SwellScale * sustain;
        word.Transform.ScaleX = swell;
        word.Transform.ScaleY = swell;
    }

    /// <summary>A step toward a value, with the softness of a spring.</summary>
    private static double Approach(double current, double target)
    {
        var step = (target - current) * 0.18;
        if (Math.Abs(step) < 0.001)
        {
            return target;
        }

        return current + step;
    }

    /// <summary>The glide to the line being sung: a spring, never a jump.</summary>
    private void GlideTo(Row row)
    {
        if (_scroll.ViewportHeight <= 1)
        {
            // The panel has no height yet (it is coming into view): try again in a frame.
            _needsGlide = true;
            return;
        }

        double target;
        try
        {
            var offset = row.Host.TransformToVisual(_lines).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
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

    /// <summary>The lyric type: the Text optical size, semibold, a little tracked out and airy.
    /// The backing vocals are the same shape in a lighter weight.</summary>
    private static void Shape(TextBlock block, double fontSize, bool soft = false)
    {
        block.FontFamily = LyricFont;
        block.FontWeight = soft
            ? Microsoft.UI.Text.FontWeights.Medium
            : Microsoft.UI.Text.FontWeights.SemiBold;
        block.FontSize = fontSize;
        block.CharacterSpacing = Tracking;
        block.LineHeight = Math.Round(fontSize * LineHeightFactor);
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    public void Dispose()
    {
        _tween.Stop();
        _frame.Stop();
    }
}

