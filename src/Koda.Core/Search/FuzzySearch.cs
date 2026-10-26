using System;
using System.Collections.Generic;

namespace Koda.Core;

/// <summary>A run of characters that matched, for highlighting in the UI.</summary>
public readonly record struct SearchRange(int Start, int Length);

/// <summary>How well a query matched a string, and which characters to light up.</summary>
public sealed record SearchMatch(double Score, IReadOnlyList<SearchRange> Ranges);

/// <summary>
/// The settings search. Every word of the query has to match; a word matches as a
/// substring when it can, and otherwise as a subsequence ("cfd" finds "Crossfade").
/// Matching is case-insensitive and returns the character ranges a page can highlight.
/// </summary>
public static class FuzzySearch
{
    /// <summary>Matches <paramref name="query"/> against <paramref name="text"/>.
    /// An empty query matches everything; a query with no match returns null.</summary>
    public static SearchMatch? Match(string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchMatch(0, []);
        }

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return new SearchMatch(0, []);
        }

        var ranges = new List<SearchRange>();
        var score = 0.0;
        foreach (var word in words)
        {
            var match = MatchWord(text, word);
            if (match is null)
            {
                return null;
            }

            score += match.Value.Score;
            ranges.AddRange(match.Value.Ranges);
        }

        return new SearchMatch(score, Merge(ranges));
    }

    private static WordMatch? MatchWord(string text, string word)
    {
        // A substring is the strongest match, and a word start beats the middle of a word.
        var first = -1;
        var wordStart = -1;
        for (var i = 0; i <= text.Length - word.Length; i++)
        {
            if (string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                continue;
            }

            first = first < 0 ? i : first;
            if (IsWordStart(text, i))
            {
                wordStart = i;
                break;
            }
        }

        var at = wordStart >= 0 ? wordStart : first;
        if (at >= 0)
        {
            var score = 100 + (word.Length * 10) - Math.Min(at, 20);
            if (IsWordStart(text, at))
            {
                score += 25;
            }

            return new WordMatch(score, [new SearchRange(at, word.Length)]);
        }

        return MatchSubsequence(text, word);
    }

    /// <summary>The letters in order, not necessarily together: "xl" finds "Lyrics",
    /// and "dsc" finds "Discord". Consecutive letters score higher than scattered ones.</summary>
    private static WordMatch? MatchSubsequence(string text, string word)
    {
        var ranges = new List<SearchRange>();
        var score = 0.0;
        var cursor = 0;
        var previous = -2;
        var first = -1;
        var last = -1;
        foreach (var wanted in word)
        {
            var found = -1;
            for (var i = cursor; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(wanted))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                return null;
            }

            first = first < 0 ? found : first;
            last = found;
            score += found == previous + 1 ? 12 : 4;
            if (IsWordStart(text, found))
            {
                score += 8;
            }

            Add(ranges, found);
            previous = found;
            cursor = found + 1;
        }

        // A tight match reads better than one scattered across the whole line.
        score += 30 - Math.Max(0, (last - first + 1) - word.Length);
        return new WordMatch(score, ranges);
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]);

    private static void Add(List<SearchRange> ranges, int index)
    {
        if (ranges.Count > 0 && ranges[^1].Start + ranges[^1].Length == index)
        {
            ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + 1 };
            return;
        }

        ranges.Add(new SearchRange(index, 1));
    }

    private static List<SearchRange> Merge(List<SearchRange> ranges)
    {
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<SearchRange>(ranges.Count);
        foreach (var range in ranges)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (range.Start <= last.Start + last.Length)
                {
                    var end = Math.Max(last.Start + last.Length, range.Start + range.Length);
                    merged[^1] = last with { Length = end - last.Start };
                    continue;
                }
            }

            merged.Add(range);
        }

        return merged;
    }

    private readonly record struct WordMatch(double Score, IReadOnlyList<SearchRange> Ranges);
}
