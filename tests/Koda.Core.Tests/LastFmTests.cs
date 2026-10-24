using System.Net;
using System.Text.Json.Nodes;
using Koda.Core.LastFm;

namespace Koda.Core.Tests;

/// <summary>
/// Last.fm: the request signature, what counts as a scrobble, the responses Koda reads, and a
/// play that is kept until Last.fm takes it.
/// </summary>
public class LastFmTests
{
    private static Song Song(string title = "Song", double? duration = 200, bool video = false, params ArtistRef[] artists) =>
        new("dQw4w9WgXcQ", title, artists.Length == 0 ? "Artist" : string.Join(", ", artists.Select(a => a.Name)),
            artists, new AlbumRef("MPREb_x", "Album"), duration, null, video, null);

    [Fact]
    public void TheSignatureSortsTheParametersAndLeavesOutFormat()
    {
        // md5("api_keyKEYmethodauth.getTokenSECRET"), as Last.fm's auth spec builds it.
        var parameters = new Dictionary<string, string>
        {
            ["method"] = "auth.getToken",
            ["api_key"] = "KEY",
            ["format"] = "json",
        };

        var signature = LastFmProtocol.Signature(parameters, "SECRET");

        var expected = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("api_keyKEYmethodauth.getTokenSECRET")));
        Assert.Equal(expected, signature);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("0123456789abcdef0123456789abcde", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    public void AKeyIsThirtyTwoHexDigits(string key, bool valid) =>
        Assert.Equal(valid, LastFmProtocol.IsCredential(key));

    [Theory]
    [InlineData(200, 99, false)]
    [InlineData(200, 100, true)]
    [InlineData(600, 239, false)]
    [InlineData(600, 240, true)]
    [InlineData(30, 30, false)]
    public void APlayCountsAtHalfTheSongOrFourMinutes(double duration, double heard, bool counts) =>
        Assert.Equal(counts, LastFmListeningClock.Qualifies(heard, duration));

    [Fact]
    public void TheClockQualifiesOncePerPlayAndNeverCountsASeek()
    {
        var clock = new LastFmListeningClock();
        var play = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // A seek reported as a long jump only ever adds what a tick could hold.
        Assert.False(clock.Add(90, play, 200, now));
        Assert.Equal(6, clock.Heard);

        var qualified = 0;
        for (var i = 0; i < 400; i++)
        {
            if (clock.Add(0.5, play, 200, now)) qualified++;
        }

        Assert.Equal(1, qualified);

        // The same occurrence started again (repeat one) is a new play.
        clock.Start(play, now);
        Assert.Equal(0, clock.Heard);
        Assert.False(clock.IsSubmitted);
    }

    [Fact]
    public void ASongIsFiledUnderItsFirstArtist()
    {
        var song = Song(artists: [new ArtistRef("UC1", "First"), new ArtistRef("UC2", "Second")]);

        Assert.Equal("First", LastFmPlay.ArtistOf(song));
        Assert.Equal("Artist", LastFmPlay.ArtistOf(Song()));
    }

    [Fact]
    public void VideosAndNamelessSongsAreNotScrobbled()
    {
        Assert.False(LastFm.LastFm.IsScrobblable("Artist", "Title", isVideo: true));
        Assert.False(LastFm.LastFm.IsScrobblable(" ", "Title", isVideo: false));
        Assert.True(LastFm.LastFm.IsScrobblable("Artist", "Title", isVideo: false));
    }

    [Fact]
    public void RecentTracksReadOneTrackOrMany()
    {
        var one = JsonNode.Parse("""
            { "recenttracks": { "track": {
                "name": "Song", "artist": { "#text": "Artist" }, "album": { "#text": "Album" },
                "image": [ { "#text": "http://small" }, { "#text": "https://large" } ],
                "@attr": { "nowplaying": "true" } } } }
            """)!.AsObject();
        var many = JsonNode.Parse("""
            { "recenttracks": { "track": [
                { "name": "A", "artist": { "#text": "X" }, "date": { "uts": "1700000000" } },
                { "name": "", "artist": { "#text": "skipped" } },
                { "name": "B", "artist": { "#text": "Y" }, "date": { "uts": "1700000100" } } ] } }
            """)!.AsObject();

        var now = Assert.Single(LastFmProtocol.RecentTracks(one));
        Assert.True(now.IsNowPlaying);
        Assert.Equal("https://large", now.Artwork?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("Album", now.Album);

        var tracks = LastFmProtocol.RecentTracks(many);
        Assert.Equal(["A", "B"], tracks.Select(t => t.Title));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), tracks[0].PlayedAt);
    }

    [Fact]
    public void TheProfileReadsNumbersSentAsText()
    {
        var root = JsonNode.Parse("""
            { "user": { "name": "listener", "realname": "", "country": "None", "playcount": "12345",
                        "registered": { "unixtime": "1400000000" }, "subscriber": "1" } }
            """)!.AsObject();

        var profile = LastFmProtocol.Profile(root);

        Assert.NotNull(profile);
        Assert.Equal(12345, profile!.PlayCount);
        Assert.Equal("", profile.Country);
        Assert.True(profile.IsSubscriber);
        Assert.Equal(2014, profile.Registered?.Year);
    }

    [Fact]
    public void AnIgnoredScrobbleSaysWhy()
    {
        var root = JsonNode.Parse("""
            { "scrobbles": { "scrobble": { "ignoredMessage": { "code": "1", "#text": "Artist name ignored" } } } }
            """)!.AsObject();

        Assert.Equal(1, LastFmProtocol.ScrobbleCode(root));
        Assert.Throws<LastFmException>(() => LastFmProtocol.ScrobbleCode(new JsonObject()));
    }

    [Theory]
    [InlineData(9, LastFmException.Reason.SessionRevoked)]
    [InlineData(14, LastFmException.Reason.NotApprovedYet)]
    [InlineData(15, LastFmException.Reason.SignInExpired)]
    [InlineData(29, LastFmException.Reason.RateLimited)]
    [InlineData(6, LastFmException.Reason.Api)]
    public void LastFmErrorsBecomeReasons(int code, LastFmException.Reason reason) =>
        Assert.Equal(reason, LastFmException.FromApi(code, "message").Kind);

    [Fact]
    public async Task APlayWaitsOfflineAndIsSentOnceLastFmTakesIt()
    {
        var directory = Path.Combine(Fixtures.Scratch, $"lastfm-{Guid.NewGuid():N}");
        var secrets = new MemorySecrets();
        var server = new FakeLastFm();
        // The five-minute wait after a failure, made instant so the test can come back online.
        var lastFm = new LastFm.LastFm(directory, secrets, server) { Cooldown = TimeSpan.Zero };

        // Signed in: a session, as auth.getSession leaves it.
        var url = await lastFm.BeginSignInAsync(new string('a', 32), new string('b', 32));
        Assert.Contains("token=TOKEN", url.Query);
        await lastFm.FinishSignInAsync();
        Assert.True(lastFm.IsConnected);
        Assert.Equal("listener", lastFm.User);

        var song = Song(duration: 200);
        var play = Guid.NewGuid();
        server.Offline = true;
        lastFm.Started(play, song);
        for (var i = 0; i < 200; i++) lastFm.Heard(0.5, play, song);

        // Offline: the play is kept, on disk too.
        await WaitAsync(() => lastFm.Error is not null || !lastFm.IsBusy);
        Assert.Single(lastFm.Queued);
        Assert.Single(new LastFm.LastFm(directory, secrets, server).Queued);

        server.Offline = false;
        await lastFm.RefreshAsync();
        await WaitAsync(() => lastFm.Queued.Length == 0);
        Assert.Empty(lastFm.Queued);
        Assert.Equal(1, server.Scrobbles);
        Assert.NotNull(lastFm.LastSent);
    }

    [Fact]
    public async Task TurningScrobblingOffDropsWhatWaits()
    {
        var directory = Path.Combine(Fixtures.Scratch, $"lastfm-off-{Guid.NewGuid():N}");
        var server = new FakeLastFm();
        var lastFm = new LastFm.LastFm(directory, new MemorySecrets(), server);
        await lastFm.BeginSignInAsync(new string('a', 32), new string('b', 32));
        await lastFm.FinishSignInAsync();
        server.Offline = true;
        var song = Song();
        var play = Guid.NewGuid();
        lastFm.Started(play, song);
        for (var i = 0; i < 220; i++) lastFm.Heard(0.5, play, song);
        await WaitAsync(() => !lastFm.IsBusy);

        lastFm.SetEnabled(false);

        Assert.Empty(lastFm.Queued);
        Assert.False(lastFm.CanScrobble);
    }

    [Fact]
    public void PausedHistoryRecordsNothing()
    {
        var lastFm = new LastFm.LastFm(Path.Combine(Fixtures.Scratch, $"lastfm-paused-{Guid.NewGuid():N}"), new MemorySecrets(), new FakeLastFm())
        {
            IsPaused = true,
        };
        var song = Song();
        var play = Guid.NewGuid();
        lastFm.Started(play, song);
        for (var i = 0; i < 400; i++) lastFm.Heard(0.5, play, song);

        Assert.Empty(lastFm.Queued);
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(20);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private string? _value;

        public string? Read() => _value;

        public void Write(string? value) => _value = value;
    }

    /// <summary>Last.fm's answers to the calls Koda makes, or no connection at all.</summary>
    private sealed class FakeLastFm : HttpMessageHandler
    {
        public bool Offline { get; set; }
        public int Scrobbles { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (Offline) throw new HttpRequestException("offline by test design");
            var query = request.Method == HttpMethod.Post
                ? await request.Content!.ReadAsStringAsync(cancellation)
                : request.RequestUri!.Query.TrimStart('?');
            var method = query.Split('&').Select(p => p.Split('=')).First(p => p[0] == "method")[1];
            var body = method switch
            {
                "auth.getToken" => """{ "token": "TOKEN" }""",
                "auth.getSession" => """{ "session": { "name": "listener", "key": "SESSION" } }""",
                "track.scrobble" => Scrobble(),
                "track.updateNowPlaying" => """{ "nowplaying": {} }""",
                "user.getInfo" => """{ "user": { "name": "listener", "playcount": "1" } }""",
                "user.getRecentTracks" => """{ "recenttracks": { "track": [] } }""",
                _ => """{ "error": 3, "message": "Invalid method" }""",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }

        private string Scrobble()
        {
            Scrobbles++;
            return """{ "scrobbles": { "scrobble": { "ignoredMessage": { "code": "0" } } } }""";
        }
    }
}
