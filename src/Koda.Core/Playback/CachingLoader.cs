using System.IO;
using System.Net;
using System.Net.Http;
using Koda.Core.Storage;

namespace Koda.Core.Playback;

/// <summary>
/// Feeds a player a song from YouTube while keeping every byte that arrives, so
/// next time the song plays from the file.
///
/// The player cannot load a <c>koda-cache</c> resource by itself, so it asks this
/// loader for each byte range it wants. Bytes already on disk are answered from
/// the file. For the rest one ranged request runs at a time, from the first byte
/// any waiting request needs to the next byte already held; what arrives is
/// written to the file and answered from there. When nobody is waiting it fills
/// the song's remaining gaps, so a song that was skipped through still ends up
/// whole. Everything runs on one serial pump.
///
/// Ported 1:1 from the KodaCore CachingLoader.swift contract. Only the last mile
/// differs per platform: there each waiting <c>AVAssetResourceLoadingRequest</c>
/// gets its bytes as they arrive, here each wait is a <see cref="ReadAsync"/>
/// task the platform stream shim awaits. The AV loader's <c>didCancel</c> has no
/// Windows counterpart: Media Foundation drops a whole source, never one read.
/// </summary>
public sealed class CachingLoader : IDisposable
{
    /// <summary>A waiting request this close ahead of the running one is left to it rather
    /// than starting a request of its own.</summary>
    private static readonly long ReachAhead = 512 * 1024;

    /// <summary>The most handed to the player in one go.</summary>
    private static readonly long Slice = 512 * 1024;

    private readonly ResolvedStream _stream;
    private readonly long _length;
    private readonly AudioStore _store;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _pump = new(1, 1);
    private readonly List<Pending> _requests = [];
    private readonly Uri _url;
    private readonly string _userAgent;
    /// <summary>Where the running request's next byte belongs, and where it stops.</summary>
    private long _taskOffset;
    private long _taskEnd;
    private bool _taskRunning;
    /// <summary>Identity of the running fetch; a superseded or spent task's events are dropped.</summary>
    private object? _taskToken;
    private CancellationTokenSource? _taskCts;
    private Exception? _failure;
    private bool _disposed;

    /// <summary>A read the player is waiting on.</summary>
    private sealed class Pending(long offset, byte[] destination)
    {
        public long Offset { get; } = offset;
        public byte[] Destination { get; } = destination;
        /// <summary>The next byte the request wants, advanced as bytes are answered.</summary>
        public long CurrentOffset { get; set; } = offset;
        /// <summary>The byte past the last one the request wants.</summary>
        public long End { get; set; }
        public TaskCompletionSource<long> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The Swift <c>init?</c> returns nil — here Create returns null — when the
    /// stream's length isn't known: without it the file can't be laid out.</summary>
    /// <param name="handler">The transport; tests pass a recorded one. A plain socket handler when null.</param>
    public static CachingLoader? Create(ResolvedStream stream, Song? song, AudioStore store, HttpMessageHandler? handler = null)
    {
        if (stream.Url.IsFile || stream.ContentLength is not { } length || length <= 0)
        {
            return null;
        }

        return new CachingLoader(stream, song, store, length, handler);
    }

    private CachingLoader(ResolvedStream stream, Song? song, AudioStore store, long length, HttpMessageHandler? handler)
    {
        _stream = stream;
        _url = stream.Url;
        _userAgent = stream.UserAgent;
        _length = length;
        _store = store;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            // The fetch guards its own lifetime; one request at a time, per the contract.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        _store.Prepare(stream, song);
        _store.BeginUse(stream.VideoId);
    }

    /// <summary>The song whose bytes this loader holds, for the store's bookkeeping.</summary>
    public string VideoId => _stream.VideoId;

    public void Dispose()
    {
        _pump.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // The Swift deinit's invalidateAndCancel: the running request stops, and
            // nobody is left waiting on a read that can no longer be answered.
            _taskToken = null;
            _taskRunning = false;
            _taskCts?.Cancel();
            _taskCts = null;
            foreach (var request in _requests)
            {
                request.Done.TrySetException(new ObjectDisposedException(nameof(CachingLoader)));
            }

            _requests.Clear();
        }
        finally
        {
            _pump.Release();
        }

        _store.EndUse(_stream.VideoId);
        _http.Dispose();
    }

    // MARK: Answering

    /// <summary>A read the player waits on: fill the destination from the store, fetching
    /// what is missing, under the loader's slice cap. The result is the bytes copied;
    /// a short copy only happens at the song's end, the real end of the file.</summary>
    public async Task<long> ReadAsync(long offset, byte[] destination, CancellationToken cancellation = default)
    {
        ThrowIfGone();

        var request = new Pending(offset, destination);
        await _pump.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ThrowIfGone();
            request.End = Math.Min(_length, offset + destination.Length);
            _requests.Add(request);
            Serve();
        }
        finally
        {
            _pump.Release();
        }

        // The AV loader's didCancel: a read the player gave up on leaves the waiting
        // list. The running fetch keeps going; what it brings is kept either way.
        using var withdraw = cancellation.Register(() => _ = WithdrawAsync(request));
        return await request.Done.Task.ConfigureAwait(false);
    }

    private async Task WithdrawAsync(Pending request)
    {
        await _pump.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_requests.Remove(request))
            {
                request.Done.TrySetCanceled();
            }
        }
        finally
        {
            _pump.Release();
        }
    }

    private void ThrowIfGone()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CachingLoader));
        }
    }

    // MARK: Serving (always with the pump held)

    private void Serve()
    {
        for (var i = _requests.Count - 1; i >= 0; i--)
        {
            if (Answer(_requests[i]))
            {
                _requests.RemoveAt(i);
            }
        }

        FetchIfNeeded();
    }

    /// <summary>Answer as much of <paramref name="request"/> as the file holds; true once it is finished.</summary>
    private bool Answer(Pending request)
    {
        while (request.CurrentOffset < request.End)
        {
            var count = Math.Min(Math.Min(
                _store.ContiguousLength(_stream.VideoId, request.CurrentOffset),
                request.End - request.CurrentOffset), Slice);
            if (count <= 0 || _store.Read(_stream.VideoId, request.CurrentOffset, count) is not { } bytes)
            {
                break;
            }

            Buffer.BlockCopy(
                bytes, 0,
                request.Destination, (int)(request.CurrentOffset - request.Offset),
                bytes.Length);
            request.CurrentOffset += bytes.Length;
        }

        if (request.CurrentOffset >= request.End)
        {
            request.Done.TrySetResult(request.CurrentOffset - request.Offset);
            return true;
        }

        if (_failure is { } failure)
        {
            request.Done.TrySetException(failure);
            return true;
        }

        return false;
    }

    // MARK: Fetching

    private void FetchIfNeeded()
    {
        if (_failure is not null || _disposed)
        {
            return;
        }

        var needed = -1L;
        foreach (var request in _requests)
        {
            needed = needed < 0 ? request.CurrentOffset : Math.Min(needed, request.CurrentOffset);
        }

        if (needed < 0)
        {
            // Nobody waiting: fill the song's gaps while there is no request running.
            if (!_taskRunning && _store.FirstGap(_stream.VideoId) is { } whole)
            {
                Start(whole.Lower, whole.Upper);
            }

            return;
        }

        if (_taskRunning && needed >= _taskOffset && needed < Math.Min(_taskEnd, _taskOffset + ReachAhead))
        {
            return;
        }

        if (_store.FirstGap(_stream.VideoId, needed) is { } ahead)
        {
            Start(ahead.Lower, ahead.Upper);
        }
    }

    /// <summary>How long a request may go without a byte before it counts as failed: the
    /// Swift session's default <c>timeoutIntervalForRequest</c>, which is an idle timeout,
    /// not a cap on the whole transfer.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Called with the pump held. Replaces any running request, as the Swift
    /// <c>task?.cancel(); start(gap)</c> does: one request at a time, never two.</summary>
    private void Start(long lower, long upper)
    {
        _taskCts?.Cancel();
        _taskOffset = lower;
        _taskEnd = upper;
        _taskRunning = true;
        _taskToken = new object();
        _taskCts = new CancellationTokenSource(IdleTimeout);
        var token = _taskToken;
        var cts = _taskCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await FetchAsync(lower, upper, token, cts).ConfigureAwait(false);
                await EndTaskAsync(token, null).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await EndTaskAsync(token, error).ConfigureAwait(false);
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    /// <summary>One ranged request, the file's bytes written through as they arrive.
    /// Every task field is touched only under the pump and only while <paramref name="token"/>
    /// is still the running one: a superseded request must never move its successor's offset.</summary>
    private async Task FetchAsync(long lower, long upper, object token, CancellationTokenSource cts)
    {
        var cancellation = cts.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.TryAddWithoutValidation("Range", $"bytes={lower}-{upper - 1}");
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);

        if (!await AcceptAsync(token, response.StatusCode).ConfigureAwait(false))
        {
            // googlevideo refused the URL; the player fails the item and reloads it. Checked
            // before any body, so a refusal with no body cannot loop either.
            throw new IOException($"googlevideo refused the ranged request ({(int)response.StatusCode})");
        }

        await using var body = await response.Content
            .ReadAsStreamAsync(cancellation).ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        while (await body.ReadAsync(buffer, cancellation).ConfigureAwait(false) is var n && n > 0)
        {
            cts.CancelAfter(IdleTimeout);
            if (!await ForwardAsync(token, buffer, n).ConfigureAwait(false))
            {
                // Superseded: its bytes are dropped, as the Swift contract drops them.
                return;
            }
        }
    }

    /// <summary>Whether the response is audio for the range asked; checked once, on its
    /// status. A 200 means the range was ignored and the whole file is coming.</summary>
    private async Task<bool> AcceptAsync(object token, HttpStatusCode status)
    {
        await _pump.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_taskToken != token)
            {
                return true; // superseded; the read loop drops it at its first chunk
            }

            switch (status)
            {
                case HttpStatusCode.PartialContent:
                    return true;
                case HttpStatusCode.OK:
                    _taskOffset = 0;
                    _taskEnd = _length;
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _pump.Release();
        }
    }

    /// <summary>Write one arrived chunk where it belongs and answer whoever now waits.
    /// False when the request was superseded and should stop.</summary>
    private async Task<bool> ForwardAsync(object token, byte[] buffer, int count)
    {
        await _pump.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_taskToken != token)
            {
                return false;
            }

            try
            {
                _store.Write(buffer[..count], _taskOffset, _stream.VideoId);
            }
            catch (Exception error)
            {
                throw new IOException("the audio store refused the write", error);
            }

            _taskOffset += count;
            Serve();
            return true;
        }
        finally
        {
            _pump.Release();
        }
    }

    /// <summary>The fetch is done, failed or superseded: free its place, then serve once more.</summary>
    private async Task EndTaskAsync(object token, Exception? error)
    {
        await _pump.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_taskToken != token)
            {
                return;
            }

            _taskToken = null;
            _taskRunning = false;
            _taskOffset = 0;
            _taskEnd = 0;
            _taskCts = null; // disposed by its own fetch
            if (error is not null && !_disposed)
            {
                _failure = error;
            }

            Serve();
        }
        finally
        {
            _pump.Release();
        }
    }
}
