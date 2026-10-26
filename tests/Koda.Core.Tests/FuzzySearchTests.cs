namespace Koda.Core.Tests;

/// <summary>
/// The settings search: a word is found as a substring when it can be, as a
/// subsequence when it cannot, and every word of a query has to land somewhere.
/// </summary>
public class FuzzySearchTests
{
    [Fact]
    public void EmptyQueryMatchesEverything()
    {
        var match = FuzzySearch.Match("Lyrics", "  ");

        Assert.NotNull(match);
        Assert.Equal(0, match.Score);
        Assert.Empty(match.Ranges);
    }

    [Fact]
    public void FindsASubstringWithoutCaringAboutCase()
    {
        var match = FuzzySearch.Match("Discord", "DISC");

        Assert.NotNull(match);
        Assert.Equal(new SearchRange(0, 4), Assert.Single(match.Ranges));
    }

    [Fact]
    public void AWordStartScoresHigherThanTheMiddleOfAWord()
    {
        var start = FuzzySearch.Match("Playback", "play");
        var middle = FuzzySearch.Match("Autoplay", "play");

        Assert.NotNull(start);
        Assert.NotNull(middle);
        Assert.True(start.Score > middle.Score);
        Assert.Equal(new SearchRange(0, 4), Assert.Single(start.Ranges));
        Assert.Equal(new SearchRange(4, 4), Assert.Single(middle.Ranges));
    }

    [Fact]
    public void FallsBackToLettersInOrder()
    {
        var match = FuzzySearch.Match("Discord", "dsc");

        Assert.NotNull(match);
        Assert.Equal([new SearchRange(0, 1), new SearchRange(2, 2)], match.Ranges);
    }

    [Fact]
    public void EveryWordOfTheQueryHasToMatch()
    {
        var match = FuzzySearch.Match("Lyrics size", "lyr size");

        Assert.NotNull(match);
        Assert.Equal([new SearchRange(0, 3), new SearchRange(7, 4)], match.Ranges);

        Assert.Null(FuzzySearch.Match("Lyrics size", "lyr missing"));
    }

    [Fact]
    public void FindsWordsInAnyOrder()
    {
        var match = FuzzySearch.Match("Autoplay when the queue ends", "queue autoplay");

        Assert.NotNull(match);
        Assert.Equal([new SearchRange(0, 8), new SearchRange(18, 5)], match.Ranges);
    }

    [Fact]
    public void FindsAWordAtTheStartOfALaterWord()
    {
        var match = FuzzySearch.Match("Normalize volume", "volume");

        Assert.NotNull(match);
        Assert.Equal(new SearchRange(10, 6), Assert.Single(match.Ranges));
    }

    [Fact]
    public void PunctuationIsPartOfTheText()
    {
        var match = FuzzySearch.Match("Last.fm", "last.fm");

        Assert.NotNull(match);
        Assert.Equal(new SearchRange(0, 7), Assert.Single(match.Ranges));
    }

    [Fact]
    public void NoMatchReturnsNull()
    {
        Assert.Null(FuzzySearch.Match("Lyrics", "xyz"));
        Assert.Null(FuzzySearch.Match("Lyrics", "lyricszz"));
    }

    [Fact]
    public void EmptyTextOnlyMatchesAnEmptyQuery()
    {
        Assert.Null(FuzzySearch.Match("", "lyrics"));
        Assert.Null(FuzzySearch.Match(null, "lyrics"));
        Assert.NotNull(FuzzySearch.Match(null, ""));
    }
}
