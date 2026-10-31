using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Koda.Core;
using Koda.Core.Video;
using Koda.Helpers;
using Koda.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;

namespace Koda.Playback;

/// <summary>
/// The one video player: what's on, its DASH stream on Windows' MediaPlayer, the state its
/// controls draw (time, playing, buffering), its settings (quality, speed, captions), full
/// screen and theater, and keeping it out of the music's way: starting a video pauses the
/// songs, and a song starting pauses the video. It outlives the watch page: leaving the page
/// keeps the video going in the bar, remembers where each video got to, and at the end moves
/// on to Up Next.
///
/// Mac hands YouTube's HLS master to AVPlayer. Windows refuses it, so the stream is the
/// on-demand DASH manifest <see cref="DashManifest"/> builds from the same /player answer;
/// AdaptiveMediaSource switches and seeks natively. A chosen quality is a new source, not a
/// raised bitrate, because Windows ignores a raised DesiredMinBitrate mid-play.
/// </summary>
public sealed class VideoPlaybackService
{
    public static VideoPlaybackService Instance { get; } = new();

    private readonly MediaPlayer _player = new();
    private readonly HttpClient _http = new();
    private DispatcherQueueTimer? _tick;
    private DispatcherQueueTimer? _autoplayTimer;
    private DispatcherQueue? _dispatch;
    private MediaPlayerElement? _element;
    private InMemoryRandomAccessStream? _manifestStream;
    private AdaptiveMediaSource? _adaptive;
    private VideoStream? _stream;
    private int _generation;
    private double _pendingPosition;
    private double _duration;
    private double _lastSavedPosition = -1;
    private bool _shouldPlay;
    private bool _recovering;
    private int _recoverAttempts;
    private DateTimeOffset _bufferingSince = DateTimeOffset.MaxValue;
    private DateTimeOffset _lastDownloadFailure = DateTimeOffset.MinValue;
    private bool _musicPausedForVideo;
    private List<(double Start, double End, string Text)> _cues = [];
    private VideoCaptionTrack[] _captionTracks = [];
    private VideoCaptionTrack? _captionTrack;
    private string _caption = "";
    private bool _loadingCaptions;
    private int _autoplaySeconds;

    private VideoPlaybackService()
    {
        _player.CommandManager.IsEnabled = false;
        _player.AudioCategory = MediaPlayerAudioCategory.Movie;
        _player.Volume = Math.Clamp(App.Volume / 100, 0, 1);
        _player.MediaOpened += (_, _) => OnDispatch(OnOpened);
        _player.MediaEnded += (_, _) => OnDispatch(OnEnded);
        _player.MediaFailed += (_, args) => OnDispatch(() => OnFailed(args));
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => OnDispatch(OnStateChanged);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ClientProfiles.Default.BrowserUserAgent);
    }

    // MARK: State

    /// <summary>The video loaded, whether or not it is playing.</summary>
    public Video? Current { get; private set; }

    /// <summary>Everything under the player: details, channel, Up Next. Fetched with the video.</summary>
    public WatchPage? Watch { get; private set; }

    public bool HasVideo => Current is not null;

    public bool IsLoading { get; private set; }

    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public bool IsBuffering => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Buffering;

    public string? Error { get; private set; }

    public double Position => _player.PlaybackSession.Position.TotalSeconds;

    public double Duration => _duration > 0 ? _duration : _player.PlaybackSession.NaturalDuration.TotalSeconds;

    public IReadOnlyList<VideoQuality> Qualities => _stream?.Qualities ?? [];

    /// <summary>0 is Auto; otherwise the height the source was held to.</summary>
    public int Quality { get; private set; }

    public double Speed { get; private set; } = 1;

    public VideoCaptionTrack[] CaptionTracks => _captionTracks;

    public VideoCaptionTrack? CaptionTrack => _captionTrack;

    public bool CaptionsOn { get; private set; }

    public string CurrentCaption => CaptionsOn ? _caption : "";

    /// <summary>The seconds left before Up Next starts by itself; 0 when no countdown is running.</summary>
    public int AutoplaySeconds => _autoplaySeconds;

    public Video? Next => Watch?.Next;

    /// <summary>The player itself, for a page's MediaPlayerElement and the mini window.</summary>
    public MediaPlayer Player => _player;

    /// <summary>Raised for anything the controls draw: the video, loading, playing, quality,
    /// speed, captions, Up Next and the autoplay countdown.</summary>
    public event Action? Changed;

    /// <summary>Raised about twice a second while a video is loaded, for the scrubber.</summary>
    public event Action? Tick;

    /// <summary>A video finished; the watch page shows its end screen.</summary>
    public event Action? Ended;

    public void Connect(DispatcherQueue dispatch)
    {
        _dispatch = dispatch;
        PlaybackService.Instance.PlayStateChanged += () =>
        {
            // A song starting takes the sound back; the video waits where it is.
            if (PlaybackService.Instance.IsPlaying && IsPlaying)
            {
                _player.Pause();
                _shouldPlay = false;
                Changed?.Invoke();
            }
        };
    }

    // MARK: Opening

    public async Task PlayAsync(Video video, double? startAt = null)
    {
        if (Current?.Id == video.Id && _player.Source is not null && Error is null)
        {
            if (startAt is { } position) Seek(position);
            _shouldPlay = true;
            _player.Play();
            Changed?.Invoke();
            return;
        }

        Current = video;
        Watch = null;
        Error = null;
        _caption = "";
        _captionTrack = null;
        _captionTracks = [];
        _cues = [];
        CaptionsOn = false;
        _recoverAttempts = 0;
        _lastSavedPosition = -1;
        Quality = App.Settings.VideoQuality;
        CancelAutoplay();
        _musicPausedForVideo |= PlaybackService.Instance.IsPlaying;
        PlaybackService.Instance.Pause();
        Changed?.Invoke();
        await OpenAsync(video, startAt ?? App.Library.ProgressOf(video.Id)?.ResumeAt ?? 0, Quality, record: true);
    }

    private async Task OpenAsync(Video video, double startAt, int quality, bool record)
    {
        var generation = ++_generation;
        IsLoading = true;
        Error = null;
        _pendingPosition = startAt;
        _shouldPlay = true;
        Changed?.Invoke();
        try
        {
            var stream = await EngineHost.Music.VideoStreamAsync(video.Id);
            if (generation != _generation) return;
            _stream = stream;
            if (quality != 0 && !stream.Qualities.Any(q => q.Height == quality))
            {
                // A video offering less plays its best below the chosen height.
                quality = stream.Qualities.FirstOrDefault(q => q.Height <= quality)?.Height ?? quality;
            }

            var manifest = quality == 0 ? stream.Manifest : stream.ManifestFor(quality);
            var source = await CreateSourceAsync(manifest, stream, quality);
            if (generation != _generation)
            {
                return;
            }

            _adaptive = source;
            _player.Source = MediaSource.CreateFromAdaptiveMediaSource(source);
            if (record) App.Library.RecordWatch(video);

            // Up Next, details and comments: the watch page's own answer, fetched once here so
            // the page, the bar and autoplay all share it.
            _ = LoadWatchAsync(video, generation);
        }
        catch (Exception error)
        {
            if (generation != _generation) return;
            IsLoading = false;
            _shouldPlay = false;
            Error = $"{Errors.Title(error)}. {Errors.Describe(error)}";
            Changed?.Invoke();
        }
    }

    private async Task LoadWatchAsync(Video video, int generation)
    {
        try
        {
            var watch = await EngineHost.Music.WatchPageAsync(video.Id);
            if (generation != _generation || Current?.Id != video.Id) return;
            Watch = watch;
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // The video still plays; the page shows what it knows.
        }
    }

    private async Task<AdaptiveMediaSource> CreateSourceAsync(string manifest, VideoStream stream, int quality)
    {
        var memory = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(memory.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(Encoding.UTF8.GetBytes(manifest));
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        memory.Seek(0);
        _manifestStream = memory;
        var creation = await AdaptiveMediaSource.CreateFromStreamAsync(
            memory, new Uri("https://koda.invalid/video.mpd"), "application/dash+xml");
        if (creation.Status != AdaptiveMediaSourceCreationStatus.Success || creation.MediaSource is null)
        {
            throw new InvalidOperationException(creation.Status.ToString());
        }

        var source = creation.MediaSource;
        // Auto should start at a watchable picture rather than the bottom rung, then follow
        // the connection from there.
        if (quality == 0)
        {
            var initial = stream.Qualities.FirstOrDefault(q => q.Height <= 1080) ?? stream.Qualities.FirstOrDefault();
            if (initial is not null) source.InitialBitrate = (uint)Math.Max(1, initial.Bandwidth);
        }

        source.DownloadFailed += (_, _) => _lastDownloadFailure = DateTimeOffset.UtcNow;
        return source;
    }

    // MARK: Player events

    private void OnOpened()
    {
        if (_pendingPosition > 0.5)
        {
            _player.PlaybackSession.Position = TimeSpan.FromSeconds(_pendingPosition);
        }

        _pendingPosition = 0;
        _duration = _player.PlaybackSession.NaturalDuration.TotalSeconds;
        IsLoading = false;
        try
        {
            _player.PlaybackSession.PlaybackRate = Speed;
        }
        catch (Exception)
        {
            // A source that refuses a rate still plays at 1×.
        }

        if (_shouldPlay) _player.Play();
        Changed?.Invoke();
    }

    private void OnStateChanged()
    {
        if (IsBuffering)
        {
            if (_bufferingSince == DateTimeOffset.MaxValue) _bufferingSince = DateTimeOffset.UtcNow;
        }
        else
        {
            _bufferingSince = DateTimeOffset.MaxValue;
        }

        Changed?.Invoke();
    }

    private void OnEnded()
    {
        _shouldPlay = false;
        SaveProgress(force: true);
        Ended?.Invoke();
        Changed?.Invoke();
        if (App.Settings.AutoplaysVideos && Watch?.Next is not null) BeginAutoplay();
    }

    private void OnFailed(MediaPlayerFailedEventArgs args)
    {
        if (_recovering || Current is null)
        {
            IsLoading = false;
            Error = "This video stopped playing. Try again.";
            Changed?.Invoke();
            return;
        }

        _ = RecoverAsync(args.ErrorMessage);
    }

    private async Task RecoverAsync(string reason)
    {
        if (_recovering || Current is not { } video) return;
        _recovering = true;
        _recoverAttempts++;
        var position = Position;
        try
        {
            // googlevideo URLs expire; a refused stream is resolved again, then the same place
            // is put back.
            await EngineHost.Music.VideoStreamFailedAsync(video.Id);
            _stream = null;
            await OpenAsync(video, position, Quality, record: false);
        }
        catch (Exception)
        {
            Error = "This video stopped playing. Try again.";
            IsLoading = false;
            Changed?.Invoke();
        }
        finally
        {
            _recovering = false;
        }
    }

    // MARK: Transport

    public void TogglePlay()
    {
        if (!HasVideo || _player.Source is null) return;
        if (IsPlaying)
        {
            _player.Pause();
            _shouldPlay = false;
            SaveProgress(force: true);
        }
        else
        {
            _musicPausedForVideo |= PlaybackService.Instance.IsPlaying;
            PlaybackService.Instance.Pause();
            _shouldPlay = true;
            _player.Play();
        }

        Changed?.Invoke();
    }

    public void Seek(double seconds)
    {
        if (!HasVideo) return;
        var duration = Duration;
        var position = Math.Clamp(seconds, 0, duration > 0 ? duration : seconds);
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(position);
        _lastSavedPosition = -1;
        Tick?.Invoke();
    }

    public void Skip(double seconds) => Seek(Position + seconds);

    public async Task NextAsync()
    {
        if (Watch?.Next is { } next)
        {
            CancelAutoplay();
            await PlayAsync(next);
        }
    }

    public void SetSpeed(double speed)
    {
        Speed = speed is 0.5 or 0.75 or 1 or 1.25 or 1.5 or 1.75 or 2 ? speed : 1;
        try
        {
            _player.PlaybackSession.PlaybackRate = Speed;
        }
        catch (Exception)
        {
            // Ignored: the next source picks the rate up.
        }

        Changed?.Invoke();
    }

    public void SetVolume(double percent) => _player.Volume = Math.Clamp(percent / 100, 0, 1);

    public bool IsMuted
    {
        get => _player.IsMuted;
        set
        {
            _player.IsMuted = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Tries the video that failed again, from where it stopped.</summary>
    public async Task RetryAsync()
    {
        if (Current is not { } video) return;
        await EngineHost.Music.VideoStreamFailedAsync(video.Id);
        _stream = null;
        _recoverAttempts = 0;
        await OpenAsync(video, Position, Quality, record: false);
    }

    public async Task SetQualityAsync(int height)
    {
        if (Current is not { } video || _stream is null) return;
        if (height != 0 && !_stream.Qualities.Any(q => q.Height == height)) return;
        var resume = Position;
        var wasPlaying = _shouldPlay || IsPlaying;
        Quality = height;
        var generation = ++_generation;
        IsLoading = true;
        Changed?.Invoke();
        try
        {
            var manifest = height == 0 ? _stream.Manifest : _stream.ManifestFor(height);
            var source = await CreateSourceAsync(manifest, _stream, height);
            if (generation != _generation)
            {
                return;
            }

            _adaptive = source;
            _pendingPosition = resume;
            _shouldPlay = wasPlaying;
            _player.Source = MediaSource.CreateFromAdaptiveMediaSource(source);
        }
        catch (Exception)
        {
            IsLoading = false;
            Error = "That quality didn't start. Try another.";
            Changed?.Invoke();
        }
    }

    // MARK: Captions

    public async Task ToggleCaptionsAsync()
    {
        if (CaptionsOn)
        {
            CaptionsOn = false;
            _caption = "";
            Changed?.Invoke();
            return;
        }

        if (_captionTracks.Length == 0) await LoadCaptionTracksAsync();
        if (_captionTracks.Length == 0) return;
        await SelectCaptionAsync(_captionTrack ?? _captionTracks[0]);
    }

    public async Task LoadCaptionTracksAsync()
    {
        if (Current is not { } video || _loadingCaptions) return;
        _loadingCaptions = true;
        try
        {
            _captionTracks = await EngineHost.Music.VideoCaptionsAsync(video.Id);
            _captionTrack ??= _captionTracks.FirstOrDefault(t => !t.IsAutomatic) ?? _captionTracks.FirstOrDefault();
        }
        catch (MusicException)
        {
            _captionTracks = [];
        }
        finally
        {
            _loadingCaptions = false;
        }

        Changed?.Invoke();
    }

    public async Task SelectCaptionAsync(VideoCaptionTrack track)
    {
        _captionTrack = track;
        try
        {
            var vtt = await _http.GetStringAsync(track.Url);
            _cues = ParseVtt(vtt);
            CaptionsOn = _cues.Count > 0;
            _caption = "";
        }
        catch (Exception)
        {
            _cues = [];
            CaptionsOn = false;
        }

        Changed?.Invoke();
    }

    private static List<(double Start, double End, string Text)> ParseVtt(string text)
    {
        var cues = new List<(double, double, string)>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            var startText = line[..arrow].Trim().Split(' ')[0];
            var endText = line[(arrow + 3)..].Trim().Split(' ')[0];
            if (!TryTime(startText, out var start) || !TryTime(endText, out var end)) continue;
            var body = new StringBuilder();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
            {
                if (body.Length > 0) body.Append('\n');
                body.Append(Regex.Replace(lines[i], "<[^>]+>", ""));
            }

            var cue = System.Net.WebUtility.HtmlDecode(body.ToString()).Trim();
            if (cue.Length > 0) cues.Add((start, end, cue));
        }

        return cues;
    }

    private static bool TryTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return false;
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        seconds = parts.Length == 3
            ? values[0] * 3600 + values[1] * 60 + values[2]
            : values[0] * 60 + values[1];
        return true;
    }

    // MARK: Autoplay

    private void BeginAutoplay()
    {
        _autoplaySeconds = 5;
        Changed?.Invoke();
        _autoplayTimer?.Start();
    }

    public void CancelAutoplay()
    {
        _autoplayTimer?.Stop();
        if (_autoplaySeconds == 0) return;
        _autoplaySeconds = 0;
        Changed?.Invoke();
    }

    public async Task PlayNextNowAsync()
    {
        CancelAutoplay();
        await NextAsync();
    }

    // MARK: Attach

    /// <summary>Puts the player's picture in a page's element, taking it from wherever it was.</summary>
    public void Attach(MediaPlayerElement element)
    {
        if (_element == element)
        {
            element.SetMediaPlayer(_player);
            return;
        }

        _element?.SetMediaPlayer(null);
        _element = element;
        element.SetMediaPlayer(_player);
    }

    public void Detach(MediaPlayerElement element)
    {
        if (_element == element)
        {
            element.SetMediaPlayer(null);
            _element = null;
        }
    }

    /// <summary>Whether this element is the one drawing the picture.</summary>
    public bool IsAttached(MediaPlayerElement element) => _element == element;

    // MARK: Ticks and history

    private void OnDispatch(Action action)
    {
        if (_dispatch is null) action();
        else _dispatch.TryEnqueue(() => action());
    }

    /// <summary>Called by the shell once the dispatcher is known; starts the clock and countdown.</summary>
    public void StartTimers()
    {
        if (_dispatch is null || _tick is not null) return;
        var tick = _dispatch.CreateTimer();
        tick.Interval = TimeSpan.FromMilliseconds(500);
        tick.IsRepeating = true;
        tick.Tick += (_, _) => OnTick();
        tick.Start();
        _tick = tick;

        var autoplay = _dispatch.CreateTimer();
        autoplay.Interval = TimeSpan.FromSeconds(1);
        autoplay.IsRepeating = true;
        autoplay.Tick += (_, _) =>
        {
            if (_autoplaySeconds <= 0)
            {
                autoplay.Stop();
                return;
            }

            _autoplaySeconds--;
            if (_autoplaySeconds == 0)
            {
                autoplay.Stop();
                _ = PlayNextNowAsync();
            }
            else
            {
                Changed?.Invoke();
            }
        };
        _autoplayTimer = autoplay;
    }

    private void OnTick()
    {
        if (!HasVideo) return;
        Tick?.Invoke();

        if (CaptionsOn && _cues.Count > 0)
        {
            var position = Position;
            var cue = _cues.LastOrDefault(c => c.Start <= position);
            var text = cue.Text is not null && position <= cue.End ? cue.Text : "";
            if (text != _caption)
            {
                _caption = text;
                Changed?.Invoke();
            }
        }

        SaveProgress();
        if (IsBuffering && _bufferingSince != DateTimeOffset.MaxValue
            && (DateTimeOffset.UtcNow - _bufferingSince).TotalSeconds > 12
            && !_recovering && _recoverAttempts < 2 && _player.Source is not null)
        {
            _bufferingSince = DateTimeOffset.MaxValue;
            _ = RecoverAsync("stalled");
        }
    }

    private void SaveProgress(bool force = false)
    {
        if (Current is not { } video) return;
        var position = Position;
        var duration = Duration;
        if (duration <= 0 || position <= 0) return;
        if (!force && Math.Abs(position - _lastSavedPosition) < 5) return;
        _lastSavedPosition = position;
        App.Library.SetVideoProgress(position, duration, video.Id);
    }

    /// <summary>Leaving the app: the place is kept now, not on the next tick.</summary>
    public void Flush() => SaveProgress(force: true);

    /// <summary>Stops and clears the video, for closing it from the bar.</summary>
    public void Stop()
    {
        _generation++;
        SaveProgress(force: true);
        _player.Pause();
        _player.Source = null;
        Current = null;
        Watch = null;
        Error = null;
        IsLoading = false;
        _duration = 0;
        _stream = null;
        _cues = [];
        _captionTracks = [];
        CaptionsOn = false;
        CancelAutoplay();
        if (_musicPausedForVideo)
        {
            _musicPausedForVideo = false;
            PlaybackService.Instance.Play();
        }

        Changed?.Invoke();
    }
}
