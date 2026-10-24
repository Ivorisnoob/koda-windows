using System;
using System.Diagnostics;
using Koda.Core;
using Microsoft.UI.Dispatching;
using Windows.Devices.Enumeration;
using Windows.Media.Playback;

namespace Koda.Playback;

/// <summary>
/// The sound Koda makes, over up to three <see cref="Voice"/>s, the Mac Player's model:
/// <list type="bullet">
/// <item><b>main</b>, the song the queue says is playing;</item>
/// <item><b>standby</b>, the next song loaded and paused, so it can start the moment the
/// crossfade does (or, with crossfade off, the moment the song ends, without a gap);</item>
/// <item><b>tail</b>, the last main song playing on by itself while it fades out.</item>
/// </list>
/// Each voice's level is Koda's volume times the loudness levelling times its fade. The
/// fades are equal power (sine in, cosine out), so two songs together stay as loud as one.
///
/// Only the main voice speaks for playback: an end, a failure or a state change from a
/// voice that is no longer main is dropped, so a song fading out never moves the queue.
/// Every event is raised on the UI thread.
/// </summary>
public sealed class Player : IDisposable
{
    /// <summary>The fade a skip gets with crossfade on: long enough not to click, short enough to feel instant.</summary>
    public const double SkipFadeSeconds = 0.6;

    /// <summary>How long before the crossfade the next song is loaded: enough to buffer on a slow
    /// network, late enough that the playing song has usually been fetched whole, so repeat one
    /// reads its second copy from the file.</summary>
    private const double PreloadLeadSeconds = 30;

    private readonly Stopwatch _time = Stopwatch.StartNew();
    private DispatcherQueue? _dispatch;
    private DispatcherQueueTimer? _clock;
    private Voice? _main;
    private Voice? _standby;
    private Voice? _tail;
    /// <summary>The render device the listener chose; null follows the system default.</summary>
    private DeviceInformation? _device;
    private double _volume = 1;
    private bool _muted;
    private bool _normalizes = true;
    private double _crossfadeSeconds = 5;
    private bool _crossfades = true;
    /// <summary>The listener asked the main voice to play.</summary>
    private bool _playing;

    /// <summary>How far the main song has faded in, 0 to 1.</summary>
    private double _fadeGain = 1;
    private double _fadeInStart;
    private double _fadeInLength;

    /// <summary>The tail's level when it was let go, and its fade.</summary>
    private double _tailFrom;
    private double _tailStart;
    private double _tailLength;

    /// <summary>The main song's last seen position, to tell playback running across the
    /// crossfade's moment from a seek landing past it.</summary>
    private double _lastPosition = -1;
    private bool _crossfadeRaised;
    private bool _nearEndRaised;

    /// <summary>The main song reached its end.</summary>
    public event Action? Ended;

    /// <summary>The main song could not be read or played (human reason).</summary>
    public event Action<string>? Failed;

    /// <summary>The main song's playback state changed.</summary>
    public event Action<MediaPlayerState>? StateReached;

    /// <summary>Playback ran into the moment the crossfade should begin.</summary>
    public event Action? CrossfadeDue;

    /// <summary>The main song is close enough to its end that the next one should be loaded.</summary>
    public event Action? NearEnd;

    public void Connect(DispatcherQueue dispatch)
    {
        _dispatch = dispatch;
        _clock = dispatch.CreateTimer();
        _clock.Interval = TimeSpan.FromMilliseconds(33);
        _clock.Tick += (_, _) => Tick();
    }

    // MARK: reading

    public MediaPlayerState State => _main?.State ?? MediaPlayerState.Closed;

    public double PositionSeconds => _main?.PositionSeconds ?? 0;

    /// <summary>The stream the main song plays from.</summary>
    public ResolvedStream? Stream => _main?.Stream;

    /// <summary>The queue occurrence waiting on standby, if any.</summary>
    public Guid? StandbyOccurrence => _standby?.Occurrence;

    /// <summary>Whether a song changing now should fade: crossfade is on and sound is playing.</summary>
    public bool FadesSkips => _crossfades && _main?.IsSounding == true;

    /// <summary>Whether the main song is within reach of its end, where the next one is loaded.</summary>
    public bool IsNearEnd => _main is { } main && Remaining(main) is { } left && left <= Lead;

    /// <summary>The device in use, or null for the system default.</summary>
    public DeviceInformation? AudioDevice => _device;

    // MARK: settings

    /// <summary>Koda's own volume, 0 to 100, under the system's.</summary>
    public double Volume
    {
        get => _volume * 100;
        set
        {
            _volume = Math.Clamp(value, 0, 100) / 100;
            ApplyGains();
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            foreach (var voice in Voices())
            {
                voice.Muted = value;
            }
        }
    }

    /// <summary>Bring loud masters down to YouTube's loudness target.</summary>
    public bool NormalizesVolume
    {
        get => _normalizes;
        set
        {
            _normalizes = value;
            ApplyGains();
        }
    }

    public bool Crossfades
    {
        get => _crossfades;
        set
        {
            _crossfades = value;
            _crossfadeRaised = false;
        }
    }

    /// <summary>How long songs overlap, in seconds.</summary>
    public double CrossfadeSeconds
    {
        get => _crossfadeSeconds;
        set
        {
            _crossfadeSeconds = Math.Clamp(value, 1, 12);
            _crossfadeRaised = false;
        }
    }

    /// <summary>Play through a chosen device, now and for every song after it.
    /// Null is the system default (and follows a headset being plugged in).</summary>
    public void SetAudioDevice(DeviceInformation? device)
    {
        _device = device;
        foreach (var voice in Voices())
        {
            voice.SetAudioDevice(device);
        }
    }

    // MARK: loading

    /// <summary>Make <paramref name="song"/> the main song, starting at <paramref name="startAtSeconds"/>.
    /// With <paramref name="fadeIn"/> above zero the song that was playing fades out over it on
    /// the side while this one fades in; otherwise it stops at once.</summary>
    public void Load(ResolvedStream stream, Song song, Guid occurrence, double startAtSeconds = 0, double fadeIn = 0)
    {
        DiscardStandby();
        Retire(fadeIn);
        _main = NewVoice(stream, song, occurrence, startAtSeconds);
        StartFadeIn(fadeIn);
        _main.Open(App.AudioStore);
        if (_playing)
        {
            _main.Play();
        }
    }

    /// <summary>The song playing leaves ahead of the next one's load: fading out over
    /// <paramref name="fade"/> seconds on the side, or silenced at once.</summary>
    public void Leave(double fade)
    {
        DiscardStandby();
        Retire(fade);
    }

    /// <summary>Load the next song paused, to be switched to when the current one ends.</summary>
    public void Preload(ResolvedStream stream, Song song, Guid occurrence)
    {
        DiscardStandby();
        _standby = NewVoice(stream, song, occurrence, 0);
        _standby.Gain = 0;
        _standby.Open(App.AudioStore);
    }

    public void DiscardStandby()
    {
        _standby?.Dispose();
        _standby = null;
    }

    /// <summary>The standby song becomes the main one and plays, the old song fading out and
    /// the new one in over <paramref name="fade"/> seconds (none: straight on). False when
    /// nothing was waiting.</summary>
    public bool SwitchToStandby(double fade)
    {
        if (_standby is not { } next)
        {
            return false;
        }

        _standby = null;
        Retire(fade);
        _main = next;
        StartFadeIn(fade);
        _playing = true;
        next.Play();
        EnsureClock();
        return true;
    }

    // MARK: transport

    public void Play()
    {
        _playing = true;
        _main?.Play();
        EnsureClock();
    }

    public void Pause()
    {
        _playing = false;
        _main?.Pause();
        // A pause in the middle of a crossfade ends it: the old song is let go, the new one
        // is at its full level for when play is pressed again.
        FinishTail();
        CancelFadeIn();
    }

    public void Seek(double seconds)
    {
        _main?.Seek(seconds);
        _lastPosition = -1;
    }

    /// <summary>Silence every voice and let them go.</summary>
    public void Stop()
    {
        _playing = false;
        DiscardStandby();
        FinishTail();
        _main?.Dispose();
        _main = null;
        CancelFadeIn();
        _clock?.Stop();
    }

    // MARK: internals

    private double Lead => (_crossfades ? _crossfadeSeconds : 0) + PreloadLeadSeconds;

    private Voice NewVoice(ResolvedStream stream, Song song, Guid occurrence, double startAt)
    {
        var voice = new Voice(stream, song, occurrence, _device, startAt) { Muted = _muted };
        voice.Ended += v => OnUi(v, () => Ended?.Invoke());
        voice.Failed += (v, reason) =>
        {
            _dispatch?.TryEnqueue(() =>
            {
                if (v == _main)
                {
                    Failed?.Invoke(reason);
                }
                else if (v == _standby)
                {
                    // The next song failed to load ahead: it is loaded again when it is reached.
                    DiscardStandby();
                }
            });
        };
        voice.StateReached += (v, state) => OnUi(v, () =>
        {
            if (state == MediaPlayerState.Playing)
            {
                EnsureClock();
            }

            StateReached?.Invoke(state);
        });
        _lastPosition = -1;
        _crossfadeRaised = false;
        _nearEndRaised = false;
        return voice;
    }

    /// <summary>Run <paramref name="action"/> on the UI thread if <paramref name="voice"/> is still main then.</summary>
    private void OnUi(Voice voice, Action action) =>
        _dispatch?.TryEnqueue(() =>
        {
            if (voice == _main)
            {
                action();
            }
        });

    /// <summary>Let the main voice go: fading its song out over <paramref name="fade"/> seconds when
    /// it is sounding, otherwise silenced at once.</summary>
    private void Retire(double fade)
    {
        // Nothing playing to let go: a song already fading out (a skip's, before the next song
        // loaded) keeps fading rather than being cut off by the load.
        if (_main is not { } old)
        {
            return;
        }

        FinishTail();
        _main = null;
        if (fade <= 0 || !old.IsSounding)
        {
            old.Dispose();
            return;
        }

        _tail = old;
        _tailFrom = MainGain(old);
        _tailStart = Now;
        _tailLength = fade;
        EnsureClock();
    }

    private void FinishTail()
    {
        _tail?.Dispose();
        _tail = null;
    }

    private void StartFadeIn(double fade)
    {
        if (fade <= 0)
        {
            CancelFadeIn();
            return;
        }

        _fadeGain = 0;
        _fadeInStart = Now;
        _fadeInLength = fade;
        ApplyGains();
        EnsureClock();
    }

    /// <summary>Back to full level at once.</summary>
    private void CancelFadeIn()
    {
        _fadeInLength = 0;
        _fadeGain = 1;
        ApplyGains();
    }

    private double Now => _time.Elapsed.TotalSeconds;

    /// <summary>The levelling a song gets: loud masters come down to -14 LUFS; quiet ones are
    /// left alone rather than boosted, and nothing drops below 40%.</summary>
    private double Leveling(Voice voice) =>
        _normalizes && voice.Stream.LoudnessDb is { } db && db > 0
            ? Math.Max(0.4, Math.Pow(10, -db / 20))
            : 1;

    private double MainGain(Voice voice) => _volume * Leveling(voice) * _fadeGain;

    private void ApplyGains()
    {
        if (_main is { } main)
        {
            main.Gain = MainGain(main);
        }
    }

    private System.Collections.Generic.IEnumerable<Voice> Voices()
    {
        if (_main is not null) yield return _main;
        if (_standby is not null) yield return _standby;
        if (_tail is not null) yield return _tail;
    }

    private static double? Remaining(Voice voice)
    {
        var duration = voice.Stream.Duration ?? voice.Song.Duration ?? 0;
        return duration > 0 ? duration - voice.PositionSeconds : null;
    }

    private void EnsureClock()
    {
        if (_clock is not { } clock)
        {
            return;
        }

        if (_fadeInLength > 0 || _tail is not null)
        {
            clock.Interval = TimeSpan.FromMilliseconds(33);
        }

        if (!clock.IsRunning)
        {
            clock.Start();
        }
    }

    /// <summary>About thirty times a second while anything is moving: the fades, and the watch
    /// for the end of the main song.</summary>
    private void Tick()
    {
        var now = Now;
        if (_fadeInLength > 0)
        {
            var progress = Math.Min(1, (now - _fadeInStart) / _fadeInLength);
            _fadeGain = Math.Sin(progress * Math.PI / 2);
            ApplyGains();
            if (progress >= 1)
            {
                _fadeInLength = 0;
            }
        }

        if (_tail is { } tail)
        {
            var progress = Math.Min(1, (now - _tailStart) / _tailLength);
            tail.Gain = _tailFrom * Math.Cos(progress * Math.PI / 2);
            if (progress >= 1)
            {
                FinishTail();
            }
        }

        WatchEnd();

        var fading = _fadeInLength > 0 || _tail is not null;
        if (!_playing && !fading)
        {
            _clock?.Stop();
        }
        else if (_clock is { } clock)
        {
            // Smooth while a fade runs; a few looks a second are enough to watch for the end.
            clock.Interval = TimeSpan.FromMilliseconds(fading ? 33 : 150);
        }
    }

    private void WatchEnd()
    {
        if (_main is not { } main || !_playing || Remaining(main) is not { } left)
        {
            return;
        }

        var duration = main.Stream.Duration ?? main.Song.Duration ?? 0;
        var position = duration - left;

        if (left <= Lead)
        {
            if (!_nearEndRaised)
            {
                _nearEndRaised = true;
                NearEnd?.Invoke();
            }
        }
        else if (left > Lead + 5)
        {
            // Seeked back from the end: the next song is loaded again when the end comes round.
            _nearEndRaised = false;
        }

        // A short song keeps most of itself unfaded.
        if (_crossfades && duration > _crossfadeSeconds * 2 + 10 && main.IsSounding)
        {
            var boundary = duration - _crossfadeSeconds;
            if (position < boundary)
            {
                _crossfadeRaised = false;
            }
            // Only playback running across the moment starts the fade; a seek that lands past it
            // lets the song end, and the next one goes straight on.
            else if (!_crossfadeRaised && _lastPosition >= 0 && _lastPosition < boundary && position - _lastPosition < 1.5)
            {
                _crossfadeRaised = true;
                CrossfadeDue?.Invoke();
            }
        }

        _lastPosition = main.IsSounding ? position : -1;
    }

    public void Dispose()
    {
        Stop();
    }
}
