using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Koda.Core.Discord;

/// <summary>The playing song, as much of it as a Discord card shows.</summary>
/// <param name="SongId">The YouTube video id.</param>
/// <param name="ArtistChannelId">The first linked artist's <c>UC…</c> channel, for a link to their page.</param>
/// <param name="Duration">Seconds; 0 while unknown.</param>
/// <param name="IsPlaying">The listener wants sound: true through loading and buffering, so a stall never flips the card.</param>
public sealed record DiscordNowPlaying(string SongId, string Title, string Artist, string? ArtistChannelId, string? Album,
    Uri? Artwork, double Duration, double Position, bool IsPlaying)
{
    public static DiscordNowPlaying Of(Song song, double duration, double position, bool isPlaying) => new(
        song.Id, song.Title, song.ArtistLine, song.Artists.FirstOrDefault()?.Id, song.Album?.Title,
        song.Artwork?.Resized(DiscordActivity.ArtworkPixels),
        duration > 0 ? duration : song.Duration ?? 0, position, isPlaying);
}

/// <summary>
/// A Rich Presence activity as Discord's <c>SET_ACTIVITY</c> takes it, the Mac's card:
/// <code>
///   Listening to Song title           &lt;- type 2 + status_display_type 2
///   Song title                        &lt;- details, opens the song
///   Artist                            &lt;- state, opens the artist
///   1:04 / 3:47                       &lt;- timestamps, while playing
///   [ Listen on YouTube Music ]
///   [ Get Koda ]
/// </code>
/// Discord drops a whole activity over one field outside its limits, so every string goes
/// through <see cref="Text"/> or <see cref="Link"/> on the way in. Ported from KodaCore
/// DiscordActivity.swift, with the same field names and limits.
/// </summary>
public sealed record DiscordActivity
{
    public const int TypeListening = 2;
    /// <summary>The member list reads "Listening to &lt;details&gt;" rather than the application's name.</summary>
    public const int StatusDisplayDetails = 2;
    public const int MinText = 2;
    public const int MaxText = 128;
    public const int MaxUrl = 256;
    public const int MaxImage = 300;
    /// <summary>Discord recommends artwork of at least this many pixels a side.</summary>
    public const int ArtworkPixels = 1024;
    public const string ReleasesUrl = "https://github.com/Ivorisnoob/Koda-Windows/releases";
    /// <summary>Not whitespace, so Discord does not trim it away again.</summary>
    private const char Pad = '⠀';

    public int? StatusDisplayType { get; init; } = StatusDisplayDetails;
    public required string Details { get; init; }
    public string? DetailsUrl { get; init; }
    public string? State { get; init; }
    public string? StateUrl { get; init; }
    /// <summary>Unix seconds, while playing with a known length.</summary>
    public (long Start, long End)? Timestamps { get; init; }
    public string? LargeImage { get; init; }
    public string? LargeText { get; init; }
    public string? LargeUrl { get; init; }
    public IReadOnlyList<(string Label, string Url)> Buttons { get; init; } = [];

    /// <summary>The card for <paramref name="song"/>, or null when it has no title to show.
    /// <paramref name="now"/> only feeds the timestamps.</summary>
    public static DiscordActivity? For(DiscordNowPlaying song, DateTimeOffset now)
    {
        if (Text(song.Title) is not { } title) return null;
        var artist = Text(song.Artist);
        var songUrl = IsYouTubeId(song.SongId) ? Link($"https://music.youtube.com/watch?v={song.SongId}") : null;
        (long, long)? timestamps = EndTime(song, now) is { } end
            ? ((long)Math.Round(end - song.Duration), (long)Math.Round(end))
            : null;
        // Discord fetches the image itself, so only a web URL will do.
        var image = Link(song.Artwork?.AbsoluteUri, MaxImage);
        var buttons = new List<(string, string)>();
        if (songUrl is not null) buttons.Add(("Listen on YouTube Music", songUrl));
        // Buttons are plain links and can't open Koda, so the label says where the tap goes.
        buttons.Add(("Get Koda", ReleasesUrl));
        return new DiscordActivity
        {
            Details = title,
            DetailsUrl = songUrl,
            State = artist,
            StateUrl = artist is null ? null : ArtistUrl(song),
            Timestamps = timestamps,
            LargeImage = image,
            // The album, or nothing: the title is already on the card.
            LargeText = image is null ? null : Text(song.Album),
            LargeUrl = image is null ? null : songUrl,
            Buttons = buttons,
        };
    }

    /// <summary>The same card with only the fields every Discord version has taken.</summary>
    public DiscordActivity Basic => this with { StatusDisplayType = null, DetailsUrl = null, StateUrl = null, LargeUrl = null };

    /// <summary>The card without its progress bar: what it shows, apart from time passing.</summary>
    public DiscordActivity Content => this with { Timestamps = null };

    /// <summary>Equal when every field is, the buttons compared by value.</summary>
    public bool Same(DiscordActivity? other) =>
        other is not null && this with { Buttons = [] } == other with { Buttons = [] } && Buttons.SequenceEqual(other.Buttons);

    /// <summary>When the progress bar runs out, in Unix seconds, or null when the card has none:
    /// paused, or a length not known yet.</summary>
    public static double? EndTime(DiscordNowPlaying song, DateTimeOffset now)
    {
        if (!song.IsPlaying || song.Duration <= 0) return null;
        // A position past the end (a last tick before the next song) never pushes the bar back.
        return now.ToUnixTimeMilliseconds() / 1000.0 + song.Duration - Math.Min(Math.Max(song.Position, 0), song.Duration);
    }

    /// <summary>The artist's channel when YouTube linked one; otherwise a search for the name.</summary>
    private static string? ArtistUrl(DiscordNowPlaying song)
    {
        if (song.ArtistChannelId is { } channel && channel.StartsWith("UC", StringComparison.Ordinal))
        {
            return Link($"https://music.youtube.com/channel/{channel}");
        }

        var name = song.Artist.Trim();
        return name.Length == 0 ? null : Link($"https://music.youtube.com/search?q={Uri.EscapeDataString(name)}");
    }

    /// <summary><paramref name="value"/> fitted to a text field: trimmed, cut with an ellipsis when too
    /// long, padded when too short (a song called "7" is a real title, and unpadded it takes the
    /// card down). Discord counts UTF-16 units; cutting by whole text elements never splits an emoji.</summary>
    public static string? Text(string? value, int limit = MaxText)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (text.Length > limit)
        {
            var kept = new StringBuilder();
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext() && kept.Length + elements.GetTextElement().Length <= limit - 1)
            {
                kept.Append(elements.GetTextElement());
            }

            text = kept.ToString().TrimEnd() + "…";
        }

        while (new System.Globalization.StringInfo(text).LengthInTextElements < MinText) text += Pad;
        return text;
    }

    /// <summary><paramref name="value"/> when it is a web link Discord will take, else null.</summary>
    public static string? Link(string? value, int limit = MaxUrl) =>
        value is not null && (value.StartsWith("https://", StringComparison.Ordinal) || value.StartsWith("http://", StringComparison.Ordinal))
            && value.Length <= limit
            ? value
            : null;

    /// <summary>A YouTube video id: 11 base64url characters.</summary>
    public static bool IsYouTubeId(string value) =>
        value.Length == 11 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["type"] = TypeListening, ["details"] = Details };
        if (StatusDisplayType is { } display) json["status_display_type"] = display;
        if (DetailsUrl is not null) json["details_url"] = DetailsUrl;
        if (State is not null) json["state"] = State;
        if (StateUrl is not null) json["state_url"] = StateUrl;
        if (Timestamps is { } t) json["timestamps"] = new JsonObject { ["start"] = t.Start, ["end"] = t.End };
        if (LargeImage is not null)
        {
            var assets = new JsonObject { ["large_image"] = LargeImage };
            if (LargeText is not null) assets["large_text"] = LargeText;
            if (LargeUrl is not null) assets["large_url"] = LargeUrl;
            json["assets"] = assets;
        }

        if (Buttons.Count > 0)
        {
            json["buttons"] = new JsonArray([.. Buttons.Select(b => (JsonNode)new JsonObject { ["label"] = b.Label, ["url"] = b.Url })]);
        }

        return json;
    }
}

/// <summary>Discord's local RPC wire: frames of an opcode and a length, both little-endian 32-bit,
/// then that many bytes of JSON.</summary>
public static class DiscordIpc
{
    public enum Opcode : uint
    {
        Handshake = 0,
        Frame = 1,
        Close = 2,
        Ping = 3,
        Pong = 4,
    }

    public const int Version = 1;
    /// <summary>No reply Discord sends is near this; anything larger is a broken stream.</summary>
    public const int MaxPayload = 64 * 1024;

    public static byte[] Frame(Opcode opcode, byte[] payload)
    {
        var data = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(data, 8);
        return data;
    }

    /// <summary>The opcode and payload length from a frame's first eight bytes.</summary>
    public static (uint Opcode, int Length) Header(ReadOnlySpan<byte> bytes) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(bytes), (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]));

    public static byte[] Handshake(string clientId) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["v"] = Version, ["client_id"] = clientId }.ToJsonString());

    /// <summary><c>SET_ACTIVITY</c>; a null activity takes the card down.</summary>
    public static byte[] SetActivity(DiscordActivity? activity, int pid, string nonce) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = pid, ["activity"] = activity?.ToJson() },
            ["nonce"] = nonce,
        }.ToJsonString());

    public enum ReplyKind
    {
        Ready,
        Success,
        Error,
        Other,
    }

    public readonly record struct Reply(ReplyKind Kind, string? Nonce = null, string? Message = null);

    public static Reply Read(byte[] payload)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return new Reply(ReplyKind.Other);
        }

        if (json is null) return new Reply(ReplyKind.Other);
        var evt = json["evt"]?.GetValueKind() == JsonValueKind.String ? (string?)json["evt"] : null;
        var nonce = json["nonce"]?.GetValueKind() == JsonValueKind.String ? (string?)json["nonce"] : null;
        if (evt == "READY") return new Reply(ReplyKind.Ready);
        if (evt == "ERROR")
        {
            var data = json["data"] as JsonObject;
            var code = data?["code"] is JsonValue c ? $" ({c})" : "";
            var message = data?["message"]?.GetValueKind() == JsonValueKind.String ? (string?)data["message"] : null;
            return new Reply(ReplyKind.Error, nonce, (message ?? "Unknown error") + code);
        }

        return json["cmd"] is not null ? new Reply(ReplyKind.Success, nonce) : new Reply(ReplyKind.Other);
    }
}
