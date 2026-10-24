using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Koda.Core.Playback;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Koda.Playback;

/// <summary>
/// Feeds Media Foundation a DASH song one AAC frame at a time (as ADTS), read
/// through <see cref="Fmp4Audio"/>. MF's own MP4 source opens YouTube's fragmented
/// files and ends at once, so it is never handed the container. Seeking lands on
/// the frame at or before the asked time.
///
/// Two rules keep the playhead where the listener left it:
/// <list type="bullet">
/// <item>A start with no position is Media Foundation resuming (after a pause, or after
/// the audio graph was rebuilt): the source carries on from where it was. Reading it
/// as "from the top" sent a song back to its first second.</item>
/// <item>Every start bumps an epoch. A sample read that was in flight across a seek
/// finds the epoch moved and starts over from the new place, rather than writing the
/// old place back over it.</item>
/// </list>
/// </summary>
internal sealed class Fmp4StreamSource
{
    private readonly Fmp4Audio _audio;
    private readonly object _lock = new();
    private Fmp4Audio.Frame[] _frames = [];
    private int _fragment = -1;
    private int _next;
    /// <summary>Where the next fragment starts when the index named no time for it.</summary>
    private long _nextStart;
    /// <summary>Bumped by every start; reads begun under an older one are dropped.</summary>
    private int _epoch;
    private bool _started;

    private Fmp4StreamSource(Fmp4Audio audio) => _audio = audio;

    public static async Task<MediaStreamSource> CreateAsync(Fmp4Audio.ReadAt read, long length, CancellationToken cancellation)
    {
        var audio = await Fmp4Audio.OpenAsync(read, length, cancellation).ConfigureAwait(false);
        var feeder = new Fmp4StreamSource(audio);
        var properties = AudioEncodingProperties.CreateAacAdts((uint)audio.SampleRate, (uint)audio.Channels, 128_000);
        var source = new MediaStreamSource(new AudioStreamDescriptor(properties))
        {
            CanSeek = true,
            Duration = audio.Duration,
            BufferTime = TimeSpan.FromSeconds(1),
        };
        source.Starting += feeder.OnStarting;
        source.SampleRequested += feeder.OnSampleRequested;
        return source;
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        var request = args.Request;
        lock (_lock)
        {
            // No position: resume from the current one. Only the very first start, before
            // anything was read, begins at the top.
            if (request.StartPosition is null && _started)
            {
                return;
            }
        }

        var deferral = request.GetDeferral();
        _ = StartAsync(request, deferral);
    }

    private async Task StartAsync(MediaStreamSourceStartingRequest request, MediaStreamSourceStartingRequestDeferral deferral)
    {
        int epoch;
        lock (_lock)
        {
            epoch = ++_epoch;
            _started = true;
        }

        try
        {
            var target = request.StartPosition ?? TimeSpan.Zero;
            var ticks = (long)(target.TotalSeconds * _audio.Timescale);
            var fragment = _audio.FragmentAt(ticks);
            var start = _audio.Fragments[fragment].StartTicks;
            var frames = await _audio.FramesAsync(fragment, start >= 0 ? start : 0).ConfigureAwait(false);
            var index = 0;
            while (index + 1 < frames.Length && frames[index + 1].StartTicks <= ticks)
            {
                index++;
            }

            lock (_lock)
            {
                if (epoch != _epoch)
                {
                    return; // a later start already moved the source
                }

                _fragment = fragment;
                _frames = frames;
                _next = index;
                _nextStart = frames.Length > 0 ? frames[^1].StartTicks + frames[^1].DurationTicks : 0;
            }

            if (request.StartPosition is not null && frames.Length > 0)
            {
                request.SetActualStartPosition(TimeSpan.FromSeconds((double)frames[index].StartTicks / _audio.Timescale));
            }
        }
        catch (Exception)
        {
            // A seek that cannot be resolved ends here; the next sample read fails and the
            // service's failure path reloads the track where it was.
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        var deferral = request.GetDeferral();
        _ = SampleAsync(request, deferral);
    }

    private async Task SampleAsync(MediaStreamSourceSampleRequest request, MediaStreamSourceSampleRequestDeferral deferral)
    {
        try
        {
            while (true)
            {
                Fmp4Audio.Frame frame = default;
                bool haveFrame;
                int fragment;
                int epoch;
                long nextStart;
                lock (_lock)
                {
                    epoch = _epoch;
                    haveFrame = _next < _frames.Length;
                    if (haveFrame)
                    {
                        frame = _frames[_next++];
                    }

                    fragment = _fragment + 1;
                    nextStart = _nextStart;
                }

                if (!haveFrame)
                {
                    if (fragment >= _audio.Fragments.Count)
                    {
                        request.Sample = null; // the end of the song
                        return;
                    }

                    var frames = await _audio.FramesAsync(fragment, nextStart).ConfigureAwait(false);
                    lock (_lock)
                    {
                        // A seek while the fragment was read: its frames belong to the old place.
                        if (epoch == _epoch && _fragment + 1 == fragment)
                        {
                            _fragment = fragment;
                            _frames = frames;
                            _next = 0;
                            if (frames.Length > 0) _nextStart = frames[^1].StartTicks + frames[^1].DurationTicks;
                        }
                    }

                    continue;
                }

                var bytes = await _audio.AdtsFrameAsync(frame).ConfigureAwait(false);
                lock (_lock)
                {
                    if (epoch != _epoch)
                    {
                        continue; // read across a seek: take the frame at the new place instead
                    }
                }

                var sample = MediaStreamSample.CreateFromBuffer(
                    bytes.AsBuffer(), TimeSpan.FromSeconds((double)frame.StartTicks / _audio.Timescale));
                sample.Duration = TimeSpan.FromSeconds((double)frame.DurationTicks / _audio.Timescale);
                request.Sample = sample;
                return;
            }
        }
        catch (Exception)
        {
            // A fetch that died ends the samples here; the service sees an end that came
            // too early and reloads the song from where it stopped.
            request.Sample = null;
        }
        finally
        {
            deferral.Complete();
        }
    }
}
