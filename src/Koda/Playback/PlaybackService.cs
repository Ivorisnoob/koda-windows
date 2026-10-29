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
///
/// The next song is loaded ahead on the player's standby voice as the current one nears its
/// end. With crossfade on it fades in over the end of the current song (repeat one included:
/// the standby is the same song again, from its start); with crossfade off it starts the
/// moment the current one ends, without the gap a fresh resolve would leave.
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

    /// <summary>How long the player may wait on the network, wanting to play, before the song is
    /// reloaded where it was; half that for a song that never got going.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

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
    /// <summary>Seconds of sound since the last retry.</summary>
    private double _heardSinceRetry;
    private bool _recovering;
    private bool _extending;
    /// <summary>Bumped per play of a song (a repeat round included), so a slow resolve for a song
    /// skipped past is dropped and each play is reported once.</summary>
    private int _generation;
    /// <summary>Bumped per load ahead, so a slow resolve for a next song that changed is dropped.</summary>
    private int _nextGeneration;
    private int _reportedGeneration = -1;
    private double _resumeAt;
    /// <summary>Since when the player has been waiting on the network while the listener wants sound.</summary>
    private DateTimeOffset? _stalledSince;
    private DispatcherQueueTimer? _sleepTimer;
    private DateTimeOffset? _sleepAt;
    private int? _sleepMinutes;
    private bool _sleepAtEnd;

    /// <summary>The sleep timer's presets, in minutes.</summary>
    public static readonly int[] SleepPresets = [5, 10, 15, 30, 45, 60, 90];

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

    /// <summary>The sleep timer was set, ticked, or cleared: the pill follows the countdown.</summary>
    public event Action? SleepChanged;

    /// <summary>A queue occurrence started making sound, once per play: a song repeated is a new
    /// play. For Last.fm's "now playing" and its scrobble.</summary>
    public event Action<QueueItem>? ItemStarted;

    /// <summary>Seconds of the occurrence actually heard since the last call.</summary>
    public event Action<QueueItem, double>? ItemHeard;

    public Song? CurrentSong => _queue.Current?.Song;
    public Song? NextSong => _queue.PeekNext(automatic: false)?.Song;

    public PlayQueue Queue => _queue;

    /// <summary>What the listener asked for: the play button follows this, not the decoder's state.</summary>
    public bool IsPlaying => _wantsToPlay;

    /// <summary>Asked to play and no sound yet: resolving, opening or buffering.</summary>
    public bool IsLoading => _wantsToPlay && (_resolving ||
        _player.State is MediaPlayerState.Opening or MediaPlayerState.Buffering ||
        (_loaded && _player.State == MediaPlayerState.Closed));

    public bool IsMuted { get; private set; }

    /// <summary>When the queue ends, the radio of the last song keeps it going. The listener's
    /// choice, kept with the other settings.</summary>
    public bool Autoplay
    {
        get => Koda.Core.Settings.Current.Autoplay;
        set => Koda.Core.Settings.Current.Update(s => s.Autoplay = value);
    }

    public bool CanNext => _queue.PeekNext(automatic: false) is not null || (Autoplay && CurrentSong is not null);

    public bool CanPrev => CurrentSong is not null;

    public double PositionSeconds => _loaded ? _player.PositionSeconds : _resumeAt;

    public double DurationSeconds => _currentStream?.Duration ?? CurrentSong?.Duration ?? 0;

    /// <summary>Connect the service to the UI thread and SMTC; restores the last queue, paused.</summary>
    public void Connect(DispatcherQueue dispatch, SmtcController smtc)
    {
        _dispatch = dispatch;
        _smtc = smtc;

        // The player raises everything on the UI thread already.
        _player.Connect(dispatch);
        _player.Ended += () => _ = OnEndedAsync();
        _player.Failed += reason => _ = RecoverAsync(reason);
        _player.StateReached += OnStateReached;
        _player.NearEnd += () => _ = PrepareNextAsync();
        _player.CrossfadeDue += OnCrossfadeDue;
        ApplyAudioSettings(Koda.Core.Settings.Current);
        Koda.Core.Settings.Current.Changed += settings => dispatch.TryEnqueue(() => ApplyAudioSettings(settings));

        // A 429 arms a hold on background fan-out only; the shell shows it as a ribbon.
        EngineHost.Music.Gate.Throttled += hold => dispatch.TryEnqueue(() => StatusRaised?.Invoke(
            StatusTone.Pacing,
            $"YouTube asked Koda to slow down - background refreshes pause for {Math.Ceiling(hold.TotalMinutes):F0} min"));

        _positionTimer = dispatch.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(500);
        _positionTimer.Tick += (_, _) => OnPositionTimer();
        _positionTimer.Start();

        // The sleep timer counts wall-clock seconds, even while paused: it is the listener's
        // "stop by then", not a count of played time.
        _sleepTimer = dispatch.CreateTimer();
        _sleepTimer.Interval = TimeSpan.FromSeconds(1);
        _sleepTimer.Tick += (_, _) => TickSleep();

        RestoreQueue();
        _ = RestoreAudioDeviceAsync();
    }

    private void ApplyAudioSettings(Koda.Core.Settings settings)
    {
        _player.NormalizesVolume = settings.NormalizesVolume;
        _player.Crossfades = settings.Crossfades;
        _player.CrossfadeSeconds = settings.CrossfadeSeconds;
        // A crossfade turned on near the end needs the next song loaded now, not next song.
        _ = PrepareNextAsync();
    }

    private void OnStateReached(MediaPlayerState state)
    {
        if (state == MediaPlayerState.Playing)
        {
            _resolving = false;
            ReportStart();
        }

        PlayStateChanged?.Invoke();
        _smtc?.UpdatePlaybackState(state == MediaPlayerState.Playing);
    }

    private void OnPositionTimer()
    {
        PositionTick?.Invoke();
        var sounding = _player.State == MediaPlayerState.Playing;
        if (_queue.Current is { } item && sounding)
        {
            App.Library.AddListening(0.5, item.Song);
            ItemHeard?.Invoke(item, 0.5);

            // A retried song that has played well for a while has earned another retry: a
            // stall much later in a long song is a new problem, not the same one again.
            if (_failedOnce && (_heardSinceRetry += 0.5) >= 30)
            {
                _failedOnce = false;
            }
        }

        _smtc?.SetTimeline(PositionSeconds, DurationSeconds);
        WatchForStall(sounding);
    }

    /// <summary>The Mac's stall watchdog: waiting on the network too long while the listener wants
    /// sound reloads the song where it was, rather than leaving it spinning for ever.</summary>
    private void WatchForStall(bool sounding)
    {
        if (!_wantsToPlay || !_loaded || _recovering || sounding || CurrentSong is null)
        {
            _stalledSince = null;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _stalledSince ??= now;
        var timeout = PositionSeconds > 0.5 ? StallTimeout : StallTimeout / 2;
        if (now - _stalledSince.Value >= timeout)
        {
            _stalledSince = null;
            _ = RecoverAsync("the stream stalled");
        }
    }

    /// <summary>The output device the listener chose; null is the system default.</summary>
    public string? AudioDeviceId => Koda.Core.Settings.Current.AudioDeviceId;

    /// <summary>Play through a device from now on, and remember it. Choosing the
    /// system default clears the saved id, so a new headset is followed.</summary>
    public async Task SetAudioDeviceAsync(string? id)
    {
        Koda.Core.Settings.Current.Update(s => s.AudioDeviceId = id);
        var device = await Interop.AudioOutputs.FindAsync(id);
        _player.SetAudioDevice(device);
        if (!string.IsNullOrEmpty(id) && device is null)
        {
            Koda.Core.Settings.Current.Update(s => s.AudioDeviceId = null);
            StatusRaised?.Invoke(StatusTone.Pacing,
                "That audio device isn't connected - playing through the system default.");
        }
    }

    private async Task RestoreAudioDeviceAsync()
    {
        var id = Koda.Core.Settings.Current.AudioDeviceId;
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var device = await Interop.AudioOutputs.FindAsync(id);
        _player.SetAudioDevice(device);
        if (device is null)
        {
            Koda.Core.Settings.Current.Update(s => s.AudioDeviceId = null);
        }
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
            var songs = radio.Songs.Where(s => s.Id == seed.Id || !App.Library.IsUnwanted(s)).ToArray();
            await PlayNowAsync(songs.Length > 0 ? songs[0] : seed, songs.Length > 0 ? songs : [seed]);
        }
        catch (MusicException error)
        {
            StatusRaised?.Invoke(StatusTone.Fault, $"The radio for {seed.Title} didn't start. {Helpers.Errors.Describe(error)}");
        }
    }

    /// <summary>
    /// A link's video id carries no title or artist; its radio starts with the song
    /// itself, which has both.
    /// </summary>
    public async Task PlayLinkedSongAsync(string videoId)
    {
        try
        {
            var radio = await EngineHost.Music.RadioAsync(videoId);
            var seed = radio.Songs.FirstOrDefault(s => s.Id == videoId) ?? radio.Songs.FirstOrDefault();
            if (seed is null)
            {
                StatusRaised?.Invoke(StatusTone.Fault, "That song isn't available.");
                return;
            }

            var songs = new[] { seed }
                .Concat(radio.Songs.Where(s => s.Id != seed.Id && !App.Library.IsUnwanted(s)))
                .ToArray();
            await PlayNowAsync(seed, songs);
        }
        catch (MusicException error)
        {
            StatusRaised?.Invoke(StatusTone.Fault, $"That song didn't play. {Helpers.Errors.Describe(error)}");
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
        QueueEdited();
        TrackChanged?.Invoke(); // Next becomes available
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
        QueueEdited();
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
            SaveQueue(); // the place survives a crash while paused
        }
        else if (!_loaded)
        {
            // After a relaunch the song is shown but not loaded; Play loads it where it was.
            _ = PlayCurrentAsync(resumeAt: _resumeAt, fade: false);
        }
        else if (EnsureOutput())
        {
            _wantsToPlay = true;
            _player.Play();
        }

        PlayStateChanged?.Invoke();
    }

    /// <summary>With no speakers or headphones connected there is nothing to play through: the
    /// listener is told at once, rather than left watching a song load that can never sound.</summary>
    private bool EnsureOutput()
    {
        if (Interop.AudioOutputs.HasOutput)
        {
            return true;
        }

        StatusRaised?.Invoke(StatusTone.Fault,
            "No speakers or headphones are connected. Connect one, then press Play.");
        return false;
    }

    public async Task NextAsync()
    {
        if (CurrentSong is null)
        {
            return;
        }

        // The next song already waiting on standby starts at once, with the skip's short fade.
        if (_loaded && _queue.PeekNext(automatic: false) is { } next && _player.StandbyOccurrence == next.Id)
        {
            _queue.Advance(automatic: false);
            _wantsToPlay = true;
            BeginStandby(_player.FadesSkips ? Player.SkipFadeSeconds : 0);
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
        PositionTick?.Invoke();
    }

    /// <summary>
    /// Jump within the playing song, by the listener's interval. A jump past the end
    /// stops at the end rather than leaving the track: the skip buttons move inside the
    /// song, next moves between songs, as on the Mac.
    /// </summary>
    public void Skip(double seconds)
    {
        if (CurrentSong is null)
        {
            return;
        }

        var target = PositionSeconds + seconds;
        var duration = DurationSeconds;
        if (duration > 0)
        {
            target = Math.Min(target, Math.Max(0, duration - 0.5));
        }

        SeekSeconds(Math.Max(0, target));
    }

    // MARK: sleep timer

    /// <summary>Whether a sleep timer is set, either a time or the end of the song.</summary>
    public bool IsSleepArmed => _sleepAt is not null || _sleepAtEnd;

    /// <summary>The countdown's length, kept so the menu can show which preset is set.</summary>
    public int? SleepMinutes => _sleepMinutes;

    /// <summary>Whether the timer stops after the song rather than at a time.</summary>
    public bool SleepAtEnd => _sleepAtEnd;

    /// <summary>The pill's words: the time left, or "End of song".</summary>
    public string? SleepLabel
    {
        get
        {
            if (_sleepAtEnd)
            {
                return "End of song";
            }

            if (_sleepAt is not { } at)
            {
                return null;
            }

            var left = at - DateTimeOffset.Now;
            if (left <= TimeSpan.Zero)
            {
                return "0:00 left";
            }

            var total = (int)Math.Ceiling(left.TotalSeconds);
            return total >= 3600
                ? $"{total / 3600}:{total % 3600 / 60:D2}:{total % 60:D2} left"
                : $"{total / 60}:{total % 60:D2} left";
        }
    }

    /// <summary>Stops playback after this many minutes.</summary>
    public void SleepIn(int minutes)
    {
        if (minutes <= 0)
        {
            return;
        }

        _sleepAt = DateTimeOffset.Now.AddMinutes(minutes);
        _sleepMinutes = minutes;
        _sleepAtEnd = false;
        _sleepTimer?.Start();
        SleepChanged?.Invoke();
        StatusRaised?.Invoke(StatusTone.Pacing, $"Sleep timer: {minutes} minutes");
    }

    /// <summary>Stops playback when the song playing now finishes. The song ends on its own,
    /// without crossfading into the next.</summary>
    public void SleepAtEndOfSong()
    {
        _sleepAt = null;
        _sleepMinutes = null;
        _sleepAtEnd = true;
        _sleepTimer?.Stop();
        _player.DiscardStandby();
        SleepChanged?.Invoke();
        StatusRaised?.Invoke(StatusTone.Pacing, "Sleep timer: after this song");
    }

    /// <summary>Turns the sleep timer off without touching playback.</summary>
    public void CancelSleep()
    {
        if (!IsSleepArmed)
        {
            return;
        }

        _sleepAt = null;
        _sleepMinutes = null;
        _sleepAtEnd = false;
        _sleepTimer?.Stop();
        SleepChanged?.Invoke();
        StatusRaised?.Invoke(StatusTone.Pacing, "Sleep timer off");
        _ = PrepareNextAsync();
    }

    /// <summary>Called once a second while a timed sleep timer is set; the pill counts down.</summary>
    private void TickSleep()
    {
        if (_sleepAt is not { } at)
        {
            _sleepTimer?.Stop();
            return;
        }

        if (DateTimeOffset.Now < at)
        {
            SleepChanged?.Invoke();
            return;
        }

        _sleepAt = null;
        _sleepMinutes = null;
        _sleepTimer?.Stop();
        if (_wantsToPlay)
        {
            _wantsToPlay = false;
            _player.Pause();
            PlayStateChanged?.Invoke();
            SaveQueue();
        }

        SleepChanged?.Invoke();
        StatusRaised?.Invoke(StatusTone.Pacing, "Sleep timer ended: playback paused.");
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
            QueueEdited();
        }
    }

    public void ClearUpcoming()
    {
        _queue.ClearUpcoming();
        QueueEdited();
        TrackChanged?.Invoke();
    }

    public void MoveUpcoming(int from, int to)
    {
        _queue.MoveUpcoming(from, to);
        QueueEdited();
    }

    public void SetShuffle(bool on)
    {
        _queue.SetShuffle(on);
        QueueEdited();
    }

    public void CycleRepeat()
    {
        _queue.Repeat = _queue.Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        QueueEdited();
    }

    public void SetAutoplay(bool on)
    {
        Autoplay = on;
        QueueEdited();
        TrackChanged?.Invoke();
        if (on) _ = TopUpAsync();
    }

    /// <summary>More songs at the end, for a queue list scrolled to its end: the radio of the last song.</summary>
    public Task LoadMoreAsync() => Autoplay ? ExtendWithRadioAsync(thenAdvance: false) : Task.CompletedTask;

    public bool IsExtending => _extending;

    /// <summary>The queue changed shape: redraw, keep it, and make sure what waits on standby is
    /// still the song that plays next.</summary>
    private void QueueEdited()
    {
        QueueChanged?.Invoke();
        SaveQueue();
        _ = PrepareNextAsync();
    }

    // MARK: internals

    /// <summary>Load and play the queue's current song. <paramref name="fade"/> is a change of
    /// song the listener asked for: with crossfade on, the song playing fades out quickly while
    /// this one loads and fades in. A reload of the same song after a failure never fades.</summary>
    private async Task PlayCurrentAsync(double resumeAt = 0, bool fade = true)
    {
        if (_queue.Current is not { } item)
        {
            return;
        }

        var song = item.Song;
        var generation = ++_generation;
        // The old song leaves now, not when the new one has resolved: a skip is heard at once.
        var fadeIn = fade && _player.FadesSkips ? Player.SkipFadeSeconds : 0;
        _player.Leave(fadeIn);
        _currentStream = null;
        _loaded = false;
        _resolving = true;
        _wantsToPlay = true;
        _failedOnce = false;
        _stalledSince = null;
        _resumeAt = resumeAt;
        _queue.TrimHistory(keeping: 50);
        if (!Interop.AudioOutputs.HasOutput)
        {
            // The song is shown, ready where it is; Play loads it once there is a way to hear it.
            _resolving = false;
            _wantsToPlay = false;
        }

        TrackChanged?.Invoke();
        QueueChanged?.Invoke();
        PlayStateChanged?.Invoke();
        SaveQueue();
        UpdateSmtcTrack(song);
        if (!EnsureOutput())
        {
            return;
        }

        ResolvedStream stream;
        try
        {
            // The whole song on disk plays from the file, with no connection needed.
            stream = await ResolveAsync(song);
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
        // The start position travels with the load: the media opens a moment later, and the
        // song must not sound its first second before it moves.
        _player.Load(stream, song, item.Id, resumeAt, fadeIn);
        _loaded = true;
        _player.Play();
        BeganSong(song);
    }

    private static async Task<ResolvedStream> ResolveAsync(Song song) =>
        App.AudioStore.CompleteStream(song.Id) ?? await EngineHost.Music.StreamAsync(song.Id);

    /// <summary>What every new play of a song does once its audio is in the player.</summary>
    private void BeganSong(Song song)
    {
        App.Library.RecordPlay(song);
        App.Library.Flush(); // the listening time of the finished song is on disk now
        TrackChanged?.Invoke(); // the exact duration is known now
        PlayStateChanged?.Invoke();
        Koda.Services.LyricsService.Prefetch(song); // the words are ready before anyone asks
        _ = TopUpAsync();
        _ = PrepareNextAsync();
    }

    /// <summary>The queue has already moved to the song waiting on standby: it becomes the
    /// playing song, the last one fading out over <paramref name="fade"/> seconds.</summary>
    private void BeginStandby(double fade)
    {
        if (_queue.Current is not { } item || !_player.SwitchToStandby(fade))
        {
            _ = PlayCurrentAsync();
            return;
        }

        ++_generation;
        _currentStream = _player.Stream;
        _loaded = true;
        _resolving = false;
        _failedOnce = false;
        _stalledSince = null;
        _resumeAt = 0;
        _queue.TrimHistory(keeping: 50);
        QueueChanged?.Invoke();
        SaveQueue();
        UpdateSmtcTrack(item.Song);
        BeganSong(item.Song);
    }

    private void UpdateSmtcTrack(Song song)
    {
        _smtc?.UpdateTrack(song.Title, song.ArtistLine, song.Artwork?.Resized(96)?.ToString());
        _smtc?.SetTimeline(0, song.Duration ?? 0);
        _smtc?.UpdatePlaybackState(false);
        _smtc?.SetTransport(CanPrev, CanNext);
    }

    /// <summary>Tell Last.fm the current song started, once per play.</summary>
    private void ReportStart()
    {
        if (_reportedGeneration == _generation || _queue.Current is not { } item)
        {
            return;
        }

        _reportedGeneration = _generation;
        ItemStarted?.Invoke(item);
    }

    /// <summary>Keep the song that plays next loaded on standby, once the current one is near its
    /// end: under repeat one that is the same song again, from its start.</summary>
    private async Task PrepareNextAsync()
    {
        if (!_loaded || _queue.Current is not { } current || !_player.IsNearEnd)
        {
            return;
        }

        var next = _sleepAtEnd ? null : _queue.PeekNext(automatic: true);
        if (next is null)
        {
            _player.DiscardStandby();
            return;
        }

        if (_player.StandbyOccurrence == next.Id)
        {
            return;
        }

        _player.DiscardStandby();
        var token = ++_nextGeneration;
        ResolvedStream stream;
        try
        {
            // Repeat one plays the same bytes again: the stream already playing, never a fresh
            // resolve that could pick another format and start the song's file over under it.
            stream = next.Song.Id == current.Song.Id && _currentStream is { } same
                ? App.AudioStore.CompleteStream(same.VideoId) ?? same
                : await ResolveAsync(next.Song);
        }
        catch (MusicException)
        {
            return; // loaded again, the ordinary way, when it is reached
        }

        if (token != _nextGeneration || !_loaded || _queue.Current?.Id != current.Id ||
            _queue.PeekNext(automatic: true)?.Id != next.Id || _sleepAtEnd)
        {
            return;
        }

        _player.Preload(stream, next.Song, next.Id);
    }

    /// <summary>Playback ran into the crossfade's moment: the song waiting on standby fades in over
    /// what is left, as long as it is the song the queue would play anyway.</summary>
    private void OnCrossfadeDue()
    {
        if (!_wantsToPlay || _sleepAtEnd || _queue.PeekNext(automatic: true) is not { } next ||
            _player.StandbyOccurrence != next.Id)
        {
            return;
        }

        var left = DurationSeconds - PositionSeconds;
        var fade = Math.Min(_player.CrossfadeSeconds, Math.Max(1, left));
        _queue.Advance(automatic: true);
        BeginStandby(fade);
    }

    /// <summary>Autoplay keeps something waiting: with the last songs reached, the radio follows.</summary>
    private Task TopUpAsync() =>
        Autoplay && _queue.Repeat == RepeatMode.Off && _queue.Upcoming.Count < 2
            ? ExtendWithRadioAsync(thenAdvance: false)
            : Task.CompletedTask;

    /// <summary>The queue ran (or is running) out: append the radio of the last song, leaving out
    /// the songs the listener asked to hear less of.</summary>
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
            var fresh = radio.Songs.Where(s => !known.Contains(s.Id) && !App.Library.IsUnwanted(s)).ToArray();
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
            else
            {
                _ = PrepareNextAsync();
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
        // A player that runs out of samples says "ended" for two reasons: the song
        // finished, or the bytes stopped coming (a refused range, a stall). Only the
        // second one must not move the queue on - otherwise the listener loses the
        // song they were in and another one starts from its first second.
        if (Koda.Core.Playback.TrackEnd.LooksEarly(PositionSeconds, DurationSeconds))
        {
            await RecoverAsync("the audio stream ended early");
            return;
        }

        // The sleep timer's "end of song": the queue moves on to the next song, paused and
        // ready, as on the Mac. Play picks it up from its start.
        if (_sleepAtEnd)
        {
            _sleepAtEnd = false;
            _wantsToPlay = false;
            if (_queue.Advance(automatic: true) is not null)
            {
                _player.Stop();
                _loaded = false;
                _currentStream = null;
                _resumeAt = 0;
                TrackChanged?.Invoke();
                QueueChanged?.Invoke();
                SaveQueue();
            }
            else
            {
                _player.Pause();
                _player.Seek(0);
            }

            PlayStateChanged?.Invoke();
            SleepChanged?.Invoke();
            StatusRaised?.Invoke(StatusTone.Pacing, "Sleep timer ended: stopped after the song.");
            return;
        }

        // Ended without a crossfade (crossfade off, seeked past its moment, or a short song):
        // the song waiting on standby goes straight on.
        if (_queue.PeekNext(automatic: true) is { } waiting && _player.StandbyOccurrence == waiting.Id)
        {
            _queue.Advance(automatic: true);
            BeginStandby(0);
            return;
        }

        var before = _queue.Current?.Id;
        if (_queue.Advance(automatic: true) is { } next)
        {
            if (next.Id == before)
            {
                // Repeat one with nothing waiting: the same song again, already loaded. It is
                // a new play for history and Last.fm.
                ++_generation;
                _player.Seek(0);
                _player.Play();
                BeganSong(next.Song);
                return;
            }

            await PlayCurrentAsync(fade: false);
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

        // A broken stream says so twice: the player fails and the source runs dry.
        // One recovery at a time, or the song would be resolved and loaded twice.
        if (_recovering)
        {
            return;
        }

        // One honest retry: forget the URL (and the possibly refused visitor), resolve again.
        // Nothing to play through is not a broken stream: no retry fixes it.
        if (_failedOnce || !Interop.AudioOutputs.HasOutput)
        {
            _wantsToPlay = false;
            _resolving = false;
            _player.Pause();
            PlayStateChanged?.Invoke();
            if (!EnsureOutput()) return;
            StatusRaised?.Invoke(StatusTone.Fault, $"{song.Title} didn't play ({reason}).");
            return;
        }

        // Where the listener was when the stream broke. The retry continues the song
        // from there - a stream that fails a minute in must not send the listener back
        // to the first second of the song they were in.
        var resumeAt = PositionSeconds;
        var generation = _generation;

        _recovering = true;
        _failedOnce = true;
        // Only a whole file that fails to play is damaged and fetched again; the bytes of a
        // stream that broke part way are good and stay, so the retry does not start over.
        if (App.AudioStore.GetEntry(song.Id)?.IsComplete == true)
        {
            App.AudioStore.Discard(song.Id);
        }

        try
        {
            await EngineHost.Music.StreamFailedAsync(song.Id);
        }
        catch (MusicException error)
        {
            Report(error);
        }

        var reloaded = false;
        try
        {
            // The listener moved on while the identity was refreshed: nothing to recover.
            if (generation == _generation && CurrentSong?.Id == song.Id)
            {
                reloaded = true;
                await PlayCurrentAsync(resumeAt: resumeAt, fade: false);
            }
        }
        finally
        {
            _recovering = false;
            // The reload is this song's one retry; another failure soon after gives up.
            if (reloaded && CurrentSong?.Id == song.Id)
            {
                _failedOnce = true;
                _heardSinceRetry = 0;
            }
        }
    }

    private void Report(MusicException error) =>
        StatusRaised?.Invoke(StatusTone.Fault, $"{Helpers.Errors.Title(error)}. {Helpers.Errors.Describe(error)}");

    // MARK: persistence (the Mac's persistQueue)

    private sealed record SavedState(PlayQueue.Saved Queue, double Position);

    private void SaveQueue()
    {
        try
        {
            var state = new SavedState(_queue.Save(), PositionSeconds);
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
            _resumeAt = state.Position;
            _loaded = false;
            _wantsToPlay = false;
        }
        catch (Exception)
        {
            // A damaged queue file starts an empty queue.
        }
    }

    /// <summary>Write the queue and the place without stopping anything: the end of a
    /// Windows session is not the listener asking Koda to quit.</summary>
    public void SaveState() => SaveQueue();

    /// <summary>Terminal shutdown: keep the queue and place, stop audio, hand back the SMTC card.</summary>
    public void Shutdown()
    {
        SaveQueue();
        _positionTimer?.Stop();
        App.Library.Flush();
        App.AudioStore.Flush();
        _player.Dispose();
        _smtc?.Reset();
        // The Discord card comes down with the music; downloads pick up again next launch.
        App.Discord.Dispose();
        App.Downloads.Dispose();
        EngineHost.Music.Dispose();
    }
}
