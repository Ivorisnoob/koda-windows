using System.Globalization;
using System.Xml.Linq;
using Koda.Core.InnerTube;

namespace Koda.Core.Video;

/// <summary>One rung of a video's quality ladder.</summary>
/// <param name="Height">The picture's lines: the shorter side, so a portrait video reads as its real quality.</param>
public sealed record VideoQuality(int Height, int Fps, int Bandwidth);

/// <summary>
/// A video ready for Windows' <c>AdaptiveMediaSource</c>.
///
/// Mac and iOS hand YouTube's HLS master to AVPlayer. Media Foundation refuses
/// that master (0xC00D36CB): its audio is a separate rendition of raw ADTS AAC
/// with ID3 timestamps, which Windows' HLS source does not take. The visionOS
/// response also carries plain fMP4 URLs with their index ranges, so a recorded
/// video is described to Windows as an on-demand DASH manifest built here, with
/// separate audio and video, adaptive switching and seeking, and no remuxing.
/// </summary>
/// <param name="Manifest">The DASH MPD text.</param>
/// <param name="Qualities">The ladder, tallest first: the Quality menu.</param>
public sealed record VideoStream(
    string VideoId,
    string Manifest,
    IReadOnlyList<VideoQuality> Qualities,
    string UserAgent,
    DateTimeOffset? ExpiresAt)
{
    public bool IsFresh(DateTimeOffset? now = null) =>
        ExpiresAt is not { } e || (now ?? DateTimeOffset.UtcNow).AddMinutes(10) < e;

    /// <summary>
    /// The manifest held to one height: the tallest rung not above it, or the shortest
    /// rung when nothing fits. A chosen quality plays from this in a new source, because
    /// Windows ignores a raised DesiredMinBitrate mid-play. Restore the position by setting
    /// PlaybackSession.Position in MediaOpened: MediaPlaybackItem's start time is ignored here.
    /// </summary>
    public string ManifestFor(int height)
    {
        var document = XDocument.Parse(Manifest);
        var ns = document.Root!.Name.Namespace;
        var video = document.Root.Element(ns + "Period")!.Elements(ns + "AdaptationSet")
            .First(set => (string?)set.Attribute("contentType") == "video");
        var rungs = video.Elements(ns + "Representation").ToArray();
        static int Lines(XElement r) => Math.Min((int?)r.Attribute("width") ?? 0, (int?)r.Attribute("height") ?? 0);
        var fits = rungs.Where(r => Lines(r) <= height).ToArray();
        var target = fits.Length > 0 ? fits.Max(Lines) : rungs.Min(Lines);
        foreach (var rung in rungs.Where(r => Lines(r) != target)) rung.Remove();
        return document.Declaration + "\n" + document.Root;
    }
}

/// <summary>Builds the on-demand DASH manifest of a recorded video from a <c>/player</c> response.</summary>
public static class DashManifest
{
    private static readonly XNamespace Ns = "urn:mpeg:dash:schema:mpd:2011";

    /// <summary>The MPD and its ladder; null when the response lacks H.264 or AAC formats with index ranges.</summary>
    public static (string Manifest, IReadOnlyList<VideoQuality> Qualities)? Build(Json root)
    {
        var formats = root["streamingData"]["adaptiveFormats"].Array;
        var video = formats
            .Where(f => IsMp4(f, "video/mp4", "avc1") && IsIndexed(f))
            .GroupBy(f => f["itag"].Int)
            .Select(g => g.First())
            .OrderBy(f => f["bitrate"].Int ?? 0)
            .ToArray();
        var audio = BestAudio(formats.Where(f => IsMp4(f, "audio/mp4", "mp4a") && IsIndexed(f)).ToArray());
        if (video.Length == 0 || audio is not { } sound) return null;

        var milliseconds = video.Append(sound).Max(f => f["approxDurationMs"].Double ?? 0);
        var seconds = milliseconds > 0 ? milliseconds / 1000d : root["videoDetails"]["lengthSeconds"].Double ?? 0;
        if (seconds <= 0) return null;

        var mpd = new XElement(Ns + "MPD",
            new XAttribute("type", "static"),
            new XAttribute("profiles", "urn:mpeg:dash:profile:isoff-on-demand:2011"),
            new XAttribute("minBufferTime", "PT2S"),
            new XAttribute("mediaPresentationDuration", "PT" + seconds.ToString("0.###", CultureInfo.InvariantCulture) + "S"),
            new XElement(Ns + "Period",
                AdaptationSet("video", video.Select(VideoRepresentation)),
                AdaptationSet("audio", [AudioRepresentation(sound)])));

        var ladder = video
            .Select(f => new VideoQuality(
                Math.Min(f["width"].Int ?? 0, f["height"].Int ?? 0),
                f["fps"].Int ?? 0,
                f["bitrate"].Int ?? 0))
            .OrderByDescending(q => q.Height)
            .ThenByDescending(q => q.Bandwidth)
            .ToArray();
        return ("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + mpd, ladder);
    }

    private static bool IsMp4(Json format, string container, string codec)
    {
        var mime = format["mimeType"].String ?? "";
        return mime.StartsWith(container, StringComparison.Ordinal) && mime.Contains("codecs=\"" + codec, StringComparison.Ordinal);
    }

    private static bool IsIndexed(Json format) =>
        format["url"].NonEmpty is not null
        && format["initRange"]["end"].NonEmpty is not null
        && format["indexRange"]["end"].NonEmpty is not null;

    /// <summary>A dubbed video carries several soundtracks; the default one, at its best bitrate.</summary>
    private static Json? BestAudio(Json[] formats)
    {
        var originals = formats.Where(f => f["audioTrack"]["audioIsDefault"].Bool == true).ToArray();
        var untagged = formats.Where(f => !f["audioTrack"].Exists).ToArray();
        var pool = originals.Length > 0 ? originals : untagged.Length > 0 ? untagged : formats;
        return pool.Length > 0 ? pool.MaxBy(f => f["bitrate"].Int ?? 0) : null;
    }

    private static XElement AdaptationSet(string kind, IEnumerable<XElement> representations) =>
        new(Ns + "AdaptationSet",
            new XAttribute("contentType", kind),
            new XAttribute("mimeType", kind + "/mp4"),
            new XAttribute("segmentAlignment", "true"),
            new XAttribute("subsegmentAlignment", "true"),
            new XAttribute("subsegmentStartsWithSAP", "1"),
            representations);

    private static XElement VideoRepresentation(Json format)
    {
        var attributes = new List<object>
        {
            new XAttribute("width", format["width"].Int ?? 0),
            new XAttribute("height", format["height"].Int ?? 0),
        };
        if (format["fps"].Int is { } fps) attributes.Add(new XAttribute("frameRate", fps));
        return Representation(format, attributes);
    }

    private static XElement AudioRepresentation(Json format) =>
        Representation(format,
        [
            new XAttribute("audioSamplingRate", format["audioSampleRate"].String ?? "44100"),
            new XElement(Ns + "AudioChannelConfiguration",
                new XAttribute("schemeIdUri", "urn:mpeg:dash:23003:3:audio_channel_configuration:2011"),
                new XAttribute("value", format["audioChannels"].Int ?? 2)),
        ]);

    private static XElement Representation(Json format, IEnumerable<object> extra)
    {
        var mime = format["mimeType"].String ?? "";
        var start = mime.IndexOf("codecs=\"", StringComparison.Ordinal) + "codecs=\"".Length;
        var codecs = mime[start..].TrimEnd('"');
        return new XElement(Ns + "Representation",
            new XAttribute("id", format["itag"].Int ?? 0),
            new XAttribute("bandwidth", format["bitrate"].Int ?? 0),
            new XAttribute("codecs", codecs),
            extra,
            new XElement(Ns + "BaseURL", format["url"].String),
            new XElement(Ns + "SegmentBase",
                new XAttribute("indexRange", Range(format["indexRange"])),
                new XAttribute("indexRangeExact", "true"),
                new XElement(Ns + "Initialization", new XAttribute("range", Range(format["initRange"])))));
    }

    private static string Range(Json range) => $"{range["start"].String}-{range["end"].String}";
}
