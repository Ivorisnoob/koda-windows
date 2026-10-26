using Koda.Core;
using Koda.Core.Parsing;

namespace Koda.Core.Tests;

/// <summary>
/// The account layer: the cookies a sign-in leaves, the signature that proves them, the signed-in
/// pages, the mirror that keeps a library and its account in step, and YouTube history pings.
/// </summary>
public class AccountTests
{
    // MARK: Cookies

    private static readonly YouTubeCookies.Cookie Sid = new("SID", "sid");
    private static readonly YouTubeCookies.Cookie Sapisid = new("SAPISID", "sapisid");

    private static YouTubeCookies SignedInCookies(string sapisid = "sapisid") => new(
    [
        new("SID", "sid"),
        new("HSID", "hsid"),
        new("SSID", "ssid"),
        new("APISID", "apisid"),
        new("SAPISID", sapisid),
    ]);

    [Fact]
    public void AHeaderParsesWithoutTrippingOverEqualsInValues()
    {
        var cookies = YouTubeCookies.FromHeader("Cookie: SID=abc==; SAPISID=xyz; __Secure-3PAPISID=q=1");

        Assert.Equal("abc==", cookies["SID"]);
        Assert.Equal("xyz", cookies["SAPISID"]);
        Assert.Equal("q=1", cookies["__Secure-3PAPISID"]);
        Assert.Equal("SID=abc==; SAPISID=xyz; __Secure-3PAPISID=q=1", cookies.Header);
    }

    [Fact]
    public void OnlyYouTubeCookiesThatHaveNotExpiredAreTheSession()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var cookies = YouTubeCookies.FromWebView(
        [
            new("SID", "sid", ".youtube.com", null),
            new("SAPISID", "youtube", ".youtube.com", null),
            new("SAPISID", "google", ".google.com", null),
            new("SAPISID", "host", "music.youtube.com", null),
            new("HSID", "hsid", "music.youtube.com", null),
            new("SSID", "old", ".youtube.com", now.AddDays(-1)),
            new("APISID", "apisid", "youtube.com", null),
        ], now);

        // The domain-wide cookie wins over the host's; Google's SAPISID is not YouTube's.
        Assert.Equal("youtube", cookies["SAPISID"]);
        Assert.Null(cookies["SSID"]);
        Assert.Equal(new[] { "SID", "SAPISID", "APISID", "HSID" }, cookies.Cookies.Select(c => c.Name));
        Assert.True(cookies.LooksSignedIn);
        // SSID expired, so the five a real sign-in sets are not all here.
        Assert.False(cookies.HasSignInCookies);
    }

    [Fact]
    public void TheFiveSignInCookiesAreMatchedByExactName()
    {
        Assert.True(SignedInCookies().HasSignInCookies);

        // A jar with only the look-alikes is not a session.
        var impostor = new YouTubeCookies(
        [
            new("__Secure-3PSID", "a"),
            new("__Secure-3PAPISID", "b"),
        ]);
        Assert.False(impostor.HasSignInCookies);
        Assert.False(impostor.LooksSignedIn);

        // The partitioned twin signs when the legacy SAPISID is missing.
        var partitioned = new YouTubeCookies([new("SID", "sid"), new("__Secure-3PAPISID", "p")]);
        Assert.True(partitioned.LooksSignedIn);
        Assert.False(partitioned.HasSignInCookies);
    }

    [Fact]
    public void SetCookieHeadersSplitOnlyWhereACookieStarts()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var parsed = SetCookie.Parse(
        [
            "SID=new; Expires=Wed, 21 Oct 2026 07:28:00 GMT; Domain=.youtube.com; Path=/; Secure; HttpOnly, "
            + "SAPISID=abc; Domain=.youtube.com; Path=/",
            "YSC=; Max-Age=0; Domain=.youtube.com",
            "SID=google; Domain=.google.com",
        ], now);

        // The Expires comma stays inside SID's header, the google.com cookie is dropped, and the
        // deletion is reported as one (the session's merge is what leaves it out).
        Assert.Equal(3, parsed.Count);
        Assert.Equal(new SetCookie("SID", "new", false), parsed[0]);
        Assert.Equal(new SetCookie("SAPISID", "abc", false), parsed[1]);
        Assert.Equal(new SetCookie("YSC", "", true), parsed[2]);
    }

    [Fact]
    public void ARotationIsTakenOnlyForANameTheSessionHolds()
    {
        var cookies = SignedInCookies();
        var sent = cookies;
        var updates = SetCookie.Parse(
        [
            "SAPISID=fresh; Domain=.youtube.com",
            "YSC=junk; Domain=.youtube.com",
            "__Secure-3PSIDTS=stamp; Domain=.youtube.com",
            "SSID=; Max-Age=0; Domain=.youtube.com",
        ]);

        var merged = cookies.Merging(updates, sent);

        Assert.Equal("fresh", merged["SAPISID"]);
        Assert.Null(merged["YSC"]);
        // A rotating name the session did not have yet joins it.
        Assert.Equal("stamp", merged["__Secure-3PSIDTS"]);
        // A deletion never removes a cookie: YouTube's verdict decides the session.
        Assert.Equal("ssid", merged["SSID"]);
    }

    [Fact]
    public void AnOlderResponseCannotRollBackANewerRotation()
    {
        var original = SignedInCookies("first");
        var rotated = original.Merging(SetCookie.Parse(["SAPISID=second; Domain=.youtube.com"]), original);
        var stale = SetCookie.Parse(["SAPISID=stale; Domain=.youtube.com"]);

        var after = rotated.Merging(stale, original);

        Assert.Equal("second", after["SAPISID"]);
        Assert.Equal(rotated, after);
    }

    // MARK: Signature

    [Fact]
    public void TheSignatureIsTheUnixTimeAndTheSapisidHashed()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_000_000_000);

        var authorization = SapisidHash.Authorization("abc", "https://music.youtube.com", at);

        Assert.Equal("SAPISIDHASH 1000000000_a408d72b6d376bb0bd79a76729bf8b4a3199efef", authorization);
    }

    [Fact]
    public void AccountHeadersCarryTheCookiesAndTheOrigin()
    {
        var cookies = SignedInCookies("abc");
        var headers = SapisidHash.Headers(cookies, at: DateTimeOffset.FromUnixTimeSeconds(1_000_000_000));

        Assert.Equal(cookies.Header, headers["Cookie"]);
        Assert.Equal("https://music.youtube.com", headers["X-Origin"]);
        Assert.Equal("0", headers["X-Goog-AuthUser"]);
        Assert.StartsWith("SAPISIDHASH 1000000000_", headers["Authorization"]);
    }

    [Fact]
    public void WithoutASapisidNothingIsSigned()
    {
        var cookies = new YouTubeCookies([new("SID", "sid")]);
        var headers = SapisidHash.Headers(cookies);

        Assert.Equal("SID=sid", headers["Cookie"]);
        Assert.False(headers.ContainsKey("Authorization"));
    }

    // MARK: Sessions

    [Fact]
    public void SigningInStartsAGenerationAndPublishesTheCookies()
    {
        var sessions = new AccountSessions();
        var id = Guid.NewGuid();
        var saved = new List<YouTubeCookies>();
        sessions.CookiesChanged += saved.Add;
        var verdicts = new List<AccountVerdict>();
        sessions.VerdictChanged += verdicts.Add;

        sessions.SignIn(new AccountSession(id, SignedInCookies()));
        Assert.Equal("sapisid", sessions.Active!.Cookies["SAPISID"]);
        Assert.Single(saved);

        var ticket = sessions.Ticket()!;
        sessions.Received(["SAPISID=rotated; Domain=.youtube.com; Path=/"], ticket);
        Assert.Equal("rotated", sessions.CookiesFor(id)!["SAPISID"]);
        Assert.Equal(2, saved.Count);

        sessions.Note(false, ticket);
        Assert.False(sessions.SignedIn(id));
        Assert.Equal(new[] { new AccountVerdict(id, false) }, verdicts);

        // A response from the sign-in before the next one writes nothing anywhere.
        var oldTicket = sessions.Ticket()!;
        sessions.SignIn(new AccountSession(id, SignedInCookies("new")));
        var savedCount = saved.Count;
        sessions.Received(["SAPISID=stale; Domain=.youtube.com"], oldTicket);
        sessions.Note(true, oldTicket);
        Assert.Equal("new", sessions.CookiesFor(id)!["SAPISID"]);
        Assert.Equal(savedCount, saved.Count);
        Assert.Null(sessions.SignedIn(id));
    }

    [Fact]
    public void ActivatingKeepsRotationsButAFirstSignInTakesTheStore()
    {
        var sessions = new AccountSessions();
        var id = Guid.NewGuid();
        var stored = SignedInCookies("stored");

        sessions.Activate(new AccountSession(id, stored));
        var ticket = sessions.Ticket()!;
        sessions.Received(["SAPISID=rotated; Domain=.youtube.com"], ticket);
        sessions.Deactivate();
        Assert.Null(sessions.Active);

        // The app's copy still says "stored"; this run's rotation must win.
        sessions.Activate(new AccountSession(id, stored));
        Assert.Equal("rotated", sessions.Active!.Cookies["SAPISID"]);
        Assert.StartsWith($"{id}#", sessions.CacheScope);
    }

    // MARK: Signed-in pages

    [Fact]
    public void TheLoggedInVerdictIsYouTubeSWord()
    {
        Assert.True(AccountPages.SignedIn(Json.Parse("""
            {"responseContext":{"serviceTrackingParams":[{"params":[{"key":"logged_in","value":"1"}]}]}}
            """)));
        Assert.False(AccountPages.SignedIn(Json.Parse("""
            {"responseContext":{"serviceTrackingParams":[{"params":[{"key":"logged_in","value":"0"}]}]}}
            """)));
        Assert.Null(AccountPages.SignedIn(Json.Parse("""{"responseContext":{}}""")));
    }

    [Fact]
    public void TheAccountMenuNamesTheAccount()
    {
        var root = Json.Parse("""
        {
          "actions": [ { "openPopupAction": { "popup": { "multiPageMenuRenderer": {
            "header": { "activeAccountHeaderRenderer": {
              "accountName": { "runs": [ { "text": "Ivor" } ] },
              "channelHandle": { "runs": [ { "text": "@ivor" } ] },
              "accountPhoto": { "thumbnails": [
                { "url": "https://example.com/small.jpg", "width": 88, "height": 88 },
                { "url": "https://example.com/big.jpg", "width": 176, "height": 176 }
              ] }
            } },
            "sections": [ { "compactLinkRenderer": {
              "navigationEndpoint": { "browseEndpoint": { "browseId": "UCchannel" } }
            } } ]
          } } } } ],
          "responseContext": { "mainAppWebResponseContext": { "datasyncId": "sync-1" } }
        }
        """);

        var info = AccountPages.AccountInfo(root);

        Assert.NotNull(info);
        Assert.Equal("Ivor", info.Name);
        Assert.Equal("@ivor", info.Handle);
        Assert.Equal("UCchannel", info.ChannelId);
        Assert.Equal("https://example.com/big.jpg", info.Photo?.Url.ToString());
        Assert.Equal("sync-1", info.DatasyncId);
    }

    [Fact]
    public void LibraryPagesReadRowsAndGrids()
    {
        var root = Json.Parse("""
        {
          "musicShelfRenderer": {
            "contents": [ { "musicResponsiveListItemRenderer": {
              "playlistItemData": { "videoId": "vid1" },
              "flexColumns": [
                { "musicResponsiveListItemFlexColumnRenderer": { "text": { "runs": [ { "text": "Song One" } ] } } },
                { "musicResponsiveListItemFlexColumnRenderer": { "text": { "runs": [
                  { "text": "Artist One", "navigationEndpoint": { "browseEndpoint": {
                    "browseId": "UC1",
                    "browseEndpointContextSupportedConfigs": { "browseEndpointContextMusicConfig": { "pageType": "MUSIC_PAGE_TYPE_ARTIST" } }
                  } } },
                  { "text": " • " },
                  { "text": "Album One", "navigationEndpoint": { "browseEndpoint": {
                    "browseId": "MPRE1",
                    "browseEndpointContextSupportedConfigs": { "browseEndpointContextMusicConfig": { "pageType": "MUSIC_PAGE_TYPE_ALBUM" } }
                  } } }
                ] } } }
              ],
              "fixedColumns": [ { "musicResponsiveListItemFixedColumnRenderer": { "text": { "simpleText": "3:30" } } } ]
            } } ]
          },
          "gridRenderer": { "items": [ { "musicTwoRowItemRenderer": {
            "title": { "runs": [ { "text": "Album One" } ] },
            "subtitle": { "runs": [ { "text": "Artist One" }, { "text": " • " }, { "text": "2024" } ] },
            "navigationEndpoint": { "browseEndpoint": {
              "browseId": "MPRE1",
              "browseEndpointContextSupportedConfigs": { "browseEndpointContextMusicConfig": { "pageType": "MUSIC_PAGE_TYPE_ALBUM" } }
            } }
          } } ] }
        }
        """);

        var items = AccountPages.LibraryItems(root);

        var song = Assert.IsType<MusicItem.SongRecord>(items[0]).Value;
        Assert.Equal("vid1", song.Id);
        Assert.Equal("Song One", song.Title);
        Assert.Equal("Artist One", song.ArtistLine);
        Assert.Equal(210, song.Duration);
        var album = Assert.IsType<MusicItem.AlbumRecord>(items[1]).Value;
        Assert.Equal("MPRE1", album.Id);
        Assert.Equal(2024, album.Year);
    }

    [Fact]
    public void ALibraryPageKeepsItsOwnContinuationToken()
    {
        var legacy = Json.Parse("""
            {"musicShelfRenderer":{"contents":[],"continuations":[{"nextContinuationData":{"continuation":"tok1"}}]}}
            """);
        var modern = Json.Parse("""
            {"gridRenderer":{"items":[{"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"tok2"}}}}]}}
            """);
        var elsewhere = Json.Parse("""
            {"musicShelfRenderer":{"contents":[]},"sectionListRenderer":{"continuations":[{"nextContinuationData":{"continuation":"wrong"}}]}}
            """);

        Assert.Equal("tok1", AccountPages.LibraryContinuation(legacy));
        Assert.Equal("tok2", AccountPages.LibraryContinuation(modern));
        Assert.Null(AccountPages.LibraryContinuation(elsewhere));
    }

    // MARK: The mirror

    private static Song Song(string id) => new(id, $"Song {id}", "Artist", [new ArtistRef("UC1", "Artist")],
        null, 200, null, false, null);

    private static AlbumSummary Album(string id) => new(id, $"Album {id}", "Artist",
        [new ArtistRef("UC1", "Artist")], 2024, ReleaseKind.Album, null);

    [Fact]
    public void AChangeReplacesAnOlderOneToTheSameItem()
    {
        var mirror = new AccountMirror();
        var like = new AccountChange(AccountChangeKind.Liked, "vid", true);
        var unlike = new AccountChange(AccountChangeKind.Liked, "vid", false);

        mirror.Queue(like);
        mirror.Queue(unlike);

        Assert.Equal(new[] { unlike }, mirror.Pending);
        mirror.Sent(like);
        Assert.Equal(new[] { unlike }, mirror.Pending);
        mirror.Sent(unlike);
        Assert.Empty(mirror.Pending);
    }

    [Fact]
    public void OnlyWhatTheAccountHadAndNoLongerHasIsGone()
    {
        var mirror = new AccountMirror
        {
            Liked = ["gone", "kept", "likedOffline"],
        };
        mirror.Queue(new AccountChange(AccountChangeKind.Liked, "likedOffline", true));
        mirror.Queue(new AccountChange(AccountChangeKind.Liked, "unlikedOffline", false));

        var gone = mirror.Gone(AccountChangeKind.Liked, ["kept", "unlikedOffline"]);

        Assert.Equal(new[] { "gone" }, gone);
    }

    [Fact]
    public void AReconcileMergesTheAccountAheadAndDropsOnlyWhatItRemoved()
    {
        var library = new Library(null);
        library.ToggleLike(Song("local"));
        library.ToggleSave(Album("albumLocal"));
        var changed = 0;
        library.Changed += () => changed++;

        var mirror = new AccountMirror { Liked = ["gone"] };
        mirror.Queue(new AccountChange(AccountChangeKind.Liked, "unlikedOffline", false));
        mirror.Queue(new AccountChange(AccountChangeKind.Liked, "likedOffline", true));

        mirror.Reconcile(library, new AccountLibrary(
            [Song("a"), Song("b"), Song("unlikedOffline")],
            [Album("albumA")],
            [],
            []));

        Assert.Equal(new[] { "a", "b", "local" }, library.Liked.Select(s => s.Id));
        Assert.Equal(new[] { "albumA", "albumLocal" }, library.Albums.Select(a => a.Id));
        Assert.Equal(new[] { "a", "b", "unlikedOffline" }, mirror.Liked);
        Assert.Equal(new[] { "albumA" }, mirror.Albums);
        // A sync is not a listener's change, and the unsent changes are still waiting.
        Assert.Equal(0, changed);
        Assert.Equal(2, mirror.Pending.Count);
    }

    [Fact]
    public void AMergeNeverRemovesAnythingAndNeverAnnouncesItself()
    {
        var library = new Library(null);
        library.ToggleLike(Song("kept"));
        var changed = 0;
        library.Changed += () => changed++;

        library.MergeAccount([Song("kept"), Song("new")], [], [], []);

        Assert.Equal(new[] { "new", "kept" }, library.Liked.Select(s => s.Id));
        Assert.Equal(0, changed);
    }

    // MARK: History

    [Fact]
    public void ThePingUrlKeepsItsOwnParametersAndReplacesThePlayS()
    {
        var url = HistoryPing.TrackingUrl(
            "https://s.youtube.com/api/stats/playback?ns=yt&cpn=old&ver=1&c=WEB&docid=vid", "newcpn");

        Assert.NotNull(url);
        var query = Query(url);
        Assert.Equal("newcpn", query["cpn"]);
        Assert.Equal("2", query["ver"]);
        Assert.Equal("WEB_REMIX", query["c"]);
        Assert.Equal("yt", query["ns"]);
        Assert.Equal("vid", query["docid"]);
        Assert.Single(url.Query.Split('&'), pair => pair.StartsWith("cpn=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://s.youtube.com/api/stats/playback")]
    [InlineData("https://evil.example.com/api/stats/playback")]
    [InlineData("not a url")]
    public void APingGoesToYouTubeOverHttpsOnly(string baseUrl) =>
        Assert.Null(HistoryPing.TrackingUrl(baseUrl, "cpn"));

    [Fact]
    public void ThePlayerPayloadCarriesTheSignatureTimestamp()
    {
        var payload = HistoryPing.PlayerPayload("vid", "cpn", 12345);

        Assert.Equal("vid", payload["videoId"]!.GetValue<string>());
        Assert.Equal("cpn", payload["cpn"]!.GetValue<string>());
        Assert.Equal(12345,
            payload["playbackContext"]!["contentPlaybackContext"]!["signatureTimestamp"]!.GetValue<int>());
    }

    [Fact]
    public void APlaybackNonceIsSixteenUrlSafeCharacters()
    {
        var cpn = HistoryPing.Cpn();

        Assert.Equal(16, cpn.Length);
        Assert.All(cpn, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.NotEqual(cpn, HistoryPing.Cpn());
    }

    [Fact]
    public void ThePlayerScriptNamesThePlayerAndItsBuildDay()
    {
        Assert.Equal("abcd1234",
            PlayerSignatureTimestamp.PlayerIdIn("""var p="\/s\/player\/abcd1234\/player_ias.vflset\/en_US\/base.js";"""));
        Assert.Equal("abcd1234",
            PlayerSignatureTimestamp.PlayerIdIn("""var p="/s/player/abcd1234/player_ias.vflset/en_US/base.js";"""));
        Assert.Null(PlayerSignatureTimestamp.PlayerIdIn("nothing here"));

        Assert.Equal(12345, PlayerSignatureTimestamp.SignatureTimestampIn("x signatureTimestamp:12345 y"));
        Assert.Equal(67890, PlayerSignatureTimestamp.SignatureTimestampIn("signatureTimestamp=67890"));
        Assert.Equal(1234567, PlayerSignatureTimestamp.SignatureTimestampIn("signatureTimestamp:\"1234567\""));
        Assert.Null(PlayerSignatureTimestamp.SignatureTimestampIn("signatureTimestamp:123"));
    }

    [Fact]
    public void TheEstimateIsTodayLessAFortnight()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000_000);

        Assert.Equal(1_000_000_000 / 86400 - 14, PlayerSignatureTimestamp.Estimate(now));
    }

    [Fact]
    public void APlayCountsOnceAfterFifteenSecondsOrHalfAShortSong()
    {
        var threshold = new HeardThreshold();
        var play = Guid.NewGuid();

        Assert.False(threshold.Add(10, play, 200));
        Assert.True(threshold.Add(5, play, 200));
        Assert.False(threshold.Add(5, play, 200));

        // A new occurrence starts counting again.
        var shortPlay = Guid.NewGuid();
        Assert.False(threshold.Add(4.9, shortPlay, 10));
        Assert.True(threshold.Add(0.1, shortPlay, 10));

        // A song with no duration is treated as thirty seconds: fifteen heard.
        var unknown = Guid.NewGuid();
        Assert.False(threshold.Add(14, unknown, null));
        Assert.True(threshold.Add(1, unknown, null));

        // Skips and pauses add nothing.
        Assert.False(threshold.Add(0, Guid.NewGuid(), 200));
        Assert.False(threshold.Add(-5, Guid.NewGuid(), 200));
    }

    private static Dictionary<string, string> Query(Uri url)
    {
        var query = new Dictionary<string, string>();
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0) query[Uri.UnescapeDataString(pair)] = "";
            else query[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        return query;
    }
}
