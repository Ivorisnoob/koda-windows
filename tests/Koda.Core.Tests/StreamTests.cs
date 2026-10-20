using Koda.Core;

namespace Koda.Core.Tests;

public class StreamTests
{
    private const string VideoId = "4NRXx6U8ABQ";

    [Fact]
    public void PickAudioTakesTheBestAudioOnlyMp4()
    {
        var stream = StreamResolver.PickAudio(Fixtures.Load("player.json"), VideoId);

        Assert.NotNull(stream);
        Assert.Equal(VideoId, stream!.VideoId);
        // The VisionOS player answers AAC audio/mp4 only: itags 139 (48k) and 140 (128k) carry URLs,
        // and webm/opus itags plus every video format are out of the pool.
        Assert.Equal(140, stream.Itag);
        Assert.Equal("4250167", stream.ContentLength!.Value.ToString());
        Assert.Equal(3.41, stream.LoudnessDb!.Value, 2);
        Assert.Equal(262.571, stream.Duration!.Value, 3); // approxDurationMs / 1000
        Assert.Contains("googlevideo.com", stream.Url.Host);
        Assert.Contains("itag=140", stream.Url.Query);
        Assert.EndsWith("VISIONOS", stream.Url.Query);
        Assert.Equal(ClientProfiles.Default.VisionOs.UserAgent, stream.UserAgent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792025411), stream.ExpiresAt);
    }

    [Fact]
    public void PickAudioRefusesResponsesWithoutPlayableAudio()
    {
        var noAudio = Json.Parse("""
            {"streamingData": {"adaptiveFormats": [{"itag": 137, "mimeType": "video/mp4", "url": "https://host/x"}]}}
            """);
        Assert.Null(StreamResolver.PickAudio(noAudio, VideoId));

        var noUrl = Json.Parse("""
            {"streamingData": {"adaptiveFormats": [{"itag": 140, "mimeType": "audio/mp4"}]}}
            """);
        Assert.Null(StreamResolver.PickAudio(noUrl, VideoId));

        var noData = Json.Parse("""{"playabilityStatus": {"status": "OK"}}""");
        Assert.Null(StreamResolver.PickAudio(noData, VideoId));
    }

    [Fact]
    public void PickAudioMatchesTheOctober2026Capture()
    {
        // Captured live this month with the shipped Clients.xml voice (VisionOS 1.02);
        // same video as the sibling's player.json, sanitized to the same plant shape.
        var stream = StreamResolver.PickAudio(Fixtures.Load("player-2026-10.json"), VideoId);

        Assert.NotNull(stream);
        Assert.Equal(140, stream!.Itag);
        Assert.Equal("4250167", stream.ContentLength!.Value.ToString());
        Assert.Equal(262.571, stream.Duration!.Value, 3);
        Assert.Contains("rr1---sn-test.googlevideo.com", stream.Url.Host);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792403571), stream.ExpiresAt);
    }

    [Fact]
    public void FreshnessHoldsAMargin()
    {
        var now = DateTimeOffset.UtcNow;
        var expiring = new ResolvedStream(VideoId, new Uri("https://host/x"), "ua", 140, 1, 1, null, now.AddMinutes(5));
        var fresh = new ResolvedStream(VideoId, new Uri("https://host/x"), "ua", 140, 1, 1, null, now.AddMinutes(11));
        var openEnded = new ResolvedStream(VideoId, new Uri("https://host/x"), "ua", 140, 1, 1, null, null);

        Assert.False(expiring.IsFresh(now));
        Assert.True(fresh.IsFresh(now));
        Assert.True(openEnded.IsFresh(now));
    }
}
