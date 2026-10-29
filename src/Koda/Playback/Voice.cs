using System;
using System.Threading;
using System.Threading.Tasks;
using Koda.Core;
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Koda.Playback;

/// <summary>
/// One song on one Media Foundation player: the OS Windows.Media.Playback.MediaPlayer
/// with CommandManager DISABLED, so it never floats its own SMTC card and the system
/// flyout keeps exactly one Koda entry (the window-bound SmtcController).
///
/// A voice lives for one load of one song. <see cref="Player"/> keeps up to three at
/// once: the song playing, the next one waiting for its crossfade, and the last one
/// fading out. Swapping Source on a live MediaPlayer surfaces the old source's
/// MediaEnded, which the queue would read as a real track end, so a song is never
/// swapped into an existing voice.
///
/// The bytes ride our own transport (Koda.Core's CachingLoader over our ranged
/// HttpClient), because Media Foundation's built-in fetch of googlevideo URLs cannot
/// survive inside the WinUI host; the store keeps them on disk for the next play.
/// </summary>
internal sealed class Voice : IDisposable
{
    private readonly MediaPlayer _player;
    private readonly object _gate = new();
    private MediaSource? _source;
    private Koda.Core.Playback.CachingLoader? _loader;
    private bool _wantsPlay;
    /// <summary>The media opened: from here a position is applied at once.</summary>
    private bool _opened;
    /// <summary>Where the song must start once it opens; also what Position reads until then.</summary>
    private double _pendingPosition;
    /// <summary>The level asked for; held at silence until the media opens at its start position.</summary>
    private double _gain;
    private bool _disposed;

    /// <summary>The song reached its end.</summary>
    public event Action<Voice>? Ended;

    /// <summary>The song could not be read or played (human reason).</summary>
    public event Action<Voice, string>? Failed;

    /// <summary>Every playback-state change.</summary>
    public event Action<Voice, MediaPlayerState>? StateReached;

    /// <summary>The queue occurrence this voice plays.</summary>
    public Guid Occurrence { get; }

    public Song Song { get; }

    public ResolvedStream Stream { get; }

    public Voice(ResolvedStream stream, Song song, Guid occurrence, DeviceInformation? device, double startAtSeconds)
    {
        Stream = stream;
        Song = song;
        Occurrence = occurrence;
        _pendingPosition = Math.Max(0, startAtSeconds);

        _player = new MediaPlayer { AutoPlay = false, Volume = 0 };
        // CommandManager off: the system flyout must stay ours.
        _player.CommandManager.IsEnabled = false;
        if (device is not null)
        {
            try
            {
                _player.AudioDevice = device;
            }
            catch (Exception)
            {
                // The device went away between the menu and the load: system default.
            }
        }

        _player.MediaOpened += OnMediaOpened;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        _player.CurrentStateChanged += OnCurrentStateChanged;
    }

    /// <summary>Start reading the song. YouTube's audio is fragmented MP4 (DASH), which Media
    /// Foundation's MP4 source opens and then ends at once; the frames are fed to it through
    /// a MediaStreamSource instead (see Fmp4Audio). The container's head is read first, so the
    /// source attaches a moment after this returns.
    /// [verified October 2026: file URI and stream both ended at 0 s; frames play and seek]</summary>
    public void Open(Koda.Core.Storage.AudioStore store)
    {
        if (Stream.Url.IsFile)
        {
            // A song held whole on disk: the same frames, read from the file.
            _ = OpenFileFramesAsync(Stream.Url.LocalPath);
        }
        else if (Koda.Core.Playback.CachingLoader.Create(Stream, Song, store) is { } loader)
        {
            _loader = loader;
            _ = OpenFramesAsync(loader, Stream.ContentLength!.Value);
        }
        else
        {
            // The Swift nil-loader branch: hand over the plain URL and let the failure
            // path answer for it if the fetch dies.
            Attach(MediaSource.CreateFromUri(Stream.Url));
        }
    }

    public MediaPlayerState State => _player.CurrentState;

    /// <summary>Whether sound is actually coming out.</summary>
    public bool IsSounding => _opened && _player.CurrentState == MediaPlayerState.Playing;

    /// <summary>Where the song is. Until the media opens it is the place the song will start
    /// from, so a failure in the first moments keeps the listener's place.</summary>
    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                if (!_opened || _disposed)
                {
                    return _pendingPosition;
                }
            }

            try
            {
                return _player.PlaybackSession.Position.TotalSeconds;
            }
            catch (Exception)
            {
                return _pendingPosition;
            }
        }
    }

    /// <summary>The voice's own level, 0 to 1: Koda's volume, the loudness levelling and the fade.</summary>
    public double Gain
    {
        set
        {
            lock (_gate)
            {
                _gain = Math.Clamp(value, 0, 1);
                if (_opened && !_disposed)
                {
                    _player.Volume = _gain;
                }
            }
        }
    }

    public bool Muted
    {
        set
        {
            if (!_disposed)
            {
                _player.IsMuted = value;
            }
        }
    }

    public void SetAudioDevice(DeviceInformation? device)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _player.AudioDevice = device;
        }
        catch (Exception)
        {
            // Keep playing through the old device rather than go silent.
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            _wantsPlay = true;
            // Play goes in as soon as there is a source: Media Foundation may not open a
            // stream source until it is asked to play. The voice stays silent until the media
            // opens and its resume position is applied, so the song's first moment is never heard.
            if (_source is not null && !_disposed)
            {
                _player.Play();
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _wantsPlay = false;
            if (!_disposed)
            {
                _player.Pause();
            }
        }
    }

    public void Seek(double seconds)
    {
        var position = Math.Max(0, seconds);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_opened)
            {
                // The media is not open yet: the position waits for it, so a seek during the
                // loading window is not lost.
                _pendingPosition = position;
                return;
            }
        }

        try
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(position);
        }
        catch (Exception)
        {
            // The session refused it; the playhead stays where it is.
        }
    }

    private async Task OpenFramesAsync(Koda.Core.Playback.CachingLoader loader, long length)
    {
        try
        {
            async Task<byte[]> Read(long offset, int count, CancellationToken cancellation)
            {
                var buffer = new byte[Math.Max(0, Math.Min(count, length - offset))];
                var copied = await loader.ReadAsync(offset, buffer, cancellation).ConfigureAwait(false);
                return copied == buffer.Length ? buffer : buffer[..(int)copied];
            }

            var streamSource = await Fmp4StreamSource.CreateAsync(Read, length, CancellationToken.None).ConfigureAwait(false);
            Attach(MediaSource.CreateFromMediaStreamSource(streamSource));
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                Failed?.Invoke(this, "This song's audio could not be read.");
            }
        }
    }

    private async Task OpenFileFramesAsync(string path)
    {
        try
        {
            var file = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            var gate = new SemaphoreSlim(1, 1);
            async Task<byte[]> Read(long offset, int count, CancellationToken cancellation)
            {
                await gate.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    var buffer = new byte[Math.Max(0, Math.Min(count, file.Length - offset))];
                    file.Position = offset;
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var n = await file.ReadAsync(buffer.AsMemory(read), cancellation).ConfigureAwait(false);
                        if (n == 0) break;
                        read += n;
                    }

                    return read == buffer.Length ? buffer : buffer[..read];
                }
                finally
                {
                    gate.Release();
                }
            }

            try
            {
                var streamSource = await Fmp4StreamSource.CreateAsync(Read, file.Length, CancellationToken.None).ConfigureAwait(false);
                streamSource.Closed += (_, _) => file.Dispose();
                Attach(MediaSource.CreateFromMediaStreamSource(streamSource));
                return;
            }
            catch
            {
                // Not an AAC fragmented MP4 (e.g. a lossless FLAC file): hand the file
                // to Media Foundation directly and let it decode the container.
                file.Dispose();
            }

            Attach(MediaSource.CreateFromUri(new Uri(path)));
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                Failed?.Invoke(this, "This song's saved audio could not be read.");
            }
        }
    }

    /// <summary>Give the player its source, unless the voice was let go meanwhile.</summary>
    private void Attach(MediaSource source)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                source.Dispose();
                return;
            }

            _source = source;
            _player.Source = source;
            if (_wantsPlay)
            {
                _player.Play();
            }
        }
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // The resume position goes in before the first sound, exactly once.
            if (_pendingPosition > 0)
            {
                try
                {
                    sender.PlaybackSession.Position = TimeSpan.FromSeconds(_pendingPosition);
                }
                catch (Exception)
                {
                    // Refused: the song starts at its top rather than not at all.
                }
            }

            _opened = true;
            sender.Volume = _gain;
            if (_wantsPlay)
            {
                sender.Play();
            }
        }
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        if (!_disposed)
        {
            Ended?.Invoke(this);
        }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        // MF_E_NO_AUDIO_PLAYBACK_DEVICE: nothing to play through, which no retry can fix.
        const int NoAudioDevice = unchecked((int)0xC00D36FA);
        var reason = args.ExtendedErrorCode?.HResult == NoAudioDevice
            ? "no speakers or headphones are connected"
            : string.IsNullOrWhiteSpace(args.ErrorMessage)
                ? $"media error 0x{args.ExtendedErrorCode?.HResult ?? 0:x8}"
                : args.ErrorMessage;
        Failed?.Invoke(this, reason);
    }

    private void OnCurrentStateChanged(MediaPlayer sender, object args)
    {
        if (!_disposed)
        {
            StateReached?.Invoke(this, sender.CurrentState);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _wantsPlay = false;
        }

        _player.MediaOpened -= OnMediaOpened;
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        _player.CurrentStateChanged -= OnCurrentStateChanged;
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch (Exception)
        {
            // A player already torn down by the system has nothing left to stop.
        }

        _player.Dispose();
        _source?.Dispose();
        _source = null;
        _loader?.Dispose();
        _loader = null;
    }
}
