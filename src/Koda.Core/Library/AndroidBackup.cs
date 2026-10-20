using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Koda.Core;

/// <summary>
/// What Koda can take from a Koda for Android backup: liked songs, the listening history,
/// searches, the playlists made on the phone and the albums and playlists saved there.
/// The file is a zip (manifest.json, profile-data.json keyed by profile,
/// files/playlists/&lt;id&gt;.json, typed preferences.json). Ported from AndroidBackup.swift;
/// .NET's ZipArchive stands in for the Swift ZipReader, with the same size caps.
/// </summary>
public sealed class AndroidBackup
{
    public sealed record Play(Song Song, DateTimeOffset Date, double Seconds);

    public sealed record Playlist(string Name, DateTimeOffset Created, Song[] Songs);

    public sealed class ReadException(string message, bool newerVersion = false) : Exception(message)
    {
        public bool NewerVersion { get; } = newerVersion;
    }

    public Song[] Liked { get; private set; } = [];
    /// <summary>Oldest first.</summary>
    public Play[] Plays { get; private set; } = [];
    public string[] Searches { get; private set; } = [];
    public Playlist[] Playlists { get; private set; } = [];
    public AlbumSummary[] Albums { get; private set; } = [];
    public PlaylistSummary[] SavedPlaylists { get; private set; } = [];
    public DateTimeOffset? Created { get; private set; }

    private const int KnownVersion = 2;
    private const long MaxEntryBytes = 32 << 20;
    private const long MaxTotalBytes = 256 << 20;

    public static AndroidBackup Read(byte[] data)
    {
        var files = Unzip(data) ?? throw NotABackup();
        if (Object(files.GetValueOrDefault("manifest.json")) is not { } manifest || Str(manifest["format"]) != "koda-backup")
            throw NotABackup();
        var version = (int)(Num(manifest["formatVersion"]) ?? 0);
        if (version > KnownVersion)
            throw new ReadException("This backup was made by a newer Koda for Android than Koda here can read.", newerVersion: true);

        var backup = new AndroidBackup();
        if (Num(manifest["createdAt"]) is { } created && created > 0)
            backup.Created = DateTimeOffset.FromUnixTimeMilliseconds((long)created);

        // Liked songs, history and searches: from the profile in use when the backup was made
        // (version 2), or from the device-wide copies an older backup kept.
        string? likedJson, historyJson, searchText;
        if (version >= 2 && Object(files.GetValueOrDefault("profile-data.json")) is { Count: > 0 } profiles)
        {
            var active = Str(Object(files.GetValueOrDefault("profiles.json"))?["activeProfileId"]);
            var profile = (active is not null ? profiles[active] as JsonObject : null) ?? profiles.First().Value as JsonObject;
            likedJson = Str(profile?["likedSongs"]);
            historyJson = Str(profile?["playHistory"]);
            searchText = Str(profile?["searchHistory"]);
        }
        else
        {
            likedJson = Text(files.GetValueOrDefault("files/liked_songs_meta.json"));
            historyJson = Text(files.GetValueOrDefault("files/play_history.json"));
            searchText = Str(Object(files.GetValueOrDefault("preferences.json"))?["search_history"]?["history_list"]?["v"]);
        }

        backup.Liked = [.. Array(likedJson).Select(SongOf).OfType<Song>()];
        backup.Plays = [.. Array(historyJson).Select(PlayOf).OfType<Play>().OrderBy(p => p.Date)];
        backup.Searches = (searchText ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);

        var playlists = new List<Playlist>();
        foreach (var (path, contents) in files)
        {
            if (!path.StartsWith("files/playlists/") || !path.EndsWith(".json")) continue;
            if (Object(contents) is not { } playlist || Str(playlist["name"]) is not { } name) continue;
            var songs = (playlist["songs"] as JsonArray ?? []).Select(SongOf).OfType<Song>().ToArray();
            var at = Num(playlist["createdAt"]) is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : DateTimeOffset.Now;
            playlists.Add(new Playlist(name, at, songs));
        }
        backup.Playlists = [.. playlists.OrderByDescending(p => p.Created)];

        var savedText = Str(Object(files.GetValueOrDefault("preferences.json"))?["saved_playlists"]?["playlists"]?["v"]);
        var albums = new List<AlbumSummary>();
        var saved = new List<PlaylistSummary>();
        foreach (var item in Array(savedText).OfType<JsonObject>())
        {
            if (Str(item["id"]) is not { Length: > 0 } id || Str(item["name"]) is not { } name) continue;
            var artwork = Str(item["thumbnail"]) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? new Artwork(uri) : null;
            var owner = Str(item["uploader"]) ?? "";
            // Koda opens albums by their release id; an album saved by its playlist id opens as a playlist.
            if (id.StartsWith("MPREb"))
                albums.Add(new AlbumSummary(id, name, owner, [], Num(item["releaseYear"]) is { } y ? (int)y : null, null, artwork));
            else
                saved.Add(new PlaylistSummary(id.StartsWith("VL") ? id[2..] : id, name, owner.Length == 0 ? null : owner, artwork));
        }
        backup.Albums = [.. albums];
        backup.SavedPlaylists = [.. saved];
        return backup;
    }

    private static ReadException NotABackup() => new("This file isn't a Koda for Android backup.");

    private static Dictionary<string, byte[]>? Unzip(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var files = new Dictionary<string, byte[]>();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                // A file someone picked isn't trusted: a zip claiming a huge size fails instead of exhausting memory.
                if (entry.Length > MaxEntryBytes || (total += entry.Length) > MaxTotalBytes) return null;
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                files[entry.FullName] = buffer.ToArray();
            }
            return files;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // MARK: Reading the pieces

    private static string? Text(byte[]? data) => data is null ? null : System.Text.Encoding.UTF8.GetString(data);

    private static JsonObject? Object(byte[]? data)
    {
        try
        {
            return data is null ? null : JsonNode.Parse(data) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A JSON array kept as a string inside another file, as Android stores them.</summary>
    private static JsonArray Array(string? text)
    {
        try
        {
            return text is null ? [] : JsonNode.Parse(text) as JsonArray ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static bool? Bool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>A song from YouTube, from Android's Song. Songs that were files on the phone are left
    /// out: kotlinx omits values equal to their default, and a song's default source is a local file.</summary>
    private static Song? SongOf(JsonNode? any)
    {
        if (any is not JsonObject json || Str(json["source"]) != "YOUTUBE"
            || Str(json["id"]) is not { } id || !IsVideoId(id) || Str(json["title"]) is not { } title) return null;
        var ms = Num(json["duration"]) ?? 0;
        var album = Str(json["albumId"]) is { } albumId ? new AlbumRef(albumId, Str(json["album"]) ?? "") : null;
        var artwork = Str(json["thumbnailUrl"]) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? new Artwork(uri) : null;
        return new Song(id, title, Str(json["artist"]) ?? "", [], album, ms > 0 ? ms / 1000 : null, artwork,
            Bool(json["isUpload"]) ?? false, Num(json["releaseYear"]) is { } y ? (int)y : null);
    }

    /// <summary>One play from Android's PlayHistoryEntry, whose default source is YouTube.</summary>
    private static Play? PlayOf(JsonNode? any)
    {
        if (any is not JsonObject json || (Str(json["source"]) ?? "YOUTUBE") != "YOUTUBE"
            || Str(json["songId"]) is not { } id || !IsVideoId(id) || Str(json["title"]) is not { } title
            || Num(json["timestamp"]) is not { } timestamp) return null;
        var ms = Num(json["duration"]) ?? 0;
        var album = Str(json["albumId"]) is { } albumId ? new AlbumRef(albumId, Str(json["album"]) ?? "") : null;
        var artwork = Str(json["thumbnailUrl"]) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? new Artwork(uri) : null;
        var song = new Song(id, title, Str(json["artist"]) ?? "", [], album, ms > 0 ? ms / 1000 : null, artwork, false, null);
        return new Play(song, DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp), Math.Max(0, ms / 1000));
    }

    private static bool IsVideoId(string id) =>
        id.Length == 11 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
