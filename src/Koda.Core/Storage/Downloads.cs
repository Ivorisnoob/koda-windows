using System.IO;
using System.Net;
using System.Net.Http;

namespace Koda.Core.Storage;

/// <summary>
/// Songs the listener downloaded, and the work of fetching them: two at a time, in the order
/// asked, picked up again at the next launch when the app closed first. A song already held
/// whole from listening is downloaded at once, with nothing fetched.
///
/// Ported from KodaCore Downloads.swift. The Swift class is @MainActor; this one takes a lock,
/// so it may be called from any thread, and raises <see cref="Changed"/> (off the caller's
/// thread) whenever a song's state or the list changes.
/// </summary>
public sealed class Downloads : IDisposable
{
    public enum Phase
    {
        None,
        /// <summary>Asked for: waiting its turn, or for a connection.</summary>
        Waiting,
        Downloading,
        Downloaded,
    }

    /// <summary>A song's download: its phase and, while downloading, how much is here, 0 to 1.</summary>
    public readonly record struct State(Phase Phase, double Progress = 0)
    {
        public static readonly State None = new(Phase.None);
        public static readonly State Waiting = new(Phase.Waiting);
        public static readonly State Downloaded = new(Phase.Downloaded, 1);
    }

    private const int Parallel = 2;
    /// <summary>Bytes per request; a song takes a few.</summary>
    private const long Chunk = 1 << 20;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);

    private readonly AudioStore _store;
    private readonly Func<string, CancellationToken, Task<ResolvedStream>> _resolve;
    private readonly Func<string, Task> _streamFailed;
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private readonly List<Song> _songs = [];
    private readonly Dictionary<string, State> _states = [];
    private readonly List<string> _queue = [];
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private bool _disposed;

    /// <summary>A song's state or the list of downloads changed.</summary>
    public event Action? Changed;

    /// <param name="resolve">The song's stream (YouTubeMusic.StreamAsync).</param>
    /// <param name="streamFailed">Tell the resolver a URL was refused (YouTubeMusic.StreamFailedAsync).</param>
    /// <param name="handler">The transport; tests pass a recorded one.</param>
    public Downloads(AudioStore store, Func<string, CancellationToken, Task<ResolvedStream>> resolve,
        Func<string, Task> streamFailed, HttpMessageHandler? handler = null)
    {
        _store = store;
        _resolve = resolve;
        _streamFailed = streamFailed;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        foreach (var entry in store.Downloads)
        {
            if (entry.Song is not { } song) continue;
            _songs.Add(song);
            _states[song.Id] = entry.IsComplete ? State.Downloaded : State.Waiting;
            if (!entry.IsComplete) _queue.Add(song.Id);
        }
    }

    /// <summary>Start fetching what the last session left unfinished.</summary>
    public void Start()
    {
        lock (_lock) Pump();
    }

    // MARK: Reading

    /// <summary>Downloaded songs and those on their way, most recently added first.</summary>
    public Song[] Songs { get { lock (_lock) return [.. _songs]; } }

    /// <summary>Space the downloads take on disk.</summary>
    public long Bytes => _store.DownloadBytes;

    public State StateOf(Song song)
    {
        lock (_lock) return _states.GetValueOrDefault(song.Id, State.None);
    }

    /// <summary>An album's or playlist's: downloaded when every song is, downloading while any is
    /// on its way, with the share of the whole that is here.</summary>
    public State StateOf(IReadOnlyCollection<Song> songs)
    {
        if (songs.Count == 0) return State.None;
        var here = 0.0;
        var onTheWay = false;
        lock (_lock)
        {
            foreach (var song in songs)
            {
                var state = _states.GetValueOrDefault(song.Id, State.None);
                switch (state.Phase)
                {
                    case Phase.Waiting:
                        onTheWay = true;
                        break;
                    case Phase.Downloading:
                        onTheWay = true;
                        here += state.Progress;
                        break;
                    case Phase.Downloaded:
                        here += 1;
                        break;
                }
            }
        }

        if (here >= songs.Count) return State.Downloaded;
        return onTheWay ? new State(Phase.Downloading, here / songs.Count) : State.None;
    }

    // MARK: Changing

    public void Download(IEnumerable<Song> songs)
    {
        lock (_lock)
        {
            var seen = new HashSet<string>();
            var fresh = songs.Where(s => _states.GetValueOrDefault(s.Id, State.None).Phase == Phase.None && seen.Add(s.Id)).ToArray();
            if (fresh.Length == 0) return;
            _store.MarkDownloaded(fresh, DateTimeOffset.UtcNow);
            foreach (var song in fresh) _states[song.Id] = State.Waiting;
            _songs.InsertRange(0, fresh);
            _queue.AddRange(fresh.Select(s => s.Id));
            Pump();
        }

        RaiseChanged();
    }

    public void Remove(IEnumerable<Song> songs)
    {
        var ids = songs.Select(s => s.Id).ToHashSet();
        lock (_lock)
        {
            foreach (var id in ids)
            {
                if (_running.Remove(id, out var running)) running.Cancel();
                _states.Remove(id);
            }

            _queue.RemoveAll(ids.Contains);
            _songs.RemoveAll(s => ids.Contains(s.Id));
            _store.RemoveDownloads([.. ids]);
            Pump();
        }

        RaiseChanged();
    }

    public void RemoveAll() => Remove(Songs);

    // MARK: Fetching

    /// <summary>Called with the lock held: start downloads until two are running.</summary>
    private void Pump()
    {
        while (!_disposed && _running.Count < Parallel && _queue.Count > 0)
        {
            var id = _queue[0];
            _queue.RemoveAt(0);
            if (_songs.Find(s => s.Id == id) is not { } song) continue;
            var cancel = new CancellationTokenSource();
            _running[id] = cancel;
            _ = Task.Run(async () =>
            {
                try
                {
                    await FetchAsync(song, cancel.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (_lock)
                    {
                        if (_running.TryGetValue(id, out var current) && current == cancel) _running.Remove(id);
                        Pump();
                    }

                    cancel.Dispose();
                }
            });
        }
    }

    private async Task FetchAsync(Song song, CancellationToken cancellation)
    {
        var id = song.Id;
        _store.BeginUse(id);
        try
        {
            var refreshedUrl = false;
            var delay = FirstRetry;
            while (!cancellation.IsCancellationRequested)
            {
                if (_store.GetEntry(id)?.IsComplete == true) break;
                try
                {
                    var stream = await _resolve(id, cancellation).ConfigureAwait(false);
                    _store.Prepare(stream, song);
                    // Without its length the file can't be laid out; a later answer may have it.
                    if (stream.ContentLength is null) throw MusicException.Network("no content length");
                    SetState(id, new State(Phase.Downloading, Progress(id)));
                    await FillAsync(id, stream, cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (MusicException error) when (error.Kind == MusicException.Reason.Unplayable)
                {
                    // A verdict on the video: it can never be downloaded.
                    Remove([song]);
                    return;
                }
                catch (Exception error)
                {
                    if (cancellation.IsCancellationRequested) return;
                    if (!refreshedUrl && error is RefusedException)
                    {
                        // googlevideo refused the URL: one fresh one, at once.
                        refreshedUrl = true;
                        await _streamFailed(id).ConfigureAwait(false);
                        continue;
                    }

                    // Offline or refused: wait its turn again a little later, longer each time.
                    SetState(id, State.Waiting);
                    try
                    {
                        await Task.Delay(delay, cancellation).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LongestRetry.Ticks));
                }
            }

            if (cancellation.IsCancellationRequested) return;
            lock (_lock)
            {
                if (!_states.ContainsKey(id)) return; // removed meanwhile
                _states[id] = State.Downloaded;
            }

            RaiseChanged();
        }
        finally
        {
            _store.EndUse(id);
        }
    }

    /// <summary>googlevideo said no to the URL itself.</summary>
    private sealed class RefusedException(HttpStatusCode status) : IOException($"googlevideo refused the download ({(int)status})");

    /// <summary>Fetch the song's missing bytes a chunk at a time and write each to its file.</summary>
    private async Task FillAsync(string id, ResolvedStream stream, CancellationToken cancellation)
    {
        while (_store.FirstGap(id) is { } gap)
        {
            cancellation.ThrowIfCancellationRequested();
            var end = Math.Min(gap.Upper, gap.Lower + Chunk);
            using var request = new HttpRequestMessage(HttpMethod.Get, stream.Url);
            request.Headers.TryAddWithoutValidation("Range", $"bytes={gap.Lower}-{end - 1}");
            request.Headers.TryAddWithoutValidation("User-Agent", stream.UserAgent);
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            // 200: the range was ignored and this is the whole file.
            if (response.StatusCode is not (HttpStatusCode.PartialContent or HttpStatusCode.OK))
            {
                throw new RefusedException(response.StatusCode);
            }

            var data = await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
            if (data.Length == 0) throw new RefusedException(response.StatusCode);
            _store.Write(data, response.StatusCode == HttpStatusCode.OK ? 0 : gap.Lower, id);
            SetState(id, new State(Phase.Downloading, Progress(id)));
        }
    }

    private double Progress(string id) =>
        _store.GetEntry(id) is { ContentLength: { } length } entry && length > 0
            ? Math.Min(1, (double)entry.Bytes / length)
            : 0;

    private void SetState(string id, State state)
    {
        lock (_lock)
        {
            if (!_states.ContainsKey(id)) return; // removed meanwhile
            _states[id] = state;
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A listener that throws must not stop a download.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var running in _running.Values) running.Cancel();
        }

        _http.Dispose();
    }
}
