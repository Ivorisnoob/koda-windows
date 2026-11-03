using Koda.Core.InnerTube;
using Koda.Core.Video;

namespace Koda.Core.Tests;

public class VideoExtrasTests
{
    private static string Chapter(string title, long millis) =>
        "{\"chapterRenderer\": {\"title\": {\"simpleText\": \"" + title + "\"}, \"timeRangeStartMillis\": " + millis + "}}";

    private static string Set(string key, params string[] chapters) =>
        "{\"key\": \"" + key + "\", \"value\": {\"chapters\": [" + string.Join(", ", chapters) + "]}}";

    private static Json Markers(params string[] sets) => Json.Parse(
        "{\"playerOverlays\": {\"playerOverlayRenderer\": {\"decoratedPlayerBarRenderer\": {\"decoratedPlayerBarRenderer\": {\"playerBar\": "
        + "{\"multiMarkersPlayerBarRenderer\": {\"markersMap\": [" + string.Join(", ", sets) + "]}}}}}}}");

    [Fact]
    public void ReadsTheDescriptionsChaptersBeforeAutomaticOnes()
    {
        var root = Markers(
            Set("AUTO_CHAPTERS", Chapter("Auto", 0), Chapter("Auto two", 5000)),
            Set("DESCRIPTION_CHAPTERS", Chapter("Intro", 0), Chapter("Verse", 61500), Chapter("Outro", 200000)));

        var chapters = VideoPages.Chapters(root);

        Assert.Equal(["Intro", "Verse", "Outro"], chapters.Select(c => c.Title));
        Assert.Equal(61.5, chapters[1].Start);
    }

    [Fact]
    public void OneChapterIsNoChapters()
    {
        var root = Markers(Set("DESCRIPTION_CHAPTERS", Chapter("Only", 0)));
        Assert.Empty(VideoPages.Chapters(root));
        Assert.Empty(VideoPages.Chapters(Json.Parse("{}")));
    }

    [Fact]
    public void ChapterAtFindsThePlayingOne()
    {
        var page = new WatchPage(new Koda.Core.Video.Video("v", "t", null), null, null, "", null, null, [], LikeStatus.Indifferent,
            null, null, null, VideoPage.Empty, [new VideoChapter("A", 0), new VideoChapter("B", 30)]);
        Assert.Equal("A", page.ChapterAt(10)!.Title);
        Assert.Equal("B", page.ChapterAt(30)!.Title);
        Assert.Null((page with { Chapters = [] }).ChapterAt(10));
    }

    [Fact]
    public void SponsorBlockKeepsOnlyThisVideosSkipSegments()
    {
        const string answer = """
            [
              {"videoID": "other", "segments": [{"category": "sponsor", "actionType": "skip", "segment": [1, 50]}]},
              {"videoID": "abc", "segments": [
                {"category": "selfpromo", "actionType": "skip", "segment": [200.5, 230]},
                {"category": "sponsor", "actionType": "skip", "segment": [10, 40.25]},
                {"category": "sponsor", "actionType": "mute", "segment": [60, 70]},
                {"category": "interaction", "actionType": "skip", "segment": [80, 80.4]}
              ]}
            ]
            """;

        var segments = SponsorBlock.Parse(answer, "abc");

        Assert.Equal([10, 200.5], segments.Select(s => s.Start));
        Assert.Equal("sponsor", segments[0].Label);
        Assert.Equal("self-promotion", segments[1].Label);
        Assert.Empty(SponsorBlock.Parse(answer, "missing"));
    }

    [Fact]
    public void SponsorBlockAsksByAFourCharacterHashPrefix()
    {
        var prefix = SponsorBlock.HashPrefix("dQw4w9WgXcQ");
        Assert.Equal(4, prefix.Length);
        Assert.Matches("^[0-9a-f]{4}$", prefix);
    }
}
