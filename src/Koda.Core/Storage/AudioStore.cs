using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Koda.Core.Storage;

/// <summary>
/// Songs' audio kept locally: saved while they stream, so a song played
/// before starts from the file, and downloaded, kept until the listener removes it.
///
/// One file per song, filled as bytes arrive in any order, and an index of which
/// bytes each holds. Songs saved from listening are a cache: past <c>cacheLimit</c>
/// the least recently played go first, never a download or a song being played.
/// Every method may be called from any thread; the index is written off the
/// calling thread, a moment after a change.
/// Ported 1:1 from the KodaCore AudioStore.swift contract.
/// </summary>
public sealed class AudioStore
{
    /// <summary>One song's bytes, as the index holds them.</summary>
    public sealed class Entry
    {
        public string VideoId { get; }
        /// <summary>The song itself, so downloads can be listed with no connection.</summary>
        public Song? Song { get; set; }
        public long? ContentLength { get; set; }
        public int? Itag { get; set; }
        public double? Duration { get; set; }
        public double? LoudnessDb { get; set; }
        public ByteRanges Ranges { get; } = new();
        /// <summary>Kept until removed, never evicted.</summary>
        public bool IsDownload { get; set; }
        public DateTimeOffset Added { get; set; }
        public DateTimeOffset LastUsed { get; set; }

        public bool IsComplete => ContentLength is { } length && length > 0 && Ranges.Contains(0, length);
        public long Bytes => Ranges.Count;

        public Entry(string videoId, Song? song, DateTimeOffset at)
        {
            VideoId = videoId;
            Song = song;
            Added = at;
            LastUsed = at;
        }
    }

    /// <summary>The index line as it is stored: saved data must survive upgrades, anything
    /// missing takes its default.</summary>
    private sealed record SavedEntry(
        [property: JsonPropertyName("videoId")] string VideoId,
        [property: JsonPropertyName("song")] Song? Song,
        [property: JsonPropertyName("contentLength")] long? ContentLength,
        [property: JsonPropertyName("itag")] int? Itag,
        [property: JsonPropertyName("duration")] double? Duration,
        [property: JsonPropertyName("loudnessDb")] double? LoudnessDb,
        [property: JsonPropertyName("ranges")] long[][]? Ranges,
        [property: JsonPropertyName("isDownload")] bool IsDownload,
        [property: JsonPropertyName("added")] DateTimeOffset Added,
        [property: JsonPropertyName("lastUsed")] DateTimeOffset? LastUsed);

    private readonly string _directory;
    private readonly string _indexFile;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Dictionary<string, FileStream> _handles = [];
    /// <summary>Songs being played or fetched, by how many users; never evicted.</summary>
    private readonly Dictionary<string, int> _inUse = [];
    private long _cacheLimitBytes;
    private int _saveVersion;

    /// <summary>Raised when downloads or the offline set change, so lists can redraw.</summary>
    public event Action? Changed;

    public AudioStore(string directory, long cacheLimit = 1_000_000_000)
    {
        _directory = directory;
        _indexFile = Path.Combine(directory, "index.json");
        _cacheLimitBytes = cacheLimit;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // Reads and writes fail individually below.
        }

        try
        {
            if (File.ReadAllText(_indexFile) is not { Length: > 0 } saved)
            {
                return;
            }

            // A file that went missing holds nothing, whatever the index says.
            foreach (var entry in Decode(saved))
            {
                if (!File.Exists(Path.Combine(_directory, $"{entry.VideoId}.m4a")))
                {
                    entry.Ranges.Clear();
                }

                _entries[entry.VideoId] = entry;
            }
        }
        catch
        {
            // A damaged index costs the cache, never playback.
        }
    }

    // MARK: Reading

    public Entry? GetEntry(string videoId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(videoId, out var entry) ? Snapshot(entry) : null;
        }
    }

    /// <summary>Downloads, most recently added first.</summary>
    public Entry[] Downloads
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries.Values.Where(e => e.IsDownload).OrderByDescending(e => e.Added).Select(Snapshot)];
            }
        }
    }

    /// <summary>Songs held whole, downloaded or kept from listening, most recently played first: what
    /// plays with no connection.</summary>
    public Song[] CompleteSongs
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries.Values.Where(e => e.IsComplete).OrderByDescending(e => e.LastUsed)
                    .Select(e => e.Song).OfType<Song>()];
            }
        }
    }

    public long CacheBytes
    {
        get
        {
            lock (_lock)
            {
                return _entries.Values.Where(e => !e.IsDownload).Sum(e => e.Bytes);
            }
        }
    }

    public long DownloadBytes
    {
        get
        {
            lock (_lock)
            {
                return _entries.Values.Where(e => e.IsDownload).Sum(e => e.Bytes);
            }
        }
    }

    public long CacheLimit
    {
        get
        {
            lock (_lock)
            {
                return _cacheLimitBytes;
            }
        }
        set
        {
            lock (_lock)
            {
                _cacheLimitBytes = value;
            }

            Evict();
            ScheduleSave();
        }
    }

    public string FileUrl(string videoId) => Path.Combine(_directory, $"{videoId}.m4a");

    /// <summary>The song as a stream from its file, when every byte of it is here.</summary>
    public ResolvedStream? CompleteStream(string videoId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(videoId, out var entry) || !entry.IsComplete)
            {
                return null;
            }

            entry.LastUsed = DateTimeOffset.UtcNow;
            return new ResolvedStream(
                videoId, new Uri(FileUrl(videoId)), string.Empty, entry.Itag ?? 0,
                entry.ContentLength, entry.Duration, entry.LoudnessDb, ExpiresAt: null);
        }
    }

    public long ContiguousLength(string videoId, long offset)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(videoId, out var entry) ? entry.Ranges.ContiguousLength(offset) : 0;
        }
    }

    /// <summary>The first byte the song's file is missing, and where that gap ends.</summary>
    public (long Lower, long Upper)? FirstGap(string videoId, long offset = 0)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(videoId, out var entry) || entry.ContentLength is not { } length)
            {
                return null;
            }

            if (entry.Ranges.FirstMissing(offset, length) is not { } start)
            {
                return null;
            }

            return (start, entry.Ranges.GapEnd(start, length));
        }
    }

    /// <summary>Bytes from the file, only when all of them are there.</summary>
    public byte[]? Read(string videoId, long offset, long count)
    {
        lock (_lock)
        {
            if (count <= 0 || !_entries.TryGetValue(videoId, out var entry)
                || !entry.Ranges.Contains(offset, offset + count)
                || OpenHandle(videoId) is not { } handle)
            {
                return null;
            }

            try
            {
                handle.Seek(offset, SeekOrigin.Begin);
                var data = new byte[count];
                var read = 0;
                while (read < count)
                {
                    var n = handle.Read(data, read, (int)(count - read));
                    if (n == 0)
                    {
                        return null;
                    }

                    read += n;
                }

                return data;
            }
            catch
            {
                return null;
            }
        }
    }

    // MARK: Writing

    /// <summary>Get ready to hold <paramref name="stream"/>'s bytes. A different format of the song
    /// than the one held (another length) starts the file again.</summary>
    public void Prepare(ResolvedStream stream, Song? song)
    {
        lock (_lock)
        {
            var entry = _entries.TryGetValue(stream.VideoId, out var held)
                ? held
                : new Entry(stream.VideoId, song, DateTimeOffset.UtcNow);
            if (stream.ContentLength is { } length && entry.ContentLength is { } heldLength && heldLength != length)
            {
                DiscardBytes(entry);
            }

            if (song is not null)
            {
                entry.Song = song;
            }

            // The stream wins, the held value only fills in what it does not say (Swift:
            // `stream.contentLength ?? entry.contentLength`). [scar] The first port had it
            // backwards (`??=`): after another format's bytes were discarded above, the
            // file kept the old length, so it could never complete and gap fills ran past
            // the real end of the file.
            entry.ContentLength = stream.ContentLength ?? entry.ContentLength;
            entry.Itag = stream.Itag;
            entry.Duration = stream.Duration ?? entry.Duration;
            entry.LoudnessDb = stream.LoudnessDb ?? entry.LoudnessDb;
            entry.LastUsed = DateTimeOffset.UtcNow;
            _entries[stream.VideoId] = entry;
        }

        ScheduleSave();
    }

    /// <summary>Write <paramref name="data"/> where it belongs in the song's file. Throws
    /// when the write is refused, as the Swift write throws the load along.</summary>
    public void Write(byte[] data, long offset, string videoId)
    {
        if (data.Length == 0)
        {
            return;
        }

        bool completed;
        lock (_lock)
        {
            if (!_entries.TryGetValue(videoId, out var entry) || OpenHandle(videoId) is not { } handle)
            {
                throw new IOException($"the audio store cannot open {videoId}");
            }

            var wasComplete = entry.IsComplete;
            try
            {
                handle.Seek(offset, SeekOrigin.Begin);
                handle.Write(data, 0, data.Length);
                handle.Flush(flushToDisk: false);
            }
            catch (Exception error)
            {
                throw new IOException(
                    $"the audio store cannot write {data.Length} bytes at {offset} to {videoId}", error);
            }

            entry.Ranges.Insert(offset, offset + data.Length);
            completed = entry.IsComplete && !wasComplete;
            if (completed)
            {
                handle.Dispose();
                _handles.Remove(videoId);
            }
        }

        ScheduleSave();
        if (completed)
        {
            Evict();
            Changed?.Invoke();
        }
    }

    /// <summary>Keep the song through eviction while it plays or downloads; balance with <see cref="EndUse"/>.</summary>
    public void BeginUse(string videoId)
    {
        lock (_lock)
        {
            _inUse.TryGetValue(videoId, out var count);
            _inUse[videoId] = count + 1;
        }
    }

    public void EndUse(string videoId)
    {
        lock (_lock)
        {
            _inUse.TryGetValue(videoId, out var count);
            if (count > 1)
            {
                _inUse[videoId] = count - 1;
            }
            else
            {
                _inUse.Remove(videoId);
            }
        }

        Evict();
    }

    /// <summary>A file that failed to play: drop its bytes so it is fetched again, keeping whether it is a download.</summary>
    public void Discard(string videoId)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(videoId, out var entry))
            {
                DiscardBytes(entry);
            }
        }

        ScheduleSave();
    }

    // MARK: Downloads

    /// <summary>Mark songs as downloads, kept until removed. Their bytes are fetched by the
    /// downloads driver, which a later phase owns.</summary>
    public void MarkDownloaded(IReadOnlyList<Song> songs, DateTimeOffset at)
    {
        lock (_lock)
        {
            foreach (var song in songs)
            {
                var entry = _entries.TryGetValue(song.Id, out var held)
                    ? held
                    : new Entry(song.Id, song, at);
                if (!entry.IsDownload)
                {
                    entry.Added = at;
                }

                entry.IsDownload = true;
                entry.Song = song;
                _entries[song.Id] = entry;
            }
        }

        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>Remove downloads and their files, unless one is playing: that one stays as cache.</summary>
    public void RemoveDownloads(IReadOnlyList<string> videoIds)
    {
        lock (_lock)
        {
            foreach (var id in videoIds)
            {
                if (!_entries.TryGetValue(id, out var entry) || !entry.IsDownload)
                {
                    continue;
                }

                if (_inUse.ContainsKey(id))
                {
                    entry.IsDownload = false;
                }
                else
                {
                    RemoveFile(id);
                }
            }
        }

        ScheduleSave();
        Changed?.Invoke();
    }

    public void RemoveAllDownloads() => RemoveDownloads(Downloads.Select(e => e.VideoId).ToArray());

    /// <summary>Empty the listening cache, except songs in use.</summary>
    public void ClearCache()
    {
        lock (_lock)
        {
            foreach (var (id, entry) in _entries.Where(pair => !pair.Value.IsDownload).ToArray())
            {
                if (!_inUse.ContainsKey(id))
                {
                    RemoveFile(id);
                }
            }
        }

        ScheduleSave();
        Changed?.Invoke();
    }

    // MARK: Housekeeping

    /// <summary>Bring the cache under its limit, least recently played first.</summary>
    private void Evict()
    {
        lock (_lock)
        {
            var total = _entries.Values.Where(e => !e.IsDownload).Sum(e => e.Bytes);
            if (total <= _cacheLimitBytes)
            {
                return;
            }

            var candidates = _entries.Values
                .Where(e => !e.IsDownload && !_inUse.ContainsKey(e.VideoId))
                .OrderBy(e => e.LastUsed)
                .ToList();
            // Removed under the same lock that chose them, as the Swift evict does: released
            // in between, a victim could begin playing and lose its file mid-song.
            foreach (var entry in candidates)
            {
                if (total <= _cacheLimitBytes)
                {
                    break;
                }

                total -= entry.Bytes;
                RemoveFile(entry.VideoId);
            }
        }

        ScheduleSave();
    }

    /// <summary>Called with the lock held.</summary>
    private FileStream? OpenHandle(string videoId)
    {
        if (_handles.TryGetValue(videoId, out var handle))
        {
            return handle;
        }

        try
        {
            var url = FileUrl(videoId);
            if (!File.Exists(url))
            {
                using (File.Create(url)) { }
            }

            handle = new FileStream(url, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _handles[videoId] = handle;
            return handle;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Called with the lock held.</summary>
    private void DiscardBytes(Entry entry)
    {
        if (_handles.Remove(entry.VideoId, out var handle))
        {
            handle.Dispose();
        }

        try
        {
            File.Delete(FileUrl(entry.VideoId));
        }
        catch
        {
            // The file may still be in use elsewhere; the index no longer claims it.
        }

        entry.Ranges.Clear();
    }

    /// <summary>Called with the lock held.</summary>
    private void RemoveFile(string videoId)
    {
        if (_handles.Remove(videoId, out var handle))
        {
            handle.Dispose();
        }

        try
        {
            File.Delete(FileUrl(videoId));
        }
        catch
        {
            // Same as DiscardBytes: the index is the truth.
        }

        _entries.Remove(videoId);
    }

    /// <summary>Write the index a moment later, once, for the newest state.</summary>
    private void ScheduleSave()
    {
        long version;
        lock (_lock)
        {
            version = ++_saveVersion;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                string? saved;
                lock (_lock)
                {
                    if (version != _saveVersion)
                    {
                        return;
                    }

                    saved = Encode();
                }

                if (saved is not null)
                {
                    WriteAtomically(saved);
                }
            }
            catch
            {
                // The previous good index persists.
            }
        });
    }

    /// <summary>Write the index now, on this thread: the app is leaving the screen.</summary>
    public void Flush()
    {
        try
        {
            string? saved;
            lock (_lock)
            {
                _saveVersion++;
                saved = Encode();
            }

            if (saved is not null)
            {
                WriteAtomically(saved);
            }
        }
        catch
        {
            // Keeping the previous index is fine; the files carry their bytes.
        }
    }

    // MARK: Index encoding

    private string? Encode()
    {
        var lines = new List<SavedEntry>();
        foreach (var entry in _entries.Values)
        {
            Song? song = entry.Song;
            if (song is not null)
            {
                // One broken song costs only its line, never the index.
                try
                {
                    _ = JsonSerializer.SerializeToNode(song);
                }
                catch
                {
                    song = null;
                }
            }

            lines.Add(new SavedEntry(
                entry.VideoId,
                song, // the checked one above, not entry.Song: a broken song costs its line only
                entry.ContentLength,
                entry.Itag,
                entry.Duration,
                entry.LoudnessDb,
                [.. entry.Ranges.InOrder.Select(range => new[] { range.Lower, range.Upper })],
                entry.IsDownload,
                entry.Added,
                entry.LastUsed));
        }

        try
        {
            return JsonSerializer.Serialize(lines);
        }
        catch
        {
            // An index too broken to encode is lost; the files' bytes help nothing without it.
            return null;
        }
    }

    private static Entry[] Decode(string saved)
    {
        try
        {
            if (JsonSerializer.Deserialize<SavedEntry[]>(saved) is not { } lines)
            {
                return [];
            }

            var entries = new List<Entry>();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line.VideoId))
                {
                    continue;
                }

                var at = line.Added;
                var entry = new Entry(line.VideoId, line.Song, at)
                {
                    ContentLength = line.ContentLength,
                    Itag = line.Itag,
                    Duration = line.Duration,
                    LoudnessDb = line.LoudnessDb,
                    IsDownload = line.IsDownload,
                    LastUsed = line.LastUsed ?? at,
                };
                if (line.Ranges is { } ranges)
                {
                    foreach (var range in ranges)
                    {
                        if (range is [_, _, ..])
                        {
                            entry.Ranges.Insert(range[0], range[1]);
                        }
                    }
                }

                entries.Add(entry);
            }

            return [.. entries];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>A reader's copy of an entry, bytes included.</summary>
    private static Entry Snapshot(Entry entry)
    {
        var copy = new Entry(entry.VideoId, entry.Song, entry.Added)
        {
            ContentLength = entry.ContentLength,
            Itag = entry.Itag,
            Duration = entry.Duration,
            LoudnessDb = entry.LoudnessDb,
            IsDownload = entry.IsDownload,
            LastUsed = entry.LastUsed,
        };
        foreach (var range in entry.Ranges.InOrder)
        {
            copy.Ranges.Insert(range.Lower, range.Upper);
        }

        return copy;
    }

    private void WriteAtomically(string saved)
    {
        var temp = $"{_indexFile}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, saved);
        File.Move(temp, _indexFile, overwrite: true);
    }
}
