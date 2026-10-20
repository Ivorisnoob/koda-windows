using System;
using Koda.Core;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Koda.Playback;

/// <summary>
/// The audio voice over Media Foundation: the OS Windows.Media.Playback.MediaPlayer
/// projection with CommandManager DISABLED. Streams ride our own transport
/// (Koda.Core's CachingLoader over our ranged HttpClient), because Media
/// Foundation's built-in fetch of googlevideo URLs cannot survive inside the
/// WinUI host; it never floats its own SMTC card, so the system flyout keeps
/// exactly one Koda entry - the manual window-bound SmtcController.
/// </summary>
public sealed class Player : IDisposable
{
    private MediaPlayer? _player;
    private MediaSource? _source;
    private Koda.Core.Playback.CachingLoader? _loader;
    private readonly object _attach = new();
    /// <summary>Bumped per Load and Stop, so a source that opens late for an old track is dropped.</summary>
    private int _generation;
    private bool _wantsPlay;

    /// <summary>The track reached its end (raised off the UI thread).</summary>
    public event Action? Ended;

    /// <summary>Media Foundation gave up (raised off the UI thread; human reason).</summary>
    public event Action<string>? Failed;

    /// <summary>Every playback-state change (raised off the UI thread).</summary>
    public event Action<MediaPlayerState>? StateReached;

    public Player()
    {
        _player = new MediaPlayer { AutoPlay = false };
        // CommandManager off: the system flyout must stay ours.
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        _player.CurrentStateChanged += OnCurrentStateChanged;
    }

    public MediaPlayerState State => _player is { } player ? player.CurrentState : MediaPlayerState.Closed;

    public double PositionSeconds =>
        _player is { } player && player.CurrentState is not MediaPlayerState.Closed
            ? player.PlaybackSession.Position.TotalSeconds
            : 0;

    public double Volume
    {
        get => _player is { } player ? player.Volume * 100 : 100;
        set
        {
            if (_player is { } player)
            {
                player.Volume = Math.Clamp(value, 0, 100) / 100.0;
            }
        }
    }

    public bool Muted
    {
        get => _player is { } player && player.IsMuted;
        set
        {
            if (_player is { } player)
            {
                player.IsMuted = value;
            }
        }
    }

    /// <summary>Point the player at a fresh track. A fresh player per track:
    /// swapping Source on a live MediaPlayer surfaces the old source's MediaEnded, which
    /// the queue would read as a real track end.
    ///
    /// The bytes ride our own transport (Koda.Core's CachingLoader): every byte reaches the
    /// player through our own ranged HttpClient with the per-client user agent, while the
    /// store keeps them on disk for the next play. A stream without a content length - never
    /// a resolved AAC - falls back to the direct URL.</summary>
    public void Load(ResolvedStream resolved, Song? song)
    {
        if (_player is null)
        {
            return;
        }


        var wasMuted = Muted;
        var lastVolume = Volume;

        var old = _player;
        old.MediaEnded -= OnMediaEnded;
        old.MediaFailed -= OnMediaFailed;
        old.CurrentStateChanged -= OnCurrentStateChanged;
        old.Source = null;
        old.Dispose();
        _source?.Dispose();
        _source = null;
        _loader?.Dispose();
        _loader = null;

        _player = new MediaPlayer { AutoPlay = false };
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        _player.CurrentStateChanged += OnCurrentStateChanged;
        _player.Volume = Math.Clamp(lastVolume, 0, 100) / 100.0;
        _player.IsMuted = wasMuted;

        var generation = ++_generation;
        _wantsPlay = false;
        if (resolved.Url.IsFile)
        {
            // A song held whole on disk: the same frames, read from the file.
            _ = OpenFileFramesAsync(resolved.Url.LocalPath, generation);
        }
        else if (Koda.Core.Playback.CachingLoader.Create(resolved, song, App.AudioStore) is { } loader)
        {
            _loader = loader;
            _ = OpenFramesAsync(loader, resolved.ContentLength!.Value, generation);
        }
        else
        {
            // The Swift nil-loader branch: hand over the plain URL and let the
            // failure/reload path above answer for it if the fetch dies.
            Attach(MediaSource.CreateFromUri(resolved.Url), generation);
        }
    }

    /// <summary>
    /// YouTube's audio is fragmented MP4 (DASH), which Media Foundation's MP4 source
    /// opens and then ends at once; the frames are fed to it through a MediaStreamSource
    /// instead (see Fmp4Audio). The container's head is read first, so the source is
    /// attached a moment after Load returns; a Play asked for meanwhile is kept.
    /// [verified October 2026: file URI and stream both ended at 0 s; frames play and seek]
    /// </summary>
    private async Task OpenFramesAsync(Koda.Core.Playback.CachingLoader loader, long length, int generation)
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
            Attach(MediaSource.CreateFromMediaStreamSource(streamSource), generation);
        }
        catch (Exception)
        {
            if (generation != _generation)
            {
                return;
            }

            Failed?.Invoke("This song's audio could not be read.");
        }
    }

    private async Task OpenFileFramesAsync(string path, int generation)
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

            var streamSource = await Fmp4StreamSource.CreateAsync(Read, file.Length, CancellationToken.None).ConfigureAwait(false);
            streamSource.Closed += (_, _) => file.Dispose();
            Attach(MediaSource.CreateFromMediaStreamSource(streamSource), generation);
        }
        catch (Exception)
        {
            if (generation != _generation)
            {
                return;
            }

            Failed?.Invoke("This song's saved audio could not be read.");
        }
    }

    /// <summary>Give the player its source, unless another track was loaded meanwhile.</summary>
    private void Attach(MediaSource source, int generation)
    {
        lock (_attach)
        {
            if (generation != _generation || _player is not { } player)
            {
                source.Dispose();
                return;
            }

            player.Source = source;
            _source = source;
            if (_wantsPlay)
            {
                player.Play();
            }
        }
    }

    public void Play()
    {
        lock (_attach)
        {
            _wantsPlay = true;
            if (_source is not null)
            {
                _player?.Play();
            }
        }
    }

    public void Pause()
    {
        lock (_attach)
        {
            _wantsPlay = false;
            _player?.Pause();
        }
    }

    public void Seek(double seconds)
    {
        if (_player is not { } player)
        {
            return;
        }

        player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    public void Stop()
    {
        _generation++;
        _wantsPlay = false;
        if (_player is { } player)
        {
            player.Source = null;
        }

        _source?.Dispose();
        _source = null;
        _loader?.Dispose();
        _loader = null;
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        Ended?.Invoke();
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var reason = string.IsNullOrWhiteSpace(args.ErrorMessage)
            ? $"media error 0x{args.ExtendedErrorCode.HResult:x8}"
            : args.ErrorMessage;
        Failed?.Invoke(reason);
    }

    private void OnCurrentStateChanged(MediaPlayer sender, object args)
    {
        StateReached?.Invoke(sender.CurrentState);
    }

    public void Dispose()
    {
        if (_player is null)
        {
            return;
        }

        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        _player.CurrentStateChanged -= OnCurrentStateChanged;
        _player.Source = null;
        _player.Dispose();
        _player = null;
        _source?.Dispose();
        _source = null;
        _loader?.Dispose();
        _loader = null;
    }
}
