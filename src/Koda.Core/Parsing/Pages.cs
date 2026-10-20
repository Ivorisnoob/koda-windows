namespace Koda.Core.Parsing;

/// <summary>
/// Whole WEB_REMIX responses read into pages. [verified October 2026, signed
/// out; the port is re-proved offline against the same fixture set]
///
/// This phase ports what the fixtures prove and Phase 2 scopes: search,
/// album, playlist and the /next queue. Shelves (home, explore, charts,
/// moods) and the artist page ride along in the fixture set for Phase 5.
/// </summary>
public static class Pages
{
    // MARK: Shelves (Home, Explore, charts, a mood)

    public static ShelfPage Shelves(Json root)
    {
        var shelves = new List<Shelf>();
        foreach (var carousel in root.Objects("musicCarouselShelfRenderer"))
        {
            var header = carousel["header"]["musicCarouselShelfBasicHeaderRenderer"];
            var items = carousel["contents"].Array.Select(Rows.ItemFromEntry).OfType<MusicItem>().ToArray();
            if (items.Length == 0) continue;
            shelves.Add(new Shelf(header["title"].Text(), header["strapline"].Text().TrimmedOrNull(),
                DedupeById(items, i => i.Id)));
        }
        var moods = new List<Mood>();
        foreach (var chip in root.Objects("chipCloudChipRenderer"))
        {
            if (chip["navigationEndpoint"].TargetOf() is not { } target) continue;
            moods.Add(new Mood(chip["text"].Text(), target.Id, target.Params));
        }
        foreach (var button in root.Objects("musicNavigationButtonRenderer"))
        {
            if (button["clickCommand"].TargetOf() is not { } target) continue;
            moods.Add(new Mood(button["buttonText"].Text(), target.Id, target.Params));
        }
        return new ShelfPage([.. shelves], [.. moods]);
    }

    // MARK: Artist

    public static ArtistPage? Artist(Json root, string id)
    {
        var header = root.First("musicImmersiveHeaderRenderer").Exists
            ? root.First("musicImmersiveHeaderRenderer")
            : root.First("musicVisualHeaderRenderer");
        var name = header["title"].Text();
        if (name.Length == 0) return null;
        var banner = header["thumbnail"].BestThumbnail();
        var summary = new ArtistSummary(id, name, header["monthlyListenerCount"].Text().TrimmedOrNull(), banner);
        var fallback = new Rows.Byline(name, [new ArtistRef(id, name)]);

        var songShelf = root.First("musicShelfRenderer");
        var topSongs = songShelf["contents"].Array
            .Select(entry => Rows.SongFromListRow(entry["musicResponsiveListItemRenderer"], fallback))
            .OfType<Song>().ToArray();
        var allSongs = songShelf["bottomEndpoint"].TargetOf() is { } more
            ? (more.Id.StartsWith("VL") ? more.Id[2..] : more.Id)
            : null;

        AlbumSummary[] albums = [], singles = [];
        PlaylistSummary[] featured = [];
        ArtistSummary[] similar = [];
        foreach (var carousel in root.Objects("musicCarouselShelfRenderer"))
        {
            var title = carousel["header"]["musicCarouselShelfBasicHeaderRenderer"]["title"].Text();
            var items = carousel["contents"].Array.Select(Rows.ItemFromEntry).OfType<MusicItem>().ToArray();
            switch (title)
            {
                case "Albums":
                    albums = [.. items.OfType<MusicItem.AlbumRecord>().Select(a => a.Value with
                    {
                        Kind = a.Value.Kind ?? ReleaseKind.Album,
                        ArtistLine = a.Value.ArtistLine.Length == 0 ? name : a.Value.ArtistLine,
                    })];
                    break;
                case "Singles & EPs" or "Singles" or "EPs":
                    singles = [.. items.OfType<MusicItem.AlbumRecord>().Select(a => a.Value with
                    {
                        ArtistLine = a.Value.ArtistLine.Length == 0 ? name : a.Value.ArtistLine,
                    })];
                    break;
                case "Featured on":
                    featured = [.. items.OfType<MusicItem.PlaylistRecord>().Select(p => p.Value)];
                    break;
                case "Fans might also like":
                    similar = [.. items.OfType<MusicItem.ArtistRecord>().Select(a => a.Value)];
                    break;
            }
        }
        return new ArtistPage(summary, header["description"].Text().TrimmedOrNull(), banner, topSongs, allSongs,
            albums, singles, featured, similar);
    }

    // MARK: Search

    public static SearchResults Search(Json root)
    {
        var results = SearchResults.Empty;

        if (root.Objects("musicCardShelfRenderer") is { Length: > 0 } cards)
        {
            var card = cards[0];
            var title = card["title"];
            var titleRuns = title.Runs();
            var artwork = card["thumbnail"].BestThumbnail();
            var subtitleRuns = card["subtitle"].Runs();
            var target = titleRuns.Length > 0 ? titleRuns[0].LinkOf() : null;
            target ??= card["onTap"].TargetOf();
            if (target is not null)
            {
                var name = title.Text();
                if (target.IsArtist)
                {
                    results = results with
                    {
                        Top = new MusicItem.ArtistRecord(new ArtistSummary(target.Id, name,
                            SegmentAfterLabel(subtitleRuns), artwork)),
                    };
                    var fallback = new Rows.Byline(name, [new ArtistRef(target.Id, name)]);
                    results = results with
                    {
                        TopSongs = [.. card["contents"].Array
                            .Select(entry => Rows.SongFromListRow(entry["musicResponsiveListItemRenderer"], fallback))
                            .Where(s => s is not null).Select(s => s!)],
                    };
                }
                else if (target.IsAlbum)
                {
                    var by = Rows.BylineOf(subtitleRuns);
                    results = results with
                    {
                        Top = new MusicItem.AlbumRecord(new AlbumSummary(target.Id, name, by.Line, by.Artists,
                            Rows.YearIn(subtitleRuns), Rows.KindOf(subtitleRuns), artwork)),
                    };
                }
                else if (target.IsPlaylist)
                {
                    results = results with
                    {
                        Top = new MusicItem.PlaylistRecord(new PlaylistSummary(
                            target.Id.StartsWith("VL") ? target.Id[2..] : target.Id, name,
                            SegmentAfterLabel(subtitleRuns), artwork)),
                    };
                }
            }
            else if (titleRuns.Length > 0 && titleRuns[0]["navigationEndpoint"]["watchEndpoint"]["videoId"].NonEmpty is { } videoId)
            {
                var by = Rows.BylineOf(subtitleRuns);
                results = results with
                {
                    Top = new MusicItem.SongRecord(new Song(videoId, title.Text(), by.Line, by.Artists,
                        Rows.AlbumRefOf(subtitleRuns), null, artwork, Rows.IsVideoOf(card), null)),
                };
            }
        }

        // Rows outside the top card. The all-results page lists them one per
        // item section, a filtered page in one shelf; reading rows either way
        // and sorting by kind covers both.
        foreach (var shelf in root.Objects("musicShelfRenderer"))
        {
            foreach (var entry in shelf["contents"].Array)
            {
                var row = entry["musicResponsiveListItemRenderer"];
                if (!row.Exists) continue;
                var item = Rows.ItemFromListRow(row);
                if (item is null) continue;
                Add(item, row, ref results);
            }
        }
        foreach (var section in root.Objects("itemSectionRenderer"))
        {
            foreach (var entry in section["contents"].Array)
            {
                var row = entry["musicResponsiveListItemRenderer"];
                if (!row.Exists) continue;
                var item = Rows.ItemFromListRow(row);
                if (item is null) continue;
                Add(item, row, ref results);
            }
        }
        results = results with
        {
            Songs = DedupeById(results.Songs, s => s.Id),
            Videos = DedupeById(results.Videos, s => s.Id),
            Albums = DedupeById(results.Albums, a => a.Id),
            Artists = DedupeById(results.Artists, a => a.Id),
            Playlists = DedupeById(results.Playlists, p => p.Id),
        };
        return results;
    }

    private static void Add(MusicItem item, Json row, ref SearchResults results)
    {
        switch (item)
        {
            case MusicItem.SongRecord { Value: var song }:
                // Podcast episodes link a non-music page from their title.
                if (Rows.ColumnsOf(row) is { Length: > 0 } cols
                    && Rows.RunsOf(cols[0]).Any(run => run.LinkOf() is not null))
                {
                    return;
                }
                if (song.IsVideo) results = results with { Videos = [.. results.Videos, song] };
                else results = results with { Songs = [.. results.Songs, song] };
                break;
            case MusicItem.AlbumRecord { Value: var album }:
                results = results with { Albums = [.. results.Albums, album] };
                break;
            case MusicItem.ArtistRecord { Value: var artist }:
                // Profiles are user channels, not artists.
                var label = Rows.ColumnsOf(row) is { Length: > 1 } c ? Rows.PlainText(Rows.RunsOf(c[1])) : "";
                if (!label.StartsWith("Profile")) results = results with { Artists = [.. results.Artists, artist] };
                break;
            case MusicItem.PlaylistRecord { Value: var playlist }:
                results = results with { Playlists = [.. results.Playlists, playlist] };
                break;
        }
    }

    private static string? SegmentAfterLabel(Json[] runs)
    {
        var segments = Rows.Segments(runs).Select(Rows.PlainText)
            .Where(t => t.Length > 0 && !Rows.TypeLabels.Contains(t));
        return segments.LastOrDefault();
    }

    public static string? SearchContinuation(Json root)
    {
        var continuationData = root.Objects("nextContinuationData");
        if (continuationData.Length > 0 && continuationData[0]["continuation"].NonEmpty is { } token) return token;
        var command = root.Objects("continuationItemRenderer")
            .Select(c => c["continuationEndpoint"]["continuationCommand"]["token"].NonEmpty)
            .FirstOrDefault(t => t is not null);
        return command;
    }

    // MARK: Album

    public static AlbumPage? Album(Json root, string id)
    {
        var header = root.First("musicResponsiveHeaderRenderer").Exists
            ? root.First("musicResponsiveHeaderRenderer")
            : root.First("musicDetailHeaderRenderer");
        var title = header["title"].Text();
        if (title.Length == 0) return null;
        var strapline = header["straplineTextOne"].Runs();
        var by = Rows.BylineOf(strapline);
        if (by.Line.Length == 0) by = new Rows.Byline(Rows.PlainText(strapline), []);
        var subtitle = header["subtitle"].Runs();
        var artwork = header["thumbnail"].BestThumbnail();
        var summary = new AlbumSummary(id, title, by.Line, by.Artists, Rows.YearIn(subtitle),
            Rows.KindOf(subtitle), artwork);
        var albumRef = new AlbumRef(id, title);

        // Only the track shelf: "Other versions" and "Releases for you" must never become tracks.
        var songs = new List<Song>();
        foreach (var shelf in root.Objects("musicShelfRenderer"))
        {
            foreach (var entry in shelf["contents"].Array)
            {
                var row = entry["musicResponsiveListItemRenderer"];
                var song = Rows.SongFromListRow(row, by);
                if (song is null) continue;
                var filled = song.With(album: albumRef, year: summary.Year);
                if (filled.Artwork is null) filled = filled.With(artwork: artwork);
                songs.Add(filled);
            }
        }
        var otherShelves = root.Objects("musicCarouselShelfRenderer").ToArray();
        var other = Array.Find(otherShelves, carousel =>
            carousel["header"]["musicCarouselShelfBasicHeaderRenderer"]["title"].Text() == "Other versions");
        var otherVersions = other.Exists
            ? other["contents"].Array
                .Select(Rows.ItemFromEntry)
                .Where(i => i is MusicItem.AlbumRecord)
                .Select(i => ((MusicItem.AlbumRecord)i!).Value)
                .ToArray()
            : [];
        var description = header.First("musicDescriptionShelfRenderer")["description"].Text();
        return new AlbumPage(summary,
            description.TrimmedOrNull(),
            Rows.PlainText(header["secondSubtitle"].Runs()).TrimmedOrNull(),
            [.. songs], otherVersions,
            header.Strings("playlistId").FirstOrDefault(p => p.StartsWith("OLAK5uy_")));
    }

    /// <summary>
    /// Signed out, an album lists a track's music video in place of the song
    /// where it has one: same title and length, but the video's audio with its
    /// own intro and ending. The album's audio playlist has the songs.
    /// </summary>
    public static Song[] WithSongVersions(Song[] album, Song[] audio)
    {
        static string Key(Song s) => s.Title.Trim().ToLowerInvariant();
        var unused = new List<Song>(audio);
        var result = new Song[album.Length];
        for (var index = 0; index < album.Length; index++)
        {
            var track = album[index];
            if (!track.IsVideo)
            {
                result[index] = track;
                continue;
            }
            Song? match = null;
            if (index < audio.Length && Key(audio[index]) == Key(track) && unused.Contains(audio[index]))
            {
                match = audio[index];
            }
            else
            {
                match = unused.FirstOrDefault(a => Key(a) == Key(track));
            }
            if (match is null || !unused.Remove(match))
            {
                result[index] = track;
                continue;
            }
            result[index] = track.With(id: match.Id, isVideo: false, duration: match.Duration ?? track.Duration);
        }
        return result;
    }

    // MARK: Playlist

    public static PlaylistPage? Playlist(Json root, string id)
    {
        var header = root.First("musicResponsiveHeaderRenderer").Exists
            ? root.First("musicResponsiveHeaderRenderer")
            : root.First("musicDetailHeaderRenderer");
        var title = header["title"].Text();
        if (title.Length == 0) return null;
        var strapline = Rows.PlainText(header["straplineTextOne"].Runs());
        return new PlaylistPage(
            new PlaylistSummary(id, title, strapline.TrimmedOrNull(), header["thumbnail"].BestThumbnail()),
            strapline.TrimmedOrNull(),
            Rows.PlainText(header["secondSubtitle"].Runs()).TrimmedOrNull(),
            header.First("musicDescriptionShelfRenderer")["description"].Text().TrimmedOrNull(),
            PlaylistSongs(root),
            PlaylistContinuation(root));
    }

    public static Song[] PlaylistSongs(Json root)
    {
        var containers = root.Objects("musicPlaylistShelfRenderer")
            .Concat(root.Objects("appendContinuationItemsAction"));
        var songs = new List<Song>();
        foreach (var container in containers)
        {
            var entries = container["contents"].Array.Concat(container["continuationItems"].Array);
            foreach (var entry in entries)
            {
                var song = Rows.SongFromListRow(entry["musicResponsiveListItemRenderer"]);
                if (song is not null) songs.Add(song);
            }
        }
        return [.. songs];
    }

    /// <summary>
    /// Only the track shelf's token: a playlist page also carries a section
    /// continuation that leads to a shelf of related playlists, not more tracks.
    /// </summary>
    public static string? PlaylistContinuation(Json root)
    {
        var scopes = root.Objects("musicPlaylistShelfRenderer")
            .Concat(root.Objects("appendContinuationItemsAction"));
        foreach (var scope in scopes)
        {
            var command = scope.Objects("continuationCommand");
            if (command.Length > 0 && command[0]["token"].NonEmpty is { } token) return token;
            var data = scope.Objects("nextContinuationData");
            if (data.Length > 0 && data[0]["continuation"].NonEmpty is { } continuation) return continuation;
        }
        return null;
    }

    // MARK: Radio (/next)

    public static QueuePage Queue(Json root)
    {
        var panel = root.First("playlistPanelRenderer");
        var songs = new List<Song>();
        foreach (var entry in panel["contents"].Array)
        {
            if (entry["playlistPanelVideoWrapperRenderer"].Exists)
            {
                // Signed in, an entry with both a video and a song carries the two; take the song.
                var versions = entry.Objects("playlistPanelVideoRenderer")
                    .Select(Rows.SongFromPanel).Where(s => s is not null).Select(s => s!).ToArray();
                var pick = versions.FirstOrDefault(v => !v.IsVideo) ?? versions.FirstOrDefault();
                if (pick is not null) songs.Add(pick);
            }
            else if (Rows.SongFromPanel(entry["playlistPanelVideoRenderer"]) is { } song)
            {
                songs.Add(song);
            }
        }
        var radio = panel.Objects("nextRadioContinuationData");
        var token = radio.Length > 0 ? radio[0]["continuation"].NonEmpty : null;
        var next = panel.Objects("nextContinuationData");
        token ??= next.Length > 0 ? next[0]["continuation"].NonEmpty : null;
        return new QueuePage([.. songs], token);
    }

    public static T[] DedupeById<T>(IEnumerable<T> items, Func<T, string> id) where T : notnull
    {
        var seen = new HashSet<string>();
        var result = new List<T>();
        foreach (var item in items)
        {
            if (seen.Add(id(item))) result.Add(item);
        }
        return [.. result];
    }
}
