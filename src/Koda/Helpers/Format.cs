using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Koda.Core;

namespace Koda.Helpers;

/// <summary>
/// The words the Mac app uses for figures and days, so both apps say the same
/// thing: "2 hr 5 min", "42 min", "1 song", "Yesterday", "3:41 PM".
/// </summary>
public static class Format
{
    /// <summary>"2 hr 5 min", "42 min", "Under a minute", "0 min".</summary>
    public static string Listening(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0)
        {
            return "0 min";
        }

        var minutes = (int)(seconds / 60);
        if (minutes == 0)
        {
            return "Under a minute";
        }

        if (minutes < 60)
        {
            return $"{minutes} min";
        }

        return minutes % 60 == 0 ? $"{minutes / 60} hr" : $"{minutes / 60} hr {minutes % 60} min";
    }

    /// <summary>"1 song" / "42 songs".</summary>
    public static string Songs(int count) => count == 1 ? "1 song" : $"{Count(count)} songs";

    /// <summary>"1 play" / "42 plays".</summary>
    public static string Plays(int count) => count == 1 ? "1 play" : $"{Count(count)} plays";

    /// <summary>"42 songs · 2 hr 31 min", under a collection's title.</summary>
    public static string SongList(IEnumerable<Song> songs)
    {
        var list = songs as IReadOnlyCollection<Song> ?? songs.ToArray();
        var parts = new List<string> { Songs(list.Count) };
        var minutes = (int)(list.Sum(s => s.Duration ?? 0) / 60);
        if (minutes > 0)
        {
            parts.Add(minutes >= 60 ? $"{minutes / 60} hr {minutes % 60} min" : $"{minutes} min");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>A headline figure with the current culture's group separators.</summary>
    public static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>"Today", "Yesterday", "Saturday 18 October", "18 October 2025".</summary>
    public static string Day(DateTimeOffset date)
    {
        var local = date.ToLocalTime().Date;
        var today = DateTime.Today;
        if (local == today)
        {
            return "Today";
        }

        if (local == today.AddDays(-1))
        {
            return "Yesterday";
        }

        var culture = CultureInfo.CurrentCulture;
        return local.Year == today.Year
            ? local.ToString("dddd d MMMM", culture)
            : local.ToString("d MMMM yyyy", culture);
    }

    /// <summary>"3:41 PM".</summary>
    public static string Time(DateTimeOffset date) => date.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    /// <summary>"1 day" / "12 days", the Stats tile.</summary>
    public static string Days(int count) => count == 1 ? "1 day" : $"{count} days";

    /// <summary>"1-day streak" / "12-day streak" / "Play something to start a streak".</summary>
    public static string StreakLine(int streak) => streak switch
    {
        0 => "Play something to start a streak",
        1 => "1-day streak",
        _ => $"{streak}-day streak",
    };

    /// <summary>The chart's y-axis label for a bar count.</summary>
    public static string Minutes(double seconds) =>
        seconds <= 0 ? "0m" : $"{(int)Math.Round(seconds / 60)}m";
}
