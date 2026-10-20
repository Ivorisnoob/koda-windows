using Koda.Core.InnerTube;

namespace Koda.Core.Parsing;

/// <summary>The page types InnerTube names in browse links.</summary>
public enum MusicPageType
{
    Artist,
    UserChannel,
    Album,
    Playlist,
}

/// <summary>Where a browse link lands, with the page type YouTube names it as.</summary>
public sealed record BrowseTarget(string Id, string? Params, MusicPageType? PageType)
{
    public bool IsArtist =>
        PageType is MusicPageType.Artist or MusicPageType.UserChannel
        || (PageType is null && Id.StartsWith("UC"));

    public bool IsAlbum => PageType is MusicPageType.Album || (PageType is null && Id.StartsWith("MPRE"));

    public bool IsPlaylist => PageType is MusicPageType.Playlist || (PageType is null && Id.StartsWith("VL"));
}

public static class JsonBrowse
{
    /// <summary>A browse endpoint's target, or null when this node carries none.</summary>
    public static BrowseTarget? TargetOf(this Json json)
    {
        var endpoint = json["browseEndpoint"];
        var id = endpoint["browseId"].NonEmpty;
        if (id is null) return null;
        var type = endpoint["browseEndpointContextSupportedConfigs"]
            ["browseEndpointContextMusicConfig"]["pageType"].String;
        var pageType = (MusicPageType?)null;
        if (type is not null)
        {
            var probe = type switch
            {
                "MUSIC_PAGE_TYPE_ARTIST" => MusicPageType.Artist,
                "MUSIC_PAGE_TYPE_USER_CHANNEL" => MusicPageType.UserChannel,
                "MUSIC_PAGE_TYPE_ALBUM" => MusicPageType.Album,
                "MUSIC_PAGE_TYPE_PLAYLIST" => MusicPageType.Playlist,
                _ => (MusicPageType?)null,
            };
            pageType = probe;
        }
        return new BrowseTarget(id, endpoint["params"].NonEmpty, pageType);
    }

    /// <summary>A run's or renderer's browse link.</summary>
    public static BrowseTarget? LinkOf(this Json json) => json["navigationEndpoint"].TargetOf();
}

/// <summary>
/// Reading WEB_REMIX rows into models. [verified October 2026 against live
/// search, browse, explore, charts and next responses, signed out; the port
/// is re-proved offline against the same fixture set]
///
/// Identity comes from links and page types, never from a run's position:
/// collaborators sit where single-artist rows put the album, and a row's
/// second column can lead with a type label ("Song", "Single") or not at all.
/// </summary>
public static class Rows
{
    public static readonly HashSet<string> TypeLabels =
        ["Song", "Video", "Album", "Single", "EP", "Playlist", "Artist", "Episode", "Podcast", "Profile"];

    /// <summary>The byline as a pair, matching the Swift tuple <c>(line, artists)</c>.</summary>
    public sealed record Byline(string Line, ArtistRef[] Artists)
    {
        public static readonly Byline Empty = new("", []);
    }

    // MARK: Text helpers

    /// <summary>Runs split into " • " separated segments.</summary>
    public static List<Json[]> Segments(Json[] runs)
    {
        var segments = new List<Json[]> { Array.Empty<Json>() };
        foreach (var run in runs)
        {
            if (run["text"].String?.Trim() == "•")
            {
                segments.Add([]);
            }
            else
            {
                segments[^1] = [.. segments[^1], run];
            }
        }
        return segments.Where(s => s.Length > 0).ToList();
    }

    /// <summary>
    /// A row's columns after the title as one run list, with a "•" between columns. A column
    /// boundary separates fields as the dot does: an artist page's songs read "The Weeknd" |
    /// "3.3B plays" | "After Hours" with no dot, which flattened as-is ran into one byline.
    /// </summary>
    public static Json[] MetaRuns(Json[] columns)
    {
        // {"text": " • "}, matching the Swift port's injected separator run.
        var separator = new[] { new Json(System.Text.Json.JsonSerializer.SerializeToElement(
            new Dictionary<string, string> { ["text"] = " • " })) };
        var lists = columns.Skip(1).Select(RunsOf).Where(r => r.Length > 0).ToList();
        var joined = new List<Json>();
        for (var i = 0; i < lists.Count; i++)
        {
            if (i > 0) joined.AddRange(separator);
            joined.AddRange(lists[i]);
        }
        return [.. joined];
    }

    /// <summary>A formatted string's runs; plain text nodes carry none.</summary>
    public static Json[] RunsOf(Json node) => node["runs"].Array;

    public static string PlainText(Json[] runs) =>
        string.Concat(runs.Select(r => r["text"].String)).Trim();

    /// <summary>"3:24" or "1:02:11", else null.</summary>
    public static double? DurationOf(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts is not { Length: 2 or 3 }) return null;
        var total = 0d;
        foreach (var part in parts)
        {
            if (part.Length == 0 || !part.All(char.IsDigit)) return null;
            total = total * 60 + double.Parse(part);
        }
        return total;
    }

    /// <summary>A bare four-digit year, else null.</summary>
    public static int? YearOf(string text)
    {
        var t = text.Trim();
        return t.Length == 4 && int.TryParse(t, out var y) && y is >= 1900 and <= 2100 ? y : null;
    }

    /// <summary>The byline: the segment that links an artist, else the first segment that reads as a name.</summary>
    public static Byline BylineOf(Json[] runs)
    {
        var segments = Segments(runs);
        foreach (var segment in segments.Where(s => s.Any(run => run.LinkOf()?.IsArtist == true)))
        {
            var refs = segment
                .Select(run => (run, target: run.LinkOf()))
                .Where(pair => pair.target is { IsArtist: true } && pair.run["text"].NonEmpty is not null)
                .Select(pair => new ArtistRef(pair.target!.Id, pair.run["text"].NonEmpty!))
                .ToArray();
            return new Byline(PlainText(segment), refs);
        }
        foreach (var segment in segments)
        {
            if (!segment.All(run => run.LinkOf() is null)) continue;
            var text = PlainText(segment);
            if (text.Length == 0 || TypeLabels.Contains(text) || DurationOf(text) is not null || YearOf(text) is not null)
                continue;
            if (text.Length > 0 && char.IsDigit(text[0])) continue; // "4M views", "50 songs"
            return new Byline(text, [new ArtistRef(null, text)]);
        }
        return Byline.Empty;
    }

    public static AlbumRef? AlbumRefOf(Json[] runs)
    {
        foreach (var run in runs)
        {
            if (run.LinkOf() is { IsAlbum: true } target && run["text"].NonEmpty is { } title)
                return new AlbumRef(target.Id, title);
        }
        return null;
    }

    public static ReleaseKind? KindOf(Json[] runs)
    {
        foreach (var run in runs.Where(r => r.LinkOf() is null))
        {
            var text = (run["text"].String ?? "").Trim();
            if (text == "Album") return ReleaseKind.Album;
            if (text == "EP") return ReleaseKind.Ep;
            if (text == "Single") return ReleaseKind.Single;
        }
        return null;
    }

    public static int? YearIn(Json[] runs) =>
        runs.Where(r => r.LinkOf() is null).Select(r => YearOf(r["text"].String ?? "")).FirstOrDefault(y => y is not null);

    /// <summary>A music video or upload rather than the audio track itself; no type at all reads as a track.</summary>
    public static bool IsVideoOf(Json node) =>
        node.Strings("musicVideoType") is { Length: > 0 } types && types[0] != "MUSIC_VIDEO_TYPE_ATV";

    // MARK: musicResponsiveListItemRenderer

    public static Json[] ColumnsOf(Json row) =>
        row["flexColumns"].Array.Select(c => c["musicResponsiveListItemFlexColumnRenderer"]["text"]).ToArray();

    public static string? VideoIdOf(Json row)
    {
        if (row["playlistItemData"]["videoId"].NonEmpty is { } a) return a;
        if (row["videoId"].NonEmpty is { } b) return b;
        if (row["navigationEndpoint"]["watchEndpoint"]["videoId"].NonEmpty is { } c) return c;
        if (row["overlay"].First("watchEndpoint")["videoId"].NonEmpty is { } d) return d;
        if (ColumnsOf(row) is { Length: > 0 } cols)
            return RunsOf(cols[0]).Select(r => r["navigationEndpoint"]["watchEndpoint"]["videoId"].NonEmpty)
                .FirstOrDefault(v => v is not null);
        return null;
    }

    /// <summary>Any list row: a song or video, or an album, artist or playlist link.</summary>
    public static MusicItem? ItemFromListRow(Json row, Byline? fallbackArtist = null)
    {
        var cols = ColumnsOf(row);
        if (cols.Length == 0) return null;
        var title = cols[0].Text().Trim();
        if (title.Length == 0) return null;
        var meta = MetaRuns(cols);
        var artwork = row["thumbnail"].BestThumbnail();

        if (row.LinkOf() is { } target)
        {
            var subtitle = cols.Length > 1 ? PlainText(RunsOf(cols[1])) : "";
            if (target.IsAlbum)
            {
                var by = BylineOf(meta);
                return new MusicItem.AlbumRecord(new AlbumSummary(target.Id, title, by.Line, by.Artists,
                    YearIn(meta), KindOf(meta), artwork));
            }
            if (target.IsArtist)
            {
                var audience = Segments(meta).LastOrDefault() is { } last ? PlainText(last) : null;
                return new MusicItem.ArtistRecord(new ArtistSummary(target.Id, title,
                    audience is { } a && a != "Artist" ? a : null, artwork));
            }
            if (target.IsPlaylist)
            {
                var sub = subtitle.StartsWith("Playlist • ") ? subtitle["Playlist • ".Length..] : subtitle;
                return new MusicItem.PlaylistRecord(new PlaylistSummary(
                    target.Id.StartsWith("VL") ? target.Id[2..] : target.Id, title,
                    sub.Length == 0 ? null : sub, artwork));
            }
            return null;
        }

        var videoId = VideoIdOf(row);
        if (videoId is null) return null;
        var byline = BylineOf(meta);
        if (byline.Line.Length == 0 && fallbackArtist is not null) byline = fallbackArtist;
        var fixedRuns = row["fixedColumns"].Array
            .Select(c => c["musicResponsiveListItemFixedColumnRenderer"]["text"].Text()).ToList();
        var candidates = fixedRuns.Concat(meta.Select(r => r["text"].String));
        var duration = candidates.Select(t => DurationOf(t ?? "")).FirstOrDefault(d => d is not null);
        return new MusicItem.SongRecord(new Song(videoId, title, byline.Line, byline.Artists, AlbumRefOf(meta),
            duration, artwork, IsVideoOf(row), YearIn(meta)));
    }

    public static Song? SongFromListRow(Json row, Byline? fallbackArtist = null) =>
        ItemFromListRow(row, fallbackArtist) is MusicItem.SongRecord { Value: { } song } ? song : null;

    // MARK: musicTwoRowItemRenderer

    public static MusicItem? ItemFromTwoRow(Json row)
    {
        var title = row["title"].Text().Trim();
        if (title.Length == 0) return null;
        var subtitleRuns = row["subtitle"].Runs();
        var subtitle = PlainText(subtitleRuns);
        var artwork = row["thumbnailRenderer"].BestThumbnail();
        var nav = row["navigationEndpoint"];

        if (nav["watchEndpoint"]["videoId"].NonEmpty is { } videoId)
        {
            var by = BylineOf(subtitleRuns);
            return new MusicItem.SongRecord(new Song(videoId, title, by.Line, by.Artists, AlbumRefOf(subtitleRuns),
                null, artwork, IsVideoOf(row), null));
        }
        if (nav.TargetOf() is not { } target) return null;
        if (target.IsAlbum)
        {
            var by = BylineOf(subtitleRuns);
            return new MusicItem.AlbumRecord(new AlbumSummary(target.Id, title, by.Line, by.Artists,
                YearIn(subtitleRuns), KindOf(subtitleRuns), artwork));
        }
        if (target.IsArtist)
            return new MusicItem.ArtistRecord(new ArtistSummary(target.Id, title,
                subtitle.Length == 0 ? null : subtitle, artwork));
        if (target.IsPlaylist)
            return new MusicItem.PlaylistRecord(new PlaylistSummary(
                target.Id.StartsWith("VL") ? target.Id[2..] : target.Id, title,
                subtitle.Length == 0 ? null : subtitle, artwork));
        return null;
    }

    // MARK: playlistPanelVideoRenderer (the /next queue)

    public static Song? SongFromPanel(Json panel)
    {
        var videoId = panel["videoId"].NonEmpty;
        if (videoId is null) return null;
        var title = panel["title"].Text().Trim();
        if (title.Length == 0) return null;
        var runs = panel["longBylineText"].Runs();
        var by = BylineOf(runs);
        if (by.Line.Length == 0) by = BylineOf(panel["shortBylineText"].Runs());
        return new Song(videoId, title, by.Line, by.Artists, AlbumRefOf(runs),
            DurationOf(panel["lengthText"].Text()), panel["thumbnail"].BestThumbnail(),
            IsVideoOf(panel), YearIn(runs));
    }

    /// <summary>Any renderer wrapper found in a shelf's contents.</summary>
    public static MusicItem? ItemFromEntry(Json entry)
    {
        if (entry["musicTwoRowItemRenderer"].Exists) return ItemFromTwoRow(entry["musicTwoRowItemRenderer"]);
        if (entry["musicResponsiveListItemRenderer"].Exists)
            return ItemFromListRow(entry["musicResponsiveListItemRenderer"]);
        return null;
    }
}
