using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Interop;
using Koda.Services;
using Microsoft.UI.Dispatching;
using Windows.Media.Playback;

namespace Koda.Playback;

/// <summary>
/// The glue between the engine, the audio player, the play queue and the shell (player bar,
/// Now Playing, tray, SMTC). One instance per process, on the UI thread. Ported from the
/// KodaCore Player's queue behaviour: Autoplay carries on with the radio of the last song
/// when the queue runs low; repeat and shuffle are the queue's; the queue survives a relaunch.
/// </summary>
public sealed class PlaybackService
{
    public static PlaybackService Instance { get; } = new();

    public enum StatusTone
    {
        /// <summary>An informational note.</summary>
        Pacing,

        /// <summary>Something went wrong with playback or resolution.</summary>
        Fault,
    }

    private static string QueuePath => Path.Combine(App.DataDirectory, "queue.json");

    private readonly Player _player = new();
    private PlayQueue _queue = new();
    private DispatcherQueue? _dispatch;
    private SmtcController? _smtc;
    private DispatcherQueueTimer? _positionTimer;
    private ResolvedStream? _currentStream;
    /// <summary>The current song's audio is loaded into the player (false after a relaunch until Play).</summary>
    private bool _loaded;
    private bool _resolving;
    private bool _wantsToPlay;
    private bool _failedOnce;
    private bool _extending;
    /// <summary>Bumped per track start, so a slow resolve for a song skipped past is dropped.</summary>
    private int _generation;
    private double _resumeAt;

    /// <summary>The current song changed (bar text, cover, like, Now Playing header).</summary>
    public event Action? TrackChanged;

    /// <summary>The queue's contents or modes changed (Now Playing's list, shuffle/repeat toggles).</summary>
    public event Action? QueueChanged;

    /// <summary>Play/pause or loading changed.</summary>
    public event Action? PlayStateChanged;

    /// <summary>Twice a second: scrubbers and SMTC timeline.</summary>
    public event Action? PositionTick;

    /// <summary>Non-blocking status line for the shell ribbon (never a dialog).</summary>
    public event Action<StatusTone, string>? StatusRaised;

    public Song? CurrentSong => _queue.Current?.Song;

    public PlayQueue Queue => _queue;

    /// <summary>What the listener asked for: the play button follows this, not the decoder's state.</summary>
    public bool IsPlaying => _wantsToPlay;

    /// <summary>Asked to play and no sound yet: resolving, opening or buffering.</summary>
    public bool IsLoading => _wantsToPlay && (_resolving || _player.State is MediaPlayerState.Opening or MediaPlayerState.Buffering);

    public bool IsMuted { get; private set; }

    public bool Autoplay { get; private set; } = true;

    public bool CanNext => _queue.PeekNext(automatic: false) is not null || (Autoplay && CurrentSong is not null);

    public bool CanPrev => CurrentSong is not null;

    public double PositionSeconds => _loaded ? _player.PositionSeconds : _resumeAt;

    public double DurationSeconds => _currentStream?.Duration ?? CurrentSong?.Duration ?? 0;

    /// <summary>Connect the service to the UI thread and SMTC; restores the last queue, paused.</summary>
    public void Connect(DispatcherQueue dispatch, SmtcController smtc)
    {
        _dispatch = dispatch;
        _smtc = smtc;

        _player.Ended += () => dispatch.TryEnqueue(() => _ = OnEndedAsync());
        _player.Failed += reason => dispatch.TryEnqueue(() => _ = RecoverAsync(reason));
        _player.StateReached += state => dispatch.TryEnqueue(() =>
        {
            if (state == MediaPlayerState.Playing) _resolving = false;
            PlayStateChanged?.Invoke();
            _smtc?.UpdatePlaybackState(state == MediaPlayerState.Playing);
        });

        // A 429 arms a hold on background fan-out only; the shell shows it as a ribbon.
        EngineHost.Music.Gate.Throttled += hold => dispatch.TryEnqueue(() => StatusRaised?.Invoke(
            StatusTone.Pacing,
            $"YouTube asked Koda to slow down - background refreshes pause for {Math.Ceiling(hold.TotalMinutes):F0} min"));

        _positionTimer = dispatch.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(500);
        _positionTimer.Tick += (_, _) =>
        {
            PositionTick?.Invoke();
            if (CurrentSong is { } listening && _player.State == MediaPlayerState.Playing)
            {
                App.Library.AddListening(0.5, listening);
            }

            _smtc?.SetTimeline(PositionSeconds, DurationSeconds);
        };
        _positionTimer.Start();

        RestoreQueue();
    }

    // MARK: starting things

    /// <summary>Play a song now, adopting <paramref name="context"/> as the queue around it.</summary>
    public Task PlayNowAsync(Song song, IReadOnlyList<Song> context)
    {
        var list = context.Count > 0 ? context : [song];
        var index = Math.Max(0, list.ToList().FindIndex(s => s.Id == song.Id));
        _queue.Replace(list, index); // repeat stays with the listener, not the list
        QueueChanged?.Invoke();
        return PlayCurrentAsync();
    }

    /// <summary>The songs in a shuffled order (the queue's shuffle on).</summary>
    public Task ShuffleAsync(IReadOnlyList<Song> songs)
    {
        if (songs.Count == 0)
        {
            return Task.CompletedTask;
        }

        _queue.Replace(songs, Random.Shared.Next(songs.Count), shuffled: true);
        QueueChanged?.Invoke();
        return PlayCurrentAsync();
    }

    /// <summary>The radio that follows a song, the song itself first.</summary>
    public async Task StartRadioAsync(Song seed)
    {
        try
        {
            var radio = await EngineHost.Music.RadioAsync(seed.Id);
            var songs = radio.Songs.Length > 0 ? radio.Songs : [seed];
            await PlayNowAsync(songs[0], songs);
        }
        catch (MusicException error)
        {
            StatusRaised?.Invoke(StatusTone.Fault, $"The radio for {seed.Title} didn't start. {Helpers.Errors.Describe(error)}");
        }
    }

    /// <summary>Queue songs right after the current one; with nothing playing they start instead.</summary>
    public Task PlayNextAsync(Song song)
    {
        if (CurrentSong is null)
        {
            return PlayNowAsync(song, [song]);
        }

        _queue.PlayNext([song]);
        QueueChanged?.Invoke();
        TrackChanged?.Invoke(); // Next becomes available
        SaveQueue();
        StatusRaised?.Invoke(StatusTone.Pacing, $"Playing next: {song.Title}");
        return Task.CompletedTask;
    }

    /// <summary>Songs to the end of the queue.</summary>
    public void AddToQueue(IReadOnlyList<Song> songs, bool announce = true)
    {
        if (CurrentSong is null && songs.Count > 0)
        {
            _ = PlayNowAsync(songs[0], songs);
            return;
        }

        _queue.Append(songs);
        QueueChanged?.Invoke();
        SaveQueue();
        if (announce) StatusRaised?.Invoke(StatusTone.Pacing, songs.Count == 1 ? $"Added to queue: {songs[0].Title}" : $"Added {songs.Count} songs to the queue");
    }

    // MARK: transport

    public void Toggle()
    {
        if (CurrentSong is null)
        {
            return;
        }

        if (_wantsToPlay)
        {
            _wantsToPlay = false;
            _player.Pause();
        }
        else if (!_loaded)
        {
            // After a relaunch the song is shown but not loaded; Play loads it where it was.
            _ = PlayCurrentAsync(resumeAt: _resumeAt);
        }
        else
        {
            _wantsToPlay = true;
            _player.Play();
        }

        PlayStateChanged?.Invoke();
    }

    public async Task NextAsync()
    {
        if (CurrentSong is null)
        {
            return;
        }

        if (_queue.Advance(automatic: false) is not null)
        {
            await PlayCurrentAsync();
        }
        else if (Autoplay)
        {
            await ExtendWithRadioAsync(thenAdvance: true);
        }
    }

    /// <summary>Previous restarts the song past its first three seconds.</summary>
    public async Task PreviousAsync()
    {
        if (CurrentSong is null)
        {
            return;
        }

        if (PositionSeconds > 3.0)
        {
            SeekSeconds(0);
            return;
        }

        var before = _queue.CurrentIndex;
        _queue.Retreat();
        if (_queue.CurrentIndex != before)
        {
            await PlayCurrentAsync();
        }
        else
        {
            SeekSeconds(0);
        }
    }

    public async Task JumpAsync(Guid id)
    {
        if (_queue.Jump(id))
        {
            await PlayCurrentAsync();
        }
    }

    public void SeekSeconds(double seconds)
    {
        if (_loaded) _player.Seek(Math.Max(0, seconds));
        else _resumeAt = Math.Max(0, seconds);
    }

    public void SetVolumePercent(double percent) => _player.Volume = percent;

    public void SetMuted(bool muted)
    {
        IsMuted = muted;
        _player.Muted = muted;
    }

    // MARK: queue edits and modes

    public void Remove(Guid id)
    {
        if (_queue.Remove(id))
        {
            QueueChanged?.Invoke();
            SaveQueue();
        }
    }

    public void ClearUpcoming()
    {
        _queue.ClearUpcoming();
        QueueChanged?.Invoke();
        TrackChanged?.Invoke();
        SaveQueue();
    }

    public void MoveUpcoming(int from, int to)
    {
        _queue.MoveUpcoming(from, to);
        QueueChanged?.Invoke();
        SaveQueue();
    }

    public void SetShuffle(bool on)
    {
        _queue.SetShuffle(on);
        QueueChanged?.Invoke();
        SaveQueue();
    }

    public void CycleRepeat()
    {
        _queue.Repeat = _queue.Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        QueueChanged?.Invoke();
        SaveQueue();
    }

    public void SetAutoplay(bool on)
    {
        Autoplay = on;
        QueueChanged?.Invoke();
        TrackChanged?.Invoke();
        SaveQueue();
        if (on) _ = TopUpAsync();
    }

    /// <summary>More songs at the end, for a queue list scrolled to its end: the radio of the last song.</summary>
    public Task LoadMoreAsync() => Autoplay ? ExtendWithRadioAsync(thenAdvance: false) : Task.CompletedTask;

    public bool IsExtending => _extending;

    // MARK: internals

    private async Task PlayCurrentAsync(double resumeAt = 0)
    {
        if (_queue.Current is not { } item)
        {
            return;
        }

        var song = item.Song;
        var generation = ++_generation;
        _currentStream = null;
        _loaded = false;
        _resolving = true;
        _wantsToPlay = true;
        _failedOnce = false;
        _resumeAt = 0;
        _queue.TrimHistory(keeping: 50);
        TrackChanged?.Invoke();
        QueueChanged?.Invoke();
        PlayStateChanged?.Invoke();
        SaveQueue();

        _smtc?.UpdateTrack(song.Title, song.ArtistLine, song.Artwork?.Resized(96)?.ToString());
        _smtc?.SetTimeline(0, song.Duration ?? 0);
        _smtc?.UpdatePlaybackState(false);
        _smtc?.SetTransport(CanPrev, CanNext);

        ResolvedStream stream;
        try
        {
            // The whole song on disk plays from the file, with no connection needed.
            stream = App.AudioStore.CompleteStream(song.Id) ?? await EngineHost.Music.StreamAsync(song.Id);
        }
        catch (MusicException error)
        {
            if (generation != _generation) return;
            _resolving = false;
            _wantsToPlay = false;
            PlayStateChanged?.Invoke();
            Report(error);
            return;
        }

        if (generation != _generation)
        {
            return; // skipped past while resolving
        }

        _currentStream = stream;
        _player.Load(stream, song);
        _loaded = true;
        if (resumeAt > 1) _player.Seek(resumeAt);
        _player.Play();
        App.Library.RecordPlay(song);
        TrackChanged?.Invoke(); // the exact duration is known now
        _ = TopUpAsync();
    }

    /// <summary>Autoplay keeps something waiting: with the last songs reached, the radio follows.</summary>
    private Task TopUpAsync() =>
        Autoplay && _queue.Repeat == RepeatMode.Off && _queue.Upcoming.Count < 2
            ? ExtendWithRadioAsync(thenAdvance: false)
            : Task.CompletedTask;

    /// <summary>The queue ran (or is running) out: append the radio of the last song.</summary>
    private async Task ExtendWithRadioAsync(bool thenAdvance)
    {
        if (_extending || _queue.Items.Count == 0)
        {
            return;
        }

        _extending = true;
        QueueChanged?.Invoke();
        var anchor = _queue.Current?.Id;
        var last = _queue.Items[^1].Song;
        try
        {
            var radio = await EngineHost.Music.RadioAsync(last.Id);
            if (_queue.Current?.Id != anchor)
            {
                return;
            }

            var known = _queue.Items.TakeLast(100).Select(i => i.Song.Id).ToHashSet();
            var fresh = radio.Songs.Where(s => !known.Contains(s.Id)).ToArray();
            if (fresh.Length == 0)
            {
                return;
            }

            _queue.Append(fresh);
            SaveQueue();
            QueueChanged?.Invoke();
            TrackChanged?.Invoke();
            if (thenAdvance && _queue.Advance(automatic: false) is not null)
            {
                await PlayCurrentAsync();
            }
        }
        catch (MusicException error)
        {
            if (thenAdvance) StatusRaised?.Invoke(StatusTone.Fault, $"Autoplay couldn't find more songs. {Helpers.Errors.Describe(error)}");
        }
        finally
        {
            _extending = false;
            QueueChanged?.Invoke();
        }
    }

    private async Task OnEndedAsync()
    {
        var before = _queue.Current?.Id;
        if (_queue.Advance(automatic: true) is { } next)
        {
            if (next.Id == before)
            {
                // Repeat one: the same song again, already loaded.
                _player.Seek(0);
                _player.Play();
                return;
            }

            await PlayCurrentAsync();
            return;
        }

        if (Autoplay && _queue.Repeat == RepeatMode.Off)
        {
            await ExtendWithRadioAsync(thenAdvance: true);
            if (_queue.Current?.Id != before) return;
        }

        // The end of the queue: the song stays in the bar, paused at its start.
        _wantsToPlay = false;
        _player.Pause();
        _player.Seek(0);
        PlayStateChanged?.Invoke();
    }

    private async Task RecoverAsync(string reason)
    {
        if (CurrentSong is not { } song)
        {
            return;
        }

        // One honest retry: forget the URL (and the possibly refused visitor), resolve again.
        if (_failedOnce)
        {
            _wantsToPlay = false;
            _resolving = false;
            PlayStateChanged?.Invoke();
            StatusRaised?.Invoke(StatusTone.Fault, $"{song.Title} didn't play ({reason}).");
            return;
        }

        _failedOnce = true;
        App.AudioStore.Discard(song.Id);
        try
        {
            await EngineHost.Music.StreamFailedAsync(song.Id);
        }
        catch (MusicException error)
        {
            Report(error);
        }

        var keep = _failedOnce;
        await PlayCurrentAsync();
        _failedOnce = keep;
    }

    private void Report(MusicException error) =>
        StatusRaised?.Invoke(StatusTone.Fault, $"{Helpers.Errors.Title(error)}. {Helpers.Errors.Describe(error)}");

    // MARK: persistence (the Mac's persistQueue)

    private sealed record SavedState(PlayQueue.Saved Queue, double Position, bool Autoplay);

    private void SaveQueue()
    {
        try
        {
            var state = new SavedState(_queue.Save(), PositionSeconds, Autoplay);
            File.WriteAllText(QueuePath, JsonSerializer.Serialize(state));
        }
        catch (Exception)
        {
            // A queue that can't be saved still plays; it just won't come back next launch.
        }
    }

    private void RestoreQueue()
    {
        try
        {
            if (!File.Exists(QueuePath)) return;
            var state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(QueuePath));
            if (state?.Queue?.Songs is not { Length: > 0 }) return;
            _queue = PlayQueue.Restore(state.Queue);
            Autoplay = state.Autoplay;
            _resumeAt = state.Position;
            _loaded = false;
            _wantsToPlay = false;
        }
        catch (Exception)
        {
            // A damaged queue file starts an empty queue.
        }
    }

    /// <summary>Terminal shutdown: keep the queue and place, stop audio, hand back the SMTC card.</summary>
    public void Shutdown()
    {
        SaveQueue();
        _positionTimer?.Stop();
        App.Library.Flush();
        App.AudioStore.Flush();
        _player.Dispose();
        _smtc?.Reset();
        EngineHost.Music.Dispose();
    }
}
