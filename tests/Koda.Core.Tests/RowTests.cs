using Koda.Core;
using Koda.Core.Parsing;

namespace Koda.Core.Tests;

public class RowTests
{
    private static Json Run(string text, string? browseId = null, string? pageType = null)
    {
        var nav = browseId is null
            ? "null"
            : $"{{\"browseEndpoint\": {{\"browseId\": \"{browseId}\", \"browseEndpointContextSupportedConfigs\": {{\"browseEndpointContextMusicConfig\": {{\"pageType\": \"{pageType ?? "MUSIC_PAGE_TYPE_ARTIST"}\"}}}}}}}}";
        return Json.Parse($$"""{"text": "{{text}}", "navigationEndpoint": {{nav}}}""");
    }

    private static Json[] Runs(params object[] pieces) => [.. pieces.Select(p => p switch
    {
        string s => Run(s),
        Json j => j,
        _ => throw new InvalidOperationException(),
    })];

    [Fact]
    public void DurationReadsMinutesAndHours()
    {
        Assert.Equal(210, Rows.DurationOf("3:30"));
        Assert.Equal(3731, Rows.DurationOf("1:02:11"));
        Assert.Null(Rows.DurationOf("3"));
        Assert.Null(Rows.DurationOf("a:b"));
        Assert.Null(Rows.DurationOf("1:2:3:4"));
        Assert.Null(Rows.DurationOf(""));
    }

    [Fact]
    public void YearOnlyReadsFourDigitYears()
    {
        Assert.Equal(2020, Rows.YearOf("2020"));
        Assert.Equal(1999, Rows.YearOf(" 1999 "));
        Assert.Null(Rows.YearOf("18ab"));
        Assert.Null(Rows.YearOf("12345"));
        Assert.Null(Rows.YearOf("Album"));
    }

    [Fact]
    public void BylinePrefersTheLinkedArtistSegment()
    {
        var runs = new[]
        {
            Run("The Weeknd", "UClYV6hHfLAb0n1ZzY8xS8gQ", "MUSIC_PAGE_TYPE_ARTIST"),
            Json.Parse("""{"text": " • "}"""),
            Run("Blinding Lights"),
        };
        var by = Rows.BylineOf(runs);
        Assert.Equal("The Weeknd", by.Line);
        Assert.Single(by.Artists);
        Assert.Equal("UClYV6hHfLAb0n1ZzY8xS8gQ", by.Artists[0].Id);
    }

    [Fact]
    public void BylineSkipsLabelsCountsAndYearsForUnlinkedNames()
    {
        var runs = new[]
        {
            Run("2020"), Json.Parse("""{"text": " • "}"""),
            Run("4M views"), Json.Parse("""{"text": " • "}"""),
            Run("Someone"),
        };
        var by = Rows.BylineOf(runs);
        Assert.Equal("Someone", by.Line);
    }

    [Fact]
    public void BylineReturnsEmptyWhenNothingReadsAsName()
    {
        var runs = new[] { Run("3:30"), Json.Parse("""{"text": " • "}"""), Run("Video") };
        Assert.Equal(Rows.Byline.Empty.Line, Rows.BylineOf(runs).Line);
        Assert.Empty(Rows.BylineOf(runs).Artists);
    }

    [Fact]
    public void MetaRunsGluesColumnsWithTheDot()
    {
        // Columns arrive as their text nodes, exactly as Rows.ColumnsOf unwraps them.
        var column = (string text) => Json.Parse(
            "{\"musicResponsiveListItemFlexColumnRenderer\": {\"text\": {\"runs\": [{\"text\": \"" +
            text.Replace("\"", "\\\"") + "\"}]}}}")["musicResponsiveListItemFlexColumnRenderer"]["text"];
        var meta = Rows.MetaRuns([column("Title"), column("The Weeknd"), column("3:30")]);
        var segments = Rows.Segments(meta);
        Assert.Equal(2, segments.Count);
        Assert.Equal("The Weeknd", Rows.PlainText(segments[0]));
        Assert.Equal("3:30", Rows.PlainText(segments[1]));
    }

    [Fact]
    public void KindReadsOnlyUnlinkedTypeRuns()
    {
        Assert.Equal(ReleaseKind.Album, Rows.KindOf(new[] { Run("Album") }));
        Assert.Equal(ReleaseKind.Ep, Rows.KindOf(new[] { Run("EP") }));
        Assert.Equal(ReleaseKind.Single, Rows.KindOf(new[] { Run("Single") }));
        var linkedType = Json.Parse("""
            {"text": "Album", "navigationEndpoint": {"browseEndpoint": {"browseId": "MPREabc"}}}
            """);
        Assert.Null(Rows.KindOf([linkedType]));
        Assert.Null(Rows.KindOf(new[] { Run("Song") }));
    }

    [Fact]
    public void VideoTypeJudgesTheWholeRow()
    {
        Assert.False(Rows.IsVideoOf(Json.Parse("""{}""")));
        Assert.False(Rows.IsVideoOf(Json.Parse("""{"wrapper": {"musicVideoType": "MUSIC_VIDEO_TYPE_ATV"}}""")));
        Assert.True(Rows.IsVideoOf(Json.Parse("""{"wrapper": {"musicVideoType": "MUSIC_VIDEO_TYPE_OMV"}}""")));
    }

    [Fact]
    public void BrowseTargetTypesComeFromLinksOrIds()
    {
        var artist = Json.Parse("""
            {"browseEndpoint": {"browseId": "UCabc"}}
            """).TargetOf();
        Assert.NotNull(artist);
        Assert.True(artist!.IsArtist);

        var album = Json.Parse("""
            {"browseEndpoint": {"browseId": "MPREabc"}}
            """).TargetOf();
        Assert.NotNull(album);
        Assert.True(album!.IsAlbum);

        var playlist = Json.Parse("""
            {"browseEndpoint": {"browseId": "VLabc", "browseEndpointContextSupportedConfigs": {"browseEndpointContextMusicConfig": {"pageType": "MUSIC_PAGE_TYPE_PLAYLIST"}}}}
            """).TargetOf();
        Assert.NotNull(playlist);
        Assert.True(playlist!.IsPlaylist);
    }

    [Fact]
    public void AlbumResizingRewritesOnlyTheSizeDirective()
    {
        var url = new Uri("https://lh3.googleusercontent.com/Long0paque-Token=s544");
        Assert.Equal("https://lh3.googleusercontent.com/Long0paque-Token=s120", new Artwork(url).Resized(120).ToString());

        var sized = new Uri("https://lh3.googleusercontent.com/Token=w544-h544-p=s544");
        // Only the directive after the last '=' is rewritten — the opaque token
        // protects everything before it (a blind rewrite would corrupt it into a 404).
        Assert.Equal("https://lh3.googleusercontent.com/Token=w544-h544-p=s120", new Artwork(sized).Resized(120).ToString());

        var alien = new Uri("https://example.com/x.png");
        Assert.Equal(alien, new Artwork(alien).Resized(120));
    }
}
