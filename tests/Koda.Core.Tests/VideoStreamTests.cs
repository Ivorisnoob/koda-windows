using System.Xml.Linq;
using Koda.Core.InnerTube;
using Koda.Core.Video;

namespace Koda.Core.Tests;

public class VideoStreamTests
{
    private static readonly XNamespace Ns = "urn:mpeg:dash:schema:mpd:2011";

    private const string Sample = """
        {
          "videoDetails": {"lengthSeconds": "213"},
          "streamingData": {"adaptiveFormats": [
            {"itag": 248, "mimeType": "video/webm; codecs=\"vp9\"", "width": 1920, "height": 1080, "bitrate": 1500000,
             "url": "https://host/248", "initRange": {"start": "0", "end": "219"}, "indexRange": {"start": "220", "end": "874"}, "approxDurationMs": "213040"},
            {"itag": 137, "mimeType": "video/mp4; codecs=\"avc1.640028\"", "width": 1920, "height": 1080, "fps": 25, "bitrate": 4300000,
             "url": "https://host/137?a=1&b=2", "initRange": {"start": "0", "end": "741"}, "indexRange": {"start": "742", "end": "1229"}, "approxDurationMs": "213040"},
            {"itag": 136, "mimeType": "video/mp4; codecs=\"avc1.4D401F\"", "width": 1280, "height": 720, "fps": 25, "bitrate": 1000000,
             "url": "https://host/136", "initRange": {"start": "0", "end": "739"}, "indexRange": {"start": "740", "end": "1227"}, "approxDurationMs": "213040"},
            {"itag": 139, "mimeType": "audio/mp4; codecs=\"mp4a.40.5\"", "bitrate": 50000, "audioSampleRate": "22050", "audioChannels": 2,
             "url": "https://host/139", "initRange": {"start": "0", "end": "731"}, "indexRange": {"start": "732", "end": "1027"}, "approxDurationMs": "213159"},
            {"itag": 140, "mimeType": "audio/mp4; codecs=\"mp4a.40.2\"", "bitrate": 130000, "audioSampleRate": "44100", "audioChannels": 2,
             "url": "https://host/140", "initRange": {"start": "0", "end": "722"}, "indexRange": {"start": "723", "end": "1018"}, "approxDurationMs": "213089"},
            {"itag": 251, "mimeType": "audio/webm; codecs=\"opus\"", "bitrate": 136000,
             "url": "https://host/251", "initRange": {"start": "0", "end": "258"}, "indexRange": {"start": "259", "end": "629"}, "approxDurationMs": "213061"}
          ]}
        }
        """;

    private static XElement[] Sets(string manifest) =>
        XDocument.Parse(manifest).Root!.Element(Ns + "Period")!.Elements(Ns + "AdaptationSet").ToArray();

    [Fact]
    public void BuildsSeparateH264AndAacSetsAndLeavesWebmOut()
    {
        var (manifest, qualities) = DashManifest.Build(Json.Parse(Sample))!.Value;

        var sets = Sets(manifest);
        Assert.Equal(2, sets.Length);
        Assert.Equal(["136", "137"], sets[0].Elements(Ns + "Representation").Select(r => (string)r.Attribute("id")!));
        var audio = Assert.Single(sets[1].Elements(Ns + "Representation"));
        Assert.Equal("140", (string)audio.Attribute("id")!);
        Assert.Equal([1080, 720], qualities.Select(q => q.Height));
    }

    [Fact]
    public void DescribesEachFormatsByteRangesAndTheLongestDuration()
    {
        var (manifest, _) = DashManifest.Build(Json.Parse(Sample))!.Value;

        Assert.Equal("PT213.089S", (string)XDocument.Parse(manifest).Root!.Attribute("mediaPresentationDuration")!);
        var representation = Sets(manifest)[0].Elements(Ns + "Representation").Last();
        Assert.Equal("https://host/137?a=1&b=2", (string)representation.Element(Ns + "BaseURL")!);
        var segments = representation.Element(Ns + "SegmentBase")!;
        Assert.Equal("742-1229", (string)segments.Attribute("indexRange")!);
        Assert.Equal("0-741", (string)segments.Element(Ns + "Initialization")!.Attribute("range")!);
    }

    [Fact]
    public void KeepsTheDefaultSoundtrackOfADubbedVideo()
    {
        var dubbed = Json.Parse("""
            {"streamingData": {"adaptiveFormats": [
              {"itag": 136, "mimeType": "video/mp4; codecs=\"avc1.4D401F\"", "width": 1280, "height": 720, "bitrate": 1000000,
               "url": "https://host/v", "initRange": {"start": "0", "end": "1"}, "indexRange": {"start": "2", "end": "3"}, "approxDurationMs": "1000"},
              {"itag": 140, "mimeType": "audio/mp4; codecs=\"mp4a.40.2\"", "bitrate": 140000, "audioTrack": {"audioIsDefault": false},
               "url": "https://host/dub", "initRange": {"start": "0", "end": "1"}, "indexRange": {"start": "2", "end": "3"}},
              {"itag": 139, "mimeType": "audio/mp4; codecs=\"mp4a.40.2\"", "bitrate": 130000, "audioTrack": {"audioIsDefault": true},
               "url": "https://host/original", "initRange": {"start": "0", "end": "1"}, "indexRange": {"start": "2", "end": "3"}}
            ]}}
            """);

        var (manifest, _) = DashManifest.Build(dubbed)!.Value;

        var audio = Assert.Single(Sets(manifest)[1].Elements(Ns + "Representation"));
        Assert.Equal("https://host/original", (string)audio.Element(Ns + "BaseURL")!);
    }

    [Fact]
    public void RefusesAResponseWindowsCannotPlay()
    {
        var noAudio = Json.Parse("""
            {"streamingData": {"adaptiveFormats": [
              {"itag": 136, "mimeType": "video/mp4; codecs=\"avc1.4D401F\"", "url": "https://host/v",
               "initRange": {"start": "0", "end": "1"}, "indexRange": {"start": "2", "end": "3"}, "approxDurationMs": "1000"}
            ]}}
            """);
        Assert.Null(DashManifest.Build(noAudio));
        Assert.Null(DashManifest.Build(Json.Parse("""{"playabilityStatus": {"status": "OK"}}""")));
    }

    [Fact]
    public void ReadsAPortraitVideosShortSideAsItsQuality()
    {
        var portrait = Sample.Replace("\"width\": 1920, \"height\": 1080, \"fps\"", "\"width\": 1080, \"height\": 1920, \"fps\"");
        var (_, qualities) = DashManifest.Build(Json.Parse(portrait))!.Value;
        Assert.Equal(1080, qualities[0].Height);
    }

    [Fact]
    public void ManifestForHoldsVideoToTheBestRungThatFitsAndKeepsTheAudio()
    {
        var (manifest, qualities) = DashManifest.Build(Json.Parse(Sample))!.Value;
        var stream = new VideoStream("v", manifest, qualities, "agent", null);

        static string[] Ids(string text) =>
            Sets(text)[0].Elements(Ns + "Representation").Select(r => (string)r.Attribute("id")!).ToArray();

        Assert.Equal(["137"], Ids(stream.ManifestFor(2160)));
        Assert.Equal(["136"], Ids(stream.ManifestFor(720)));
        Assert.Equal(["136"], Ids(stream.ManifestFor(480)));
        Assert.Single(Sets(stream.ManifestFor(720))[1].Elements(Ns + "Representation"));
        Assert.StartsWith("<?xml", stream.ManifestFor(720));
    }

    [Fact]
    public void AnExpiringStreamIsNotFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var stream = new VideoStream("v", "<MPD/>", [], "agent", now.AddMinutes(5));
        Assert.False(stream.IsFresh(now));
        Assert.True((stream with { ExpiresAt = now.AddHours(1) }).IsFresh(now));
    }
}
