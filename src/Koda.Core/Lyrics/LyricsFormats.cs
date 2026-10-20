using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace Koda.Core;

/// <summary>
/// The shapes lyrics arrive in: Apple's TTML, YouLyPlus's own JSON, LRC, or plain text.
/// Ported from the KodaCore LyricsFormats.swift contract: the same parsers, the same
/// reading of a file, the same answers.
/// </summary>
public static class LyricsFormats
{
    /// <summary>Apple-style TTML: &lt;p begin end ttm:agent&gt; lines of &lt;span begin end&gt; words, the
    /// whitespace between spans marking word breaks, and backing vocals in a
    /// &lt;span ttm:role="x-bg"&gt; of their own. Line-timed TTML (no word spans) works too.</summary>
    public static IReadOnlyList<LyricLine>? ParseTtml(string text)
    {
        var reader = new TtmlReader(text);
        var lines = reader.Lines;
        if (lines.Count == 0)
        {
            return null;
        }

        var voices = Lyrics.Voices(reader.Agents, reader.GroupAgents);
        var withVoices = new List<LyricLine>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            withVoices.Add(lines[index] with { Voice = voices[index] });
        }

        var finished = Lyrics.Finish(withVoices.Where(l => l.Text.Length > 0).ToList());
        return finished.Count == 0 ? null : finished;
    }

    /// <summary>YouLyPlus's own JSON: <c>lyrics[]</c> of {time, duration, text, syllabus[], element.singer},
    /// times in milliseconds.</summary>
    internal static IReadOnlyList<LyricLine>? ParseYouLyPlus(Json json)
    {
        var items = json["lyrics"].Array;
        if (items.Length == 0)
        {
            return null;
        }

        var groups = new HashSet<string>(StringComparer.Ordinal);
        var agents = json["metadata"]["agents"];
        foreach (var prop in agents.Raw.ValueKind == JsonValueKind.Object
            ? agents.Raw.EnumerateObject().ToList()
            : [])
        {
            var type = new Json(prop.Value)["type"].String;
            if (type?.ToLowerInvariant() == "group")
            {
                groups.Add(prop.Name);
            }
        }

        var lines = new List<LyricLine>();
        var singers = new List<string?>();
        foreach (var item in items)
        {
            var time = item["time"].Double;
            if (time is not { } milliseconds)
            {
                continue;
            }

            var words = new List<LyricWord>();
            var background = new List<LyricWord>();
            foreach (var syllable in item["syllabus"].Array)
            {
                var raw = syllable["text"].String;
                var start = syllable["time"].Double;
                if (raw is null || start is not { } syllableStart)
                {
                    continue;
                }

                var trimmed = raw.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var duration = syllable["duration"].Double ?? 0;
                var word = new LyricWord(trimmed, syllableStart / 1000, (syllableStart + duration) / 1000,
                    raw.EndsWith(' '));
                if (syllable["isBackground"].Bool == true)
                {
                    background.Add(word);
                }
                else
                {
                    words.Add(word);
                }
            }

            // A syllable that ends a line has no space after it to say so; the line break is one.
            if (words.Count > 0)
            {
                words[^1] = words[^1] with { SpaceAfter = true };
            }

            if (background.Count > 0)
            {
                background[^1] = background[^1] with { SpaceAfter = true };
            }

            var text = words.Count == 0 ? item["text"].NonEmpty ?? "" : Joined(words);
            if (text.Length == 0)
            {
                continue;
            }

            var lineDuration = item["duration"].Double ?? 0;
            lines.Add(new LyricLine(0, milliseconds / 1000, text, words, background,
                lineDuration > 0 ? (milliseconds + lineDuration) / 1000 : null, LyricVoice.Lead));
            singers.Add(item["element"]["singer"].String);
        }

        var voices = Lyrics.Voices(singers, groups);
        var withVoices = new List<LyricLine>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            withVoices.Add(lines[index] with { Voice = voices[index] });
        }

        var finished = Lyrics.Finish(withVoices);
        return finished.Count == 0 ? null : finished;
    }

    /// <summary>Whatever a provider sent, as lyrics: TTML, LRC (plain or enhanced), or plain text.</summary>
    public static Lyrics? ParseAny(string text)
    {
        var cleaned = text.Replace("\uFEFF", "").Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        if (cleaned.StartsWith('<') && (cleaned.Contains("<tt") || cleaned.Contains("www.w3.org/ns/ttml")))
        {
            var lines = ParseTtml(cleaned);
            return lines is null ? null : Lyrics.Synced(lines);
        }

        if (cleaned.StartsWith('<') || cleaned.StartsWith('{'))
        {
            return null;
        }

        var synced = Lyrics.ParseLrc(cleaned);
        return synced.Count > 0 ? Lyrics.Synced(synced) : Lyrics.Unsynced(cleaned);
    }

    public static string Joined(IReadOnlyList<LyricWord> words)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            builder.Append(word.Text);
            if (word.SpaceAfter)
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>27.395, 2:19.610, 1:02:03.5, 12.3s, 500ms, 1.5m; null for anything else.</summary>
    internal static double? TtmlTime(string value)
    {
        var time = value.Trim();
        if (time.EndsWith("ms", StringComparison.Ordinal))
        {
            return Number(time[..^2]) / 1000;
        }

        if (time.EndsWith('s'))
        {
            return Number(time[..^1]);
        }

        if (time.EndsWith('m'))
        {
            return Number(time[..^1]) * 60;
        }

        if (time.EndsWith('h'))
        {
            return Number(time[..^1]) * 3600;
        }

        var parts = time.Split(':');
        var numbers = new List<double>(parts.Length);
        foreach (var part in parts)
        {
            if (Number(part) is not { } number)
            {
                return null;
            }

            numbers.Add(number);
        }

        return numbers.Count switch
        {
            1 => numbers[0],
            2 => numbers[0] * 60 + numbers[1],
            3 => numbers[0] * 3600 + numbers[1] * 60 + numbers[2],
            _ => null,
        };
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>Walks a TTML document into lines. Names are matched without their prefix, so
/// <c>tt:p</c> and <c>p</c> read alike.</summary>
internal sealed class TtmlReader
{
    private sealed class Span
    {
        public double? Begin { get; set; }
        public double? End { get; set; }
        public bool IsBackground { get; set; }
        public string Text { get; set; } = "";
    }

    public List<LyricLine> Lines { get; } = [];
    public List<string?> Agents { get; } = [];
    public HashSet<string> GroupAgents { get; } = new(StringComparer.Ordinal);

    private bool _inLine;
    private double _lineStart;
    private double? _lineEnd;
    private string? _lineAgent;
    private string _lineText = "";
    private readonly List<Span> _spans = [];
    private List<LyricWord> _words = [];
    private List<LyricWord> _background = [];

    public TtmlReader(string text)
    {
        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = false,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        try
        {
            using var reader = XmlReader.Create(new System.IO.StringReader(text), settings);
            Read(reader);
        }
        catch (XmlException)
        {
            // A broken file gives what was read so far; the caller decides.
        }
    }

    private void Read(XmlReader reader)
    {
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    Start(reader);
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    Characters(reader.Value);
                    break;
                case XmlNodeType.EndElement:
                    End(Local(reader.Name));
                    break;
            }
        }
    }

    private void Start(XmlReader reader)
    {
        switch (Local(reader.Name))
        {
            case "agent":
                if (Attribute(reader, "type")?.ToLowerInvariant() == "group" &&
                    Attribute(reader, "id") is { } id)
                {
                    GroupAgents.Add(id);
                }

                break;
            case "p":
            {
                var (begin, end) = Times(reader, null);
                if (begin is not { } start)
                {
                    return;
                }

                _inLine = true;
                _lineStart = start;
                _lineEnd = end;
                _lineAgent = Attribute(reader, "agent");
                _lineText = "";
                _spans.Clear();
                _words = [];
                _background = [];
                break;
            }

            case "span" when _inLine:
            {
                var (begin, end) = Times(reader, _spans.Count > 0 ? _spans[^1].Begin : _lineStart);
                var isBackground = Attribute(reader, "role") == "x-bg" ||
                                   (_spans.Count > 0 && _spans[^1].IsBackground);
                _spans.Add(new Span { Begin = begin, End = end, IsBackground = isBackground });
                break;
            }
        }
    }

    private void Characters(string text)
    {
        if (!_inLine)
        {
            return;
        }

        if (_spans.Count > 0 && _spans[^1].Begin is not null)
        {
            _spans[^1].Text += text;
            return;
        }

        if (text.Length > 0 && text.All(char.IsWhiteSpace))
        {
            // Whitespace between word spans is the only sign that one word ended and the next began.
            var inBackground = _spans.Count > 0 && _spans[^1].IsBackground;
            if (inBackground && _background.Count > 0)
            {
                _background[^1] = _background[^1] with { SpaceAfter = true };
            }
            else if (!inBackground && _words.Count > 0)
            {
                _words[^1] = _words[^1] with { SpaceAfter = true };
            }

            _lineText += " ";
            return;
        }

        _lineText += text;
    }

    private void End(string name)
    {
        switch (name)
        {
            case "span" when _inLine:
            {
                if (_spans.Count == 0)
                {
                    return;
                }

                var span = _spans[^1];
                _spans.RemoveAt(_spans.Count - 1);
                if (span.Begin is not { } begin)
                {
                    return;
                }

                var trimmed = span.Text.Trim();
                if (trimmed.Length == 0)
                {
                    return;
                }

                // Some files time words from the start of their line rather than of the song.
                var lineLength = (_lineEnd ?? _lineStart + 30) - _lineStart;
                var relative = begin < _lineStart - 0.25 && begin <= lineLength + 1;
                var start = relative ? _lineStart + begin : begin;
                var end = span.End is { } spanEnd ? (relative ? _lineStart + spanEnd : spanEnd) : start;
                var word = new LyricWord(trimmed, start, end, span.Text.EndsWith(' '));
                if (span.IsBackground)
                {
                    _background.Add(word);
                }
                else
                {
                    _words.Add(word);
                }

                break;
            }

            case "p" when _inLine:
            {
                _inLine = false;
                if (_words.Count > 0)
                {
                    _words[^1] = _words[^1] with { SpaceAfter = true };
                }

                if (_background.Count > 0)
                {
                    _background[^1] = _background[^1] with { SpaceAfter = true };
                }

                var text = _words.Count == 0
                    ? string.Join(' ', _lineText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    : LyricsFormats.Joined(_words);
                Lines.Add(new LyricLine(0, _lineStart, text, _words, _background, _lineEnd, LyricVoice.Lead));
                Agents.Add(_lineAgent);
                break;
            }
        }
    }

    private static string Local(string name)
    {
        var index = name.LastIndexOf(':');
        return index < 0 ? name : name[(index + 1)..];
    }

    private static string? Attribute(XmlReader reader, string suffix)
    {
        string? found = null;
        for (var index = 0; index < reader.AttributeCount; index++)
        {
            reader.MoveToAttribute(index);
            if (found is null && Local(reader.Name) == suffix)
            {
                found = reader.Value;
            }
        }

        reader.MoveToElement();
        return found;
    }

    private static (double? Begin, double? End) Times(XmlReader reader, double? start)
    {
        var begin = Attribute(reader, "begin") is { } beginText ? LyricsFormats.TtmlTime(beginText) : null;
        var end = Attribute(reader, "end") is { } endText ? LyricsFormats.TtmlTime(endText) : null;
        if (end is null && Attribute(reader, "dur") is { } durText && LyricsFormats.TtmlTime(durText) is { } dur)
        {
            end = (begin ?? start) + dur;
        }

        return (begin, end);
    }
}


