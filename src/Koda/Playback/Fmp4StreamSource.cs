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
        var deferral = request.GetDeferral();
        _ = StartAsync(request, deferral);
    }

    private async Task StartAsync(MediaStreamSourceStartingRequest request, MediaStreamSourceStartingRequestDeferral deferral)
    {
        try
        {
            var target = request.StartPosition ?? TimeSpan.Zero;
            var ticks = (long)(target.TotalSeconds * _audio.Timescale);
            var fragment = _audio.FragmentAt(ticks);
            var frames = await _audio.FramesAsync(fragment, _audio.Fragments[fragment].StartTicks >= 0 ? _audio.Fragments[fragment].StartTicks : 0)
                .ConfigureAwait(false);
            var index = 0;
            while (index + 1 < frames.Length && frames[index + 1].StartTicks <= ticks)
            {
                index++;
            }

            lock (_lock)
            {
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
            // A seek that cannot be resolved ends here; the service's failure path reloads the track.
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
            Fmp4Audio.Frame frame;
            while (true)
            {
                int fragment;
                lock (_lock)
                {
                    if (_next < _frames.Length)
                    {
                        frame = _frames[_next++];
                        break;
                    }

                    fragment = _fragment + 1;
                }

                if (fragment >= _audio.Fragments.Count)
                {
                    request.Sample = null; // the end of the song
                    return;
                }

                var frames = await _audio.FramesAsync(fragment, _nextStart).ConfigureAwait(false);
                lock (_lock)
                {
                    _fragment = fragment;
                    _frames = frames;
                    _next = 0;
                    if (frames.Length > 0) _nextStart = frames[^1].StartTicks + frames[^1].DurationTicks;
                }
            }

            var bytes = await _audio.AdtsFrameAsync(frame).ConfigureAwait(false);
            var sample = MediaStreamSample.CreateFromBuffer(
                bytes.AsBuffer(), TimeSpan.FromSeconds((double)frame.StartTicks / _audio.Timescale));
            sample.Duration = TimeSpan.FromSeconds((double)frame.DurationTicks / _audio.Timescale);
            request.Sample = sample;
        }
        catch (Exception)
        {
            // A fetch that died ends the track here; the service's failure path reloads it.
            request.Sample = null;
        }
        finally
        {
            deferral.Complete();
        }
    }
}
