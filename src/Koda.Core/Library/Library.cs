using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Koda.Core;

/// <summary>A playlist the listener made.</summary>
public sealed record UserPlaylist(Guid Id, string Name, Song[] Songs, DateTimeOffset Created, DateTimeOffset Modified)
{
    public static UserPlaylist Create(string name, Song[] songs, DateTimeOffset created) =>
        new(Guid.NewGuid(), name, songs, created, created);

    public double Duration => Songs.Sum(s => s.Duration ?? 0);
}

/// <summary>How much one song has been played.</summary>
public sealed record SongStats(Song Song, int Plays, double Seconds, DateTimeOffset FirstPlayed, DateTimeOffset LastPlayed)
{
    public static SongStats New(Song song, DateTimeOffset at) => new(song, 0, 0, at, at);
}

/// <summary>One play, with when it happened: the dated history behind the History page.</summary>
public sealed record PlayEvent(Guid Id, Song Song, DateTimeOffset Date)
{
    public static PlayEvent New(Song song, DateTimeOffset at) => new(Guid.NewGuid(), song, at);
}

/// <summary>What an Android import added, to tell the listener.</summary>
public sealed record AndroidImportSummary(int LikedSongs = 0, int Plays = 0, int Playlists = 0, int Saved = 0, int Searches = 0)
{
    public bool IsEmpty => LikedSongs + Plays + Playlists + Saved + Searches == 0;
}

public sealed class LibraryBackupException() : Exception("This file isn't a Koda library backup.");

/// <summary>
/// What the listener keeps: songs they liked, albums, playlists
/// and artists they saved, playlists they made, what they played and searched,
/// and how long they listened. One JSON file, written after each change.
/// Ported from KodaCore Library.swift. The Swift class is @MainActor; this one
/// takes a lock instead, so it may be called from any thread, and raises
/// <see cref="Changed"/> after each change (off the caller's lock).
/// </summary>
public sealed class Library
{
    private const int RecentLimit = 200;
    private const int SearchLimit = 20;
    private const int HistoryLimit = 3000;
    /// <summary>The iOS backup format name, kept so one backup restores on either platform.</summary>
    private const string BackupFormat = "koda-ios-library";

    private readonly object _lock = new();
    private readonly string? _file;
    private readonly string? _historyFile;
    private readonly List<PlayEvent> _history = [];
    private Snapshot _s = new();
    /// <summary>Listening time arrives every few seconds; it is written with the next change or <see cref="Flush"/>.</summary>
    private bool _hasUnsavedListening;
    private int _historySaveVersion;

    public event Action? Changed;

    public Library(string? directory)
    {
        _file = directory is null ? null : Path.Combine(directory, "library.json");
        _historyFile = directory is null ? null : Path.Combine(directory, "history.json");
        Load();
        LoadHistory();
    }

    // MARK: Reading

    public Song[] Liked { get { lock (_lock) return [.. _s.Liked]; } }
    public AlbumSummary[] Albums { get { lock (_lock) return [.. _s.Albums]; } }
    public PlaylistSummary[] Playlists { get { lock (_lock) return [.. _s.Playlists]; } }
    public ArtistSummary[] Artists { get { lock (_lock) return [.. _s.Artists]; } }
    /// <summary>Most recent first, one entry per song.</summary>
    public Song[] Recent { get { lock (_lock) return [.. _s.Recent]; } }
    public string[] Searches { get { lock (_lock) return [.. _s.Searches]; } }
    /// <summary>Playlists made here, newest first.</summary>
    public UserPlaylist[] UserPlaylists { get { lock (_lock) return [.. _s.UserPlaylists]; } }
    /// <summary>Play counts and listening time per song, by video id.</summary>
    public IReadOnlyDictionary<string, SongStats> SongStats { get { lock (_lock) return new Dictionary<string, SongStats>(_s.SongStats); } }
    /// <summary>Seconds listened per day, keyed yyyy-MM-dd in the device's time zone.</summary>
    public IReadOnlyDictionary<string, double> DailyListening { get { lock (_lock) return new Dictionary<string, double>(_s.DailyListening); } }
    public IReadOnlyDictionary<string, double> LyricsOffsets { get { lock (_lock) return new Dictionary<string, double>(_s.LyricsOffsets); } }
    /// <summary>Every play, newest first, under the day it happened.</summary>
    public PlayEvent[] History { get { lock (_lock) return [.. _history]; } }

    // MARK: Songs

    public bool IsLiked(Song song) { lock (_lock) return _s.Liked.Exists(s => s.Id == song.Id); }

    public void ToggleLike(Song song) => Edit(s =>
    {
        var index = s.Liked.FindIndex(x => x.Id == song.Id);
        if (index >= 0) s.Liked.RemoveAt(index);
        else s.Liked.Insert(0, song);
    });

    /// <summary>Remembers that a song was played: in the recently played list, and in the stats.</summary>
    public void RecordPlay(Song song, DateTimeOffset? at = null)
    {
        // The switch says history, Quick picks and stats alike; when it is off,
        // a play leaves no trace at all.
        if (!Settings.Current.SavesListeningHistory) return;

        var date = at ?? DateTimeOffset.Now;
        Edit(s =>
        {
            s.Recent.RemoveAll(x => x.Id == song.Id);
            s.Recent.Insert(0, song);
            if (s.Recent.Count > RecentLimit) s.Recent.RemoveRange(RecentLimit, s.Recent.Count - RecentLimit);

            var stats = s.SongStats.TryGetValue(song.Id, out var held) ? held : global::Koda.Core.SongStats.New(song, date);
            s.SongStats[song.Id] = stats with { Song = song, Plays = stats.Plays + 1, LastPlayed = date };
        });

        lock (_lock)
        {
            _history.Insert(0, PlayEvent.New(song, date));
            if (_history.Count > HistoryLimit)
            {
                _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
            }
        }

        ScheduleHistorySave();
    }

    /// <summary>Credit <paramref name="seconds"/> of listening to <paramref name="song"/> on the day of <paramref name="at"/>.</summary>
    public void AddListening(double seconds, Song song, DateTimeOffset? at = null, TimeZoneInfo? zone = null)
    {
        if (seconds <= 0) return;
        if (!Settings.Current.SavesListeningHistory) return;

        var date = at ?? DateTimeOffset.Now;
        lock (_lock)
        {
            var stats = _s.SongStats.TryGetValue(song.Id, out var held) ? held : global::Koda.Core.SongStats.New(song, date);
            _s.SongStats[song.Id] = stats with { Seconds = stats.Seconds + seconds };
            var key = DayKey(date, zone);
            _s.DailyListening[key] = _s.DailyListening.GetValueOrDefault(key) + seconds;
            _hasUnsavedListening = true;
        }
    }

    /// <summary>Write listening time gathered since the last save.</summary>
    public void Flush()
    {
        bool dirty;
        lock (_lock) dirty = _hasUnsavedListening || _history.Count > 0;
        if (dirty)
        {
            Edit(_ => { });
            WriteHistoryNow();
        }
    }

    public void ClearHistory()
    {
        Edit(s => s.Recent.Clear());
        lock (_lock)
        {
            _history.Clear();
        }

        ScheduleHistorySave();
    }

    /// <summary>Forget one play. A song with no play left in the history leaves Recently Played too.</summary>
    public void RemoveFromHistory(Guid id)
    {
        string? songId;
        bool remains;
        lock (_lock)
        {
            var index = _history.FindIndex(e => e.Id == id);
            if (index < 0)
            {
                return;
            }

            songId = _history[index].Song.Id;
            _history.RemoveAt(index);
            remains = _history.Exists(e => e.Song.Id == songId);
        }

        if (!remains)
        {
            Edit(s => s.Recent.RemoveAll(song => song.Id == songId));
        }

        ScheduleHistorySave();
    }

    /// <summary>Forget play counts and listening time; history and likes stay.</summary>
    public void ClearStats() => Edit(s =>
    {
        s.SongStats.Clear();
        s.DailyListening.Clear();
    });

    public int PlaysOf(Song song) { lock (_lock) return _s.SongStats.TryGetValue(song.Id, out var st) ? st.Plays : 0; }

    // MARK: Collections

    public bool IsSavedAlbum(string id) { lock (_lock) return _s.Albums.Exists(a => a.Id == id); }
    public bool IsSavedPlaylist(string id) { lock (_lock) return _s.Playlists.Exists(p => p.Id == id); }
    public bool IsSavedArtist(string id) { lock (_lock) return _s.Artists.Exists(a => a.Id == id); }

    public void ToggleSave(AlbumSummary album) => Edit(s => Toggle(s.Albums, album, a => a.Id));
    public void ToggleSave(PlaylistSummary playlist) => Edit(s => Toggle(s.Playlists, playlist, p => p.Id));
    public void ToggleSave(ArtistSummary artist) => Edit(s => Toggle(s.Artists, artist, a => a.Id));

    private static void Toggle<T>(List<T> list, T item, Func<T, string> id)
    {
        var index = list.FindIndex(x => id(x) == id(item));
        if (index >= 0) list.RemoveAt(index);
        else list.Insert(0, item);
    }

    // MARK: Playlists made here

    public UserPlaylist? UserPlaylist(Guid id) { lock (_lock) return _s.UserPlaylists.Find(p => p.Id == id); }

    public UserPlaylist CreatePlaylist(string name, IEnumerable<Song>? songs = null, DateTimeOffset? at = null)
    {
        var trimmed = name.Trim();
        var playlist = global::Koda.Core.UserPlaylist.Create(trimmed.Length == 0 ? "New Playlist" : trimmed,
            Unique(songs ?? []), at ?? DateTimeOffset.Now);
        Edit(s => s.UserPlaylists.Insert(0, playlist));
        return playlist;
    }

    public void RenamePlaylist(Guid id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        EditPlaylist(id, p => p with { Name = trimmed });
    }

    public void DeletePlaylist(Guid id) => Edit(s => s.UserPlaylists.RemoveAll(p => p.Id == id));

    /// <summary>Append the songs the playlist does not already hold; returns how many were added.</summary>
    public int AddToPlaylist(IEnumerable<Song> songs, Guid id)
    {
        var added = 0;
        EditPlaylist(id, p =>
        {
            var known = p.Songs.Select(s => s.Id).ToHashSet();
            var fresh = Unique(songs).Where(s => !known.Contains(s.Id)).ToArray();
            added = fresh.Length;
            return p with { Songs = [.. p.Songs, .. fresh] };
        });
        return added;
    }

    public void RemoveSongs(IEnumerable<int> offsets, Guid id) => EditPlaylist(id, p =>
    {
        var songs = p.Songs.ToList();
        foreach (var index in offsets.Distinct().OrderByDescending(i => i))
        {
            if (index >= 0 && index < songs.Count) songs.RemoveAt(index);
        }
        return p with { Songs = [.. songs] };
    });

    /// <summary>The SwiftUI move: the songs at <paramref name="source"/> land before the song at <paramref name="destination"/>.</summary>
    public void MoveSongs(IEnumerable<int> source, int destination, Guid id) => EditPlaylist(id, p =>
    {
        var songs = p.Songs.ToList();
        var valid = source.Distinct().Where(i => i >= 0 && i < songs.Count).OrderBy(i => i).ToArray();
        if (valid.Length == 0) return p;
        var moving = valid.Select(i => songs[i]).ToArray();
        var insertAt = Math.Min(Math.Max(destination, 0), songs.Count) - valid.Count(i => i < destination);
        foreach (var index in valid.Reverse()) songs.RemoveAt(index);
        songs.InsertRange(insertAt, moving);
        return p with { Songs = [.. songs] };
    });

    private void EditPlaylist(Guid id, Func<UserPlaylist, UserPlaylist> change) => Edit(s =>
    {
        var index = s.UserPlaylists.FindIndex(p => p.Id == id);
        if (index < 0) return;
        s.UserPlaylists[index] = change(s.UserPlaylists[index]) with { Modified = DateTimeOffset.Now };
    });

    private static Song[] Unique(IEnumerable<Song> songs)
    {
        var seen = new HashSet<string>();
        return [.. songs.Where(s => seen.Add(s.Id))];
    }

    // MARK: Lyrics timing

    public double LyricsOffset(string songId) { lock (_lock) return _s.LyricsOffsets.GetValueOrDefault(songId); }

    /// <summary>Rounded to a tenth of a second; zero removes the entry.</summary>
    public void SetLyricsOffset(double offset, string songId) => Edit(s =>
    {
        var rounded = Math.Round(offset * 10, MidpointRounding.AwayFromZero) / 10;
        if (rounded == 0) s.LyricsOffsets.Remove(songId);
        else s.LyricsOffsets[songId] = rounded;
    });

    // MARK: Search history

    public void RecordSearch(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return;

        // Off, what is searched for is not remembered.
        if (!Settings.Current.SavesSearchHistory) return;

        Edit(s =>
        {
            s.Searches.RemoveAll(q => string.Equals(q, trimmed, StringComparison.OrdinalIgnoreCase));
            s.Searches.Insert(0, trimmed);
            if (s.Searches.Count > SearchLimit) s.Searches.RemoveRange(SearchLimit, s.Searches.Count - SearchLimit);
        });
    }

    public void RemoveSearch(string query) => Edit(s => s.Searches.RemoveAll(q => q == query));

    public void ClearSearches() => Edit(s => s.Searches.Clear());

    // MARK: Days

    public static string DayKey(DateTimeOffset date, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(date, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Seconds listened on the day of <paramref name="date"/>.</summary>
    public double ListeningOn(DateTimeOffset date, TimeZoneInfo? zone = null)
    {
        lock (_lock) return _s.DailyListening.GetValueOrDefault(DayKey(date, zone));
    }

    /// <summary>Days in a row with any listening, ending today, or yesterday while today has none yet.</summary>
    public int Streak(DateTimeOffset? asOf = null, TimeZoneInfo? zone = null)
    {
        var day = asOf ?? DateTimeOffset.Now;
        if (ListeningOn(day, zone) == 0) day = day.AddDays(-1);
        var count = 0;
        while (ListeningOn(day, zone) > 0)
        {
            count++;
            day = day.AddDays(-1);
        }
        return count;
    }

    // MARK: Storage

    /// <summary>The file's shape, field for field the Swift Snapshot, so a backup reads on either platform.</summary>
    private sealed class Snapshot
    {
        public List<Song> Liked { get; set; } = [];
        public List<AlbumSummary> Albums { get; set; } = [];
        public List<PlaylistSummary> Playlists { get; set; } = [];
        public List<ArtistSummary> Artists { get; set; } = [];
        public List<Song> Recent { get; set; } = [];
        public List<string> Searches { get; set; } = [];
        public List<UserPlaylist> UserPlaylists { get; set; } = [];
        public Dictionary<string, SongStats> SongStats { get; set; } = [];
        public Dictionary<string, double> DailyListening { get; set; } = [];
        public Dictionary<string, double> LyricsOffsets { get; set; } = [];
        /// <summary>The newest play taken from a Koda for Android backup, so importing the same
        /// backup, or a later one that repeats its history, never counts a play twice.</summary>
        public DateTimeOffset? AndroidPlaysImportedThrough { get; set; }
    }

    private sealed class Backup
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public DateTimeOffset Exported { get; set; }
        public Snapshot? Library { get; set; }
        /// <summary>Version 2 adds the dated plays; version 1 files read from Recent and the stats.</summary>
        public List<PlayEvent>? History { get; set; }
    }

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new Iso8601Seconds() },
    };

    private void Load()
    {
        if (_file is null) return;
        try
        {
            if (!File.Exists(_file)) return;
            if (JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_file), Options) is { } snapshot)
                _s = Normalized(snapshot);
        }
        catch (Exception)
        {
            // A damaged file starts an empty library rather than breaking the app.
        }
    }

    /// <summary>The dated history lives beside the library, as it does on the Mac.</summary>
    private void LoadHistory()
    {
        if (_historyFile is null) return;
        try
        {
            if (!File.Exists(_historyFile)) return;
            if (JsonSerializer.Deserialize<List<PlayEvent?>>(File.ReadAllText(_historyFile), Options) is { } events)
            {
                _history.AddRange(events.OfType<PlayEvent>().OrderByDescending(e => e.Date));
                if (_history.Count > HistoryLimit)
                {
                    _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
                }
            }
        }
        catch (Exception)
        {
            // A damaged history costs the page, never the library.
        }
    }

    /// <summary>Write the history a moment later, once, for the newest state.</summary>
    private void ScheduleHistorySave()
    {
        if (_historyFile is null) return;
        int version;
        lock (_lock) version = ++_historySaveVersion;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                string? text;
                lock (_lock)
                {
                    if (version != _historySaveVersion) return;
                    text = JsonSerializer.Serialize(_history, Options);
                }

                WriteHistory(text);
            }
            catch (Exception)
            {
                // The previous file stays; the next play tries again.
            }
        });
    }

    private void WriteHistoryNow()
    {
        if (_historyFile is null) return;
        string text;
        lock (_lock)
        {
            _historySaveVersion++;
            text = JsonSerializer.Serialize(_history, Options);
        }

        WriteHistory(text);
    }

    private void WriteHistory(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyFile!)!);
            var temp = _historyFile + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, _historyFile!, overwrite: true);
        }
        catch (Exception)
        {
            // The previous file stays.
        }
    }

    /// <summary>A file from an older version lacks the newer keys; each missing one starts empty.</summary>
    private static Snapshot Normalized(Snapshot s)
    {
        s.Liked ??= [];
        s.Albums ??= [];
        s.Playlists ??= [];
        s.Artists ??= [];
        s.Recent ??= [];
        s.Searches ??= [];
        s.UserPlaylists ??= [];
        s.SongStats ??= [];
        s.DailyListening ??= [];
        s.LyricsOffsets ??= [];
        return s;
    }

    private void Edit(Action<Snapshot> change)
    {
        string? text = null;
        lock (_lock)
        {
            change(_s);
            _hasUnsavedListening = false;
            if (_file is not null)
            {
                try
                {
                    text = JsonSerializer.Serialize(_s, Options);
                }
                catch (Exception)
                {
                    text = null;
                }
            }
            if (text is not null) WriteAtomically(text);
        }
        // Each listener on its own: one that throws (a page torn down mid-call) must not keep
        // the change from the others, such as the player bar's like button.
        foreach (var listener in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)listener)();
            }
            catch (Exception)
            {
                // A listener that throws must not undo the change or silence the rest.
            }
        }
    }

    private void WriteAtomically(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file!)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, _file!, overwrite: true);
        }
        catch (Exception)
        {
            // The previous file stays; the next change tries again.
        }
    }

    // MARK: Backup

    /// <summary>Everything in the library as one JSON file, for keeping or moving to another machine.</summary>
    public byte[] BackupData(DateTimeOffset? at = null)
    {
        lock (_lock)
        {
            var options = new JsonSerializerOptions(Options) { WriteIndented = true };
            return JsonSerializer.SerializeToUtf8Bytes(
                new Backup
                {
                    Format = BackupFormat,
                    Version = 2,
                    Exported = at ?? DateTimeOffset.Now,
                    Library = _s,
                    History = [.. _history],
                }, options);
        }
    }

    /// <summary>Replace the library with a backup. Nothing changes unless the whole file reads as one.</summary>
    public void Restore(byte[] data)
    {
        Backup? backup;
        try
        {
            backup = JsonSerializer.Deserialize<Backup>(data, Options);
        }
        catch (Exception)
        {
            throw new LibraryBackupException();
        }
        if (backup is not { Format: BackupFormat, Library: { } library }) throw new LibraryBackupException();
        var restored = Normalized(library);
        Edit(s =>
        {
            s.Liked = restored.Liked;
            s.Albums = restored.Albums;
            s.Playlists = restored.Playlists;
            s.Artists = restored.Artists;
            s.Recent = restored.Recent;
            s.Searches = restored.Searches;
            s.UserPlaylists = restored.UserPlaylists;
            s.SongStats = restored.SongStats;
            s.DailyListening = restored.DailyListening;
            s.LyricsOffsets = restored.LyricsOffsets;
            s.AndroidPlaysImportedThrough = restored.AndroidPlaysImportedThrough;
        });

        // A version 1 backup has no dated history: the plays it knows of become one each,
        // at their last play, exactly as the Mac restores them.
        var history = backup.History is { Count: > 0 } dated
            ? dated.Where(e => e.Song is not null).OrderByDescending(e => e.Date).Take(HistoryLimit).ToList()
            : restored.Recent
                .Where(song => restored.SongStats.ContainsKey(song.Id))
                .Select(song => PlayEvent.New(song, restored.SongStats[song.Id].LastPlayed))
                .OrderByDescending(e => e.Date)
                .ToList();
        lock (_lock)
        {
            _history.Clear();
            _history.AddRange(history);
        }

        ScheduleHistorySave();
    }

    // MARK: Koda for Android

    /// <summary>
    /// Add a Koda for Android backup to the library. Nothing here is replaced: likes, saved
    /// albums and playlists join what's already saved, plays add to the stats and history,
    /// and a playlist already here under the same name with the same songs isn't added again.
    /// </summary>
    public AndroidImportSummary ImportAndroid(AndroidBackup backup, TimeZoneInfo? zone = null)
    {
        var summary = new AndroidImportSummary();
        var historyAdds = new List<PlayEvent>();
        Edit(s =>
        {
            var likedIds = s.Liked.Select(x => x.Id).ToHashSet();
            var newLikes = backup.Liked.Where(x => !likedIds.Contains(x.Id)).ToArray();
            s.Liked.AddRange(newLikes);
            var saved = 0;
            foreach (var album in backup.Albums.Where(a => !s.Albums.Exists(x => x.Id == a.Id)))
            {
                s.Albums.Insert(0, album);
                saved++;
            }
            foreach (var playlist in backup.SavedPlaylists.Where(p => !s.Playlists.Exists(x => x.Id == p.Id)))
            {
                s.Playlists.Insert(0, playlist);
                saved++;
            }

            var playlists = 0;
            foreach (var playlist in backup.Playlists)
            {
                var ids = playlist.Songs.Select(x => x.Id).ToArray();
                if (s.UserPlaylists.Exists(p => p.Name == playlist.Name && p.Songs.Select(x => x.Id).SequenceEqual(ids))) continue;
                s.UserPlaylists.Add(global::Koda.Core.UserPlaylist.Create(playlist.Name, playlist.Songs, playlist.Created));
                playlists++;
            }
            s.UserPlaylists = [.. s.UserPlaylists.OrderByDescending(p => p.Created)];

            var through = s.AndroidPlaysImportedThrough;
            var fresh = backup.Plays.Where(p => through is null || p.Date > through).ToArray();
            foreach (var play in fresh)
            {
                var stats = s.SongStats.TryGetValue(play.Song.Id, out var held) ? held : global::Koda.Core.SongStats.New(play.Song, play.Date);
                s.SongStats[play.Song.Id] = stats with
                {
                    Plays = stats.Plays + 1,
                    Seconds = stats.Seconds + play.Seconds,
                    FirstPlayed = stats.FirstPlayed < play.Date ? stats.FirstPlayed : play.Date,
                    LastPlayed = stats.LastPlayed > play.Date ? stats.LastPlayed : play.Date,
                };
                var key = DayKey(play.Date, zone);
                s.DailyListening[key] = s.DailyListening.GetValueOrDefault(key) + play.Seconds;
                historyAdds.Add(PlayEvent.New(play.Song, play.Date));
            }
            if (fresh.Length > 0) s.AndroidPlaysImportedThrough = fresh[^1].Date;

            // Recently played, newest first across both: each song where it was last played.
            var lastPlayed = new Dictionary<string, (Song Song, DateTimeOffset At)>();
            foreach (var song in s.Recent)
                lastPlayed[song.Id] = (song, s.SongStats.TryGetValue(song.Id, out var st) ? st.LastPlayed : DateTimeOffset.MinValue);
            foreach (var play in fresh)
            {
                var known = lastPlayed.TryGetValue(play.Song.Id, out var entry);
                if (!known || entry.At < play.Date) lastPlayed[play.Song.Id] = (known ? entry.Song : play.Song, play.Date);
            }
            s.Recent = [.. lastPlayed.Values.OrderByDescending(v => v.At).Take(RecentLimit).Select(v => v.Song)];

            var knownSearches = s.Searches.Select(q => q.ToLowerInvariant()).ToHashSet();
            var before = s.Searches.Count;
            s.Searches = [.. s.Searches.Concat(backup.Searches.Where(q => !knownSearches.Contains(q.ToLowerInvariant()))).Take(SearchLimit)];

            summary = new AndroidImportSummary(newLikes.Length, fresh.Length, playlists, saved, s.Searches.Count - before);
        });

        if (historyAdds.Count > 0)
        {
            lock (_lock)
            {
                _history.AddRange(historyAdds);
                _history.Sort((a, b) => b.Date.CompareTo(a.Date));
                if (_history.Count > HistoryLimit)
                {
                    _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
                }
            }

            ScheduleHistorySave();
        }

        return summary;
    }

    /// <summary>Dates as Swift's .iso8601 reads and writes them: UTC, whole seconds.</summary>
    private sealed class Iso8601Seconds : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                // Swift's default Date encoding: seconds since 2001-01-01.
                return new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(reader.GetDouble());
            }
            return DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}
