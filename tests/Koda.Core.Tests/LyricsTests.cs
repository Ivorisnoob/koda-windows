namespace Koda.Core.Tests;

/// <summary>
/// The lyrics parsers, against the same contract the sibling app's tests hold:
/// stamps, repeated lines, word timings, duets, and the shapes providers send.
/// </summary>
public class LyricsTests
{
    [Fact]
    public void ParsesStampsAndRepeatedLines()
    {
        var lines = Lyrics.ParseLrc("""
            [ar:Someone]
            [00:13.13] Yeah
            [00:27.16] I've been tryna call
            [01:02.50][02:10.00] Chorus
            not a lyric
            """);

        Assert.Equal(["Yeah", "I've been tryna call", "Chorus", "Chorus"], lines.Select(l => l.Text));
        Assert.Equal(13.13, lines[0].Time, 3);
        Assert.Equal(130, lines[3].Time, 3);
        Assert.Equal([0, 1, 2, 3], lines.Select(l => l.Id));
    }

    [Fact]
    public void FindsTheLineThatHasStarted()
    {
        var lines = Lyrics.ParseLrc("[00:10.00] a\n[00:20.00] b\n[00:30.00] c");

        Assert.Null(Lyrics.IndexOf(5, lines));
        Assert.Equal(0, Lyrics.IndexOf(10, lines));
        Assert.Equal(1, Lyrics.IndexOf(29.9, lines));
        Assert.Equal(2, Lyrics.IndexOf(500, lines));
    }

    [Fact]
    public void ReadsEnhancedLrc()
    {
        var lines = Lyrics.ParseLrc("[00:10.00]<00:10.00>Hello <00:10.50>world\n[00:20.00]Next");

        Assert.Equal("Hello world", lines[0].Text);
        Assert.Equal(["Hello", "world"], lines[0].Words.Select(w => w.Text));
        Assert.Equal(10.5, lines[0].Words[0].End, 3);
        Assert.Equal(20, lines[0].Words[1].End, 3);
        Assert.Empty(lines[1].Words);
    }

    [Fact]
    public void LineTimedLrcEndsWhereTheNextLineStarts()
    {
        // A line-timed line has no words to give it an end. Ending it where it starts
        // makes the panel read every gap as an instrumental, and hides the line.
        var lines = Lyrics.ParseLrc("[00:10.00] a\n[00:20.00] b");

        Assert.False(lines[0].IsWordTimed);
        Assert.Equal(20, lines[0].End ?? -1, 3);
        Assert.Null(lines[1].End);
    }

    [Fact]
    public void ReadsTtmlTimes()
    {
        Assert.Equal(27.395, LyricsFormats.TtmlTime("27.395")!.Value, 3);
        Assert.Equal(139.5, LyricsFormats.TtmlTime("2:19.5")!.Value, 3);
        Assert.Equal(3600, LyricsFormats.TtmlTime("1:00:00")!.Value, 3);
        Assert.Equal(0.5, LyricsFormats.TtmlTime("500ms")!.Value, 3);
        Assert.Equal(12, LyricsFormats.TtmlTime("12s")!.Value, 3);
        Assert.Null(LyricsFormats.TtmlTime("abc"));
    }

    [Fact]
    public void UnwrapsTtmlInsideJson()
    {
        var ttml = Fixtures.Text("lyrics-ttml.xml");
        var body = $"{{\"ttml\":{System.Text.Json.JsonSerializer.Serialize(ttml)}}}";

        var lyrics = LyricsClient.DecodeWrapped(System.Text.Encoding.UTF8.GetBytes(body));

        Assert.NotNull(lyrics);
        Assert.Equal(2, lyrics!.Quality);
    }

    [Fact]
    public void AlternatesDuetSingers()
    {
        const string ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><head><metadata>
            <ttm:agent type="person" xml:id="v1"/><ttm:agent type="person" xml:id="v2"/><ttm:agent type="group" xml:id="v1000"/>
            </metadata></head><body>
            <p begin="1.0" end="2.0" ttm:agent="v1">One</p>
            <p begin="3.0" end="4.0" ttm:agent="v2">Two</p>
            <p begin="5.0" end="6.0" ttm:agent="v1000">All</p>
            </body></tt>
            """;

        var lines = LyricsFormats.ParseTtml(ttml);

        Assert.NotNull(lines);
        Assert.Equal([LyricVoice.Lead, LyricVoice.Duet, LyricVoice.Together], lines!.Select(l => l.Voice));
        Assert.Equal(["One", "Two", "All"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.False(l.IsWordTimed));
    }

    /// <summary>Both services return the same Apple-timed lyrics for one song, in different shapes.</summary>
    private static void CheckWordTimed(IReadOnlyList<LyricLine> lines)
    {
        Assert.Equal(35, lines.Count);
        var first = lines[0];
        Assert.Equal(4, first.Words.Count);
        Assert.Equal(27.395, first.Words[0].Start, 3);
        Assert.Equal(27.549, first.Words[0].End, 3);
        Assert.True(first.Words[0].SpaceAfter); // a whole word, then the next one
        // The second line splits a word into two syllables: the first has no space after it.
        var second = lines[1];
        Assert.Equal(9, second.Words.Count);
        Assert.Contains(second.Words, word => !word.SpaceAfter);
        // Backing vocals stay out of the line and keep their own timing.
        var echoed = lines.First(l => l.Background.Count > 0);
        Assert.Equal(5, echoed.Background.Count);
        Assert.Equal(Enumerable.Range(0, 35), lines.Select(l => l.Id));
        Assert.All(lines, l => Assert.Equal(LyricVoice.Lead, l.Voice));
    }

    [Fact]
    public void ReadsAppleTtml()
    {
        var lines = LyricsFormats.ParseTtml(Fixtures.Text("lyrics-ttml.xml"));

        Assert.NotNull(lines);
        CheckWordTimed(lines!);
    }

    [Fact]
    public void ReadsYouLyPlusJson()
    {
        var lines = LyricsFormats.ParseYouLyPlus(Json.Parse(Fixtures.Text("lyrics-youlyplus.json")));

        Assert.NotNull(lines);
        CheckWordTimed(lines!);
    }
}

