using System.Text;
using System.Text.Json.Nodes;
using Koda.Core.Discord;

namespace Koda.Core.Tests;

/// <summary>
/// The Discord card: what it shows, the limits Discord holds it to, the wire it travels on, and
/// when a frame is worth sending.
/// </summary>
public class DiscordTests
{
    private static DiscordNowPlaying Playing(string title = "Song", double position = 30, bool playing = true) =>
        new("dQw4w9WgXcQ", title, "Artist", "UCartist", "Album", new Uri("https://lh3.googleusercontent.com/x=w1024-h1024"),
            200, position, playing);

    [Fact]
    public void ThePlayingSongBecomesAListeningCard()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

        var card = DiscordActivity.For(Playing(), now)!;
        var json = card.ToJson();

        Assert.Equal(2, (int)json["type"]!);
        Assert.Equal("Song", (string)json["details"]!);
        Assert.Equal("https://music.youtube.com/watch?v=dQw4w9WgXcQ", (string)json["details_url"]!);
        Assert.Equal("https://music.youtube.com/channel/UCartist", (string)json["state_url"]!);
        Assert.Equal(1_000_000 - 30, (long)json["timestamps"]!["start"]!);
        Assert.Equal(1_000_000 + 170, (long)json["timestamps"]!["end"]!);
        Assert.Equal("Album", (string)json["assets"]!["large_text"]!);
        Assert.Equal(2, json["buttons"]!.AsArray().Count);
    }

    [Fact]
    public void APausedCardHasNoProgressBar()
    {
        var card = DiscordActivity.For(Playing(playing: false), DateTimeOffset.UtcNow)!;

        Assert.Null(card.Timestamps);
        Assert.Null(card.ToJson()["timestamps"]);
    }

    [Theory]
    [InlineData("7", 2)]
    [InlineData("  ", -1)]
    public void ShortTitlesArePaddedAndEmptyOnesShowNothing(string title, int length)
    {
        var text = DiscordActivity.Text(title);

        if (length < 0) Assert.Null(text);
        else Assert.Equal(length, text!.Length);
    }

    [Fact]
    public void LongTitlesAreCutWithAnEllipsisWithoutSplittingAnEmoji()
    {
        var title = new string('a', 126) + "\U0001F3B5\U0001F3B5";

        var text = DiscordActivity.Text(title)!;

        Assert.True(text.Length <= DiscordActivity.MaxText);
        Assert.EndsWith("…", text);
        Assert.False(char.IsHighSurrogate(text[^2]));
    }

    [Fact]
    public void AnArtistWithoutAChannelLinksToASearch()
    {
        var song = Playing() with { ArtistChannelId = null, Artist = "Sigur Rós" };

        var card = DiscordActivity.For(song, DateTimeOffset.UtcNow)!;

        Assert.Equal("https://music.youtube.com/search?q=Sigur%20R%C3%B3s", card.StateUrl);
    }

    [Fact]
    public void TheBasicCardDropsTheNewerFields()
    {
        var card = DiscordActivity.For(Playing(), DateTimeOffset.UtcNow)!.Basic.ToJson();

        Assert.Null(card["status_display_type"]);
        Assert.Null(card["details_url"]);
        Assert.Null(card["state_url"]);
        Assert.Null(card["assets"]!["large_url"]);
    }

    [Fact]
    public void FramesAreAnOpcodeALengthAndTheJson()
    {
        var payload = DiscordIpc.Handshake("123");
        var frame = DiscordIpc.Frame(DiscordIpc.Opcode.Handshake, payload);

        var (opcode, length) = DiscordIpc.Header(frame);

        Assert.Equal(0u, opcode);
        Assert.Equal(payload.Length, length);
        Assert.Equal("""{"v":1,"client_id":"123"}""", Encoding.UTF8.GetString(frame, 8, length));
    }

    [Fact]
    public void ANullActivityIsWrittenOutToTakeTheCardDown()
    {
        var json = JsonNode.Parse(DiscordIpc.SetActivity(null, 42, "n"))!;

        Assert.True(json["args"]!.AsObject().ContainsKey("activity"));
        Assert.Null(json["args"]!["activity"]);
    }

    [Theory]
    [InlineData("""{"evt":"READY"}""", DiscordIpc.ReplyKind.Ready)]
    [InlineData("""{"cmd":"SET_ACTIVITY","nonce":"n"}""", DiscordIpc.ReplyKind.Success)]
    [InlineData("""{"evt":"ERROR","nonce":"n","data":{"code":4000,"message":"bad"}}""", DiscordIpc.ReplyKind.Error)]
    [InlineData("""not json""", DiscordIpc.ReplyKind.Other)]
    public void RepliesAreRead(string payload, DiscordIpc.ReplyKind kind) =>
        Assert.Equal(kind, DiscordIpc.Read(Encoding.UTF8.GetBytes(payload)).Kind);

    [Fact]
    public async Task SteadyPlaybackSendsOneFrameAndAPauseNeverPutsACardUp()
    {
        var discord = new FakeDiscord();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var presence = new DiscordPresence(() => Task.FromResult<IDiscordConnection?>(discord), clock: () => now);

        // Restored paused at launch: nothing goes up.
        presence.Update(true, Playing(playing: false));
        await Settle();
        Assert.Empty(discord.Sent);

        presence.Update(true, Playing());
        await Settle();
        for (var i = 1; i <= 10; i++)
        {
            now = now.AddSeconds(0.5);
            presence.Update(true, Playing(position: 30 + i * 0.5));
            await Settle();
        }

        Assert.Single(discord.Sent);
        Assert.Equal(DiscordPresence.Phase.Showing, presence.Status);

        // A seek moves the bar's end: worth a frame, once the push interval has passed.
        now = now.AddSeconds(10);
        presence.Update(true, Playing(position: 150));
        await Settle();
        Assert.Equal(2, discord.Sent.Count);

        // Turned off: the card comes down.
        presence.Update(false, Playing(position: 150));
        await Settle();
        Assert.Null(discord.Sent[^1]);
        Assert.Equal(DiscordPresence.Phase.Off, presence.Status);
        presence.Dispose();
    }

    private static async Task Settle() => await Task.Delay(60);

    private sealed class FakeDiscord : IDiscordConnection
    {
        public List<DiscordActivity?> Sent { get; } = [];

        public Task<DiscordSendResult> SetActivityAsync(DiscordActivity? activity)
        {
            lock (Sent) Sent.Add(activity);
            return Task.FromResult(DiscordSendResult.Accepted);
        }

        public Task<bool> IsAliveAsync() => Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
