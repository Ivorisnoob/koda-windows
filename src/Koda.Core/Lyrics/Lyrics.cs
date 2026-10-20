using System.Globalization;
using System.Text.RegularExpressions;

namespace Koda.Core;

/// <summary>One sung word, or part of one: providers split long words into syllables.</summary>
/// <param name="Text">The word as it is sung.</param>
/// <param name="Start">Seconds from the start of the song.</param>
/// <param name="End">Seconds from the start of the song.</param>
/// <param name="SpaceAfter">A space follows before the next word; false between syllables of one word.</param>
public readonly record struct LyricWord(string Text, double Start, double End, bool SpaceAfter = true)
{
    public double Duration => Math.Max(End - Start, 0.001);
}

/// <summary>Who sings a line, when the lyrics say.</summary>
public enum LyricVoice
{
    /// <summary>The main singer, or the only one.</summary>
    Lead,
    /// <summary>The second singer of a duet.</summary>
    Duet,
    /// <summary>Several singers at once.</summary>
    Together,
}

/// <summary>One line of lyrics, timed.</summary>
public sealed record LyricLine(
    int Id,
    double Time,
    string Text,
    IReadOnlyList<LyricWord> Words,
    IReadOnlyList<LyricWord> Background,
    double? End,
    LyricVoice Voice)
{
    public bool IsWordTimed => Words.Count > 0;
}

/// <summary>Lyrics: timed lines, or plain text with no timing at all.</summary>
public sealed class Lyrics
{
    /// <summary>Timed lines, when the provider had timings.</summary>
    public IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>Untimed text, when the provider had none.</summary>
    public string? Plain { get; }

    public bool IsSynced => Lines.Count > 0;

    private Lyrics(IReadOnlyList<LyricLine> lines, string? plain)
    {
        Lines = lines;
        Plain = plain;
    }

    public static Lyrics Synced(IReadOnlyList<LyricLine> lines) => new(lines, null);

    public static Lyrics Unsynced(string text) => new([], text);

    /// <summary>Word timing beats line timing beats none.</summary>
    public int Quality => Plain is not null ? 0 : Lines.Any(l => l.IsWordTimed) ? 2 : 1;

    /// <summary>The line playing at <paramref name="time"/>: the last one that has started.</summary>
    public static int? IndexOf(double time, IReadOnlyList<LyricLine> lines)
    {
        var low = 0;
        var high = lines.Count - 1;
        int? found = null;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (lines[mid].Time <= time)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    /// <summary>[mm:ss.xx] text lines, with &lt;mm:ss.xx&gt; word stamps when the file has them
    /// (enhanced LRC). Several line stamps repeat the line; lines without one are dropped.</summary>
    public static IReadOnlyList<LyricLine> ParseLrc(string text)
    {
        var stamp = new Regex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]");
        var wordStamp = new Regex(@"<(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)>");

        var lines = new List<(double Time, string Text, List<LyricWord> Words)>();
        foreach (var raw in text.Split('\n'))
        {
            var matches = stamp.Matches(raw);
            if (matches.Count == 0)
            {
                continue;
            }

            var last = matches[^1];
            var body = raw[(last.Index + last.Length)..];
            var wordMatches = wordStamp.Matches(body);
            var words = new List<LyricWord>();
            for (var index = 0; index < wordMatches.Count; index++)
            {
                var match = wordMatches[index];
                var textEnd = index + 1 < wordMatches.Count ? wordMatches[index + 1].Index : body.Length;
                var segment = body[(match.Index + match.Length)..textEnd];
                var trimmed = segment.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var start = Seconds(match.Groups[1].Value, match.Groups[2].Value);
                var end = index + 1 < wordMatches.Count
                    ? Seconds(wordMatches[index + 1].Groups[1].Value, wordMatches[index + 1].Groups[2].Value)
                    : start;
                words.Add(new LyricWord(trimmed, start, end, segment.EndsWith(' ')));
            }

            var display = wordStamp.Replace(body, "").Trim();
            for (var index = 0; index < matches.Count; index++)
            {
                // Word stamps belong to the first time the line is sung; repeats are line-timed.
                lines.Add((Seconds(matches[index].Groups[1].Value, matches[index].Groups[2].Value),
                    display,
                    index == 0 ? words : []));
            }
        }

        return Finish(lines
            .OrderBy(l => l.Time)
            .Select(l => new LyricLine(0, l.Time, l.Text, l.Words, [], null, LyricVoice.Lead))
            .ToList());
    }

    /// <summary>Number the lines in order, and fill in end times a provider left out:
    /// a word ends where the next starts, a line where the next line starts.</summary>
    internal static IReadOnlyList<LyricLine> Finish(IReadOnlyList<LyricLine> lines)
    {
        var sorted = lines.OrderBy(l => l.Time).ToList();
        var result = new List<LyricLine>(sorted.Count);
        for (var index = 0; index < sorted.Count; index++)
        {
            var line = sorted[index];
            var nextLine = index + 1 < sorted.Count ? sorted[index + 1].Time : (double?)null;
            var fallbackEnd = nextLine is { } next ? Math.Max(next, line.Time) : line.Time + 5;
            var words = Complete(line.Words, line.End ?? fallbackEnd);
            var background = Complete(line.Background, line.End ?? fallbackEnd);
            var end = line.End ?? (words.Concat(background).Select(w => w.End).DefaultIfEmpty(line.Time).Max());
            result.Add(line with { Id = index, Words = words, Background = background, End = end });
        }

        return result;
    }

    private static IReadOnlyList<LyricWord> Complete(IReadOnlyList<LyricWord> words, double lineEnd)
    {
        var sorted = words.OrderBy(w => w.Start).ToList();
        var result = new List<LyricWord>(sorted.Count);
        for (var index = 0; index < sorted.Count; index++)
        {
            var word = sorted[index];
            if (word.End > word.Start)
            {
                result.Add(word);
                continue;
            }

            var next = index + 1 < sorted.Count ? sorted[index + 1].Start : lineEnd;
            result.Add(word with { End = word.Start + Math.Min(Math.Max(next - word.Start, 0.08), 10) });
        }

        return result;
    }

    /// <summary>Lead and duet by alternating singers; a group agent sings together. One singer is always lead.</summary>
    internal static IReadOnlyList<LyricVoice> Voices(IReadOnlyList<string?> agents, IReadOnlySet<string> groups)
    {
        var people = new List<string>();
        foreach (var agent in agents)
        {
            if (agent is null || groups.Contains(agent) || people.Contains(agent))
            {
                continue;
            }

            people.Add(agent);
        }

        if (people.Count < 2)
        {
            return agents.Select(_ => LyricVoice.Lead).ToList();
        }

        return agents.Select(agent =>
        {
            if (agent is null)
            {
                return LyricVoice.Lead;
            }

            if (groups.Contains(agent))
            {
                return LyricVoice.Together;
            }

            return people.IndexOf(agent) % 2 == 1 ? LyricVoice.Duet : LyricVoice.Lead;
        }).ToList();
    }

    internal static double Seconds(string minutes, string rest)
    {
        var minutesValue = double.TryParse(minutes, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 0;
        var restValue = double.TryParse(rest.Replace(':', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0;
        return minutesValue * 60 + restValue;
    }
}

