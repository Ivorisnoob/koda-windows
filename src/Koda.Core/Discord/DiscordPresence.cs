using System.IO;
using System.IO.Pipes;

namespace Koda.Core.Discord;

/// <summary>What happened to one frame sent to Discord.</summary>
public enum DiscordSendResult
{
    Accepted,
    /// <summary>Discord answered and refused the card.</summary>
    Rejected,
    /// <summary>The connection is gone.</summary>
    Failed,
}

/// <summary>A live connection to Discord; an interface so tests can stand in for the Discord app.</summary>
public interface IDiscordConnection : IAsyncDisposable
{
    /// <summary>Shows <paramref name="activity"/> on the profile; null takes the card down.</summary>
    Task<DiscordSendResult> SetActivityAsync(DiscordActivity? activity);

    Task<bool> IsAliveAsync();
}

/// <summary>
/// Shows what Koda is playing on the listener's Discord profile, through the Discord app on
/// this PC. The Windows counterpart of the Mac's and Koda for Android's presence, with the same
/// application. Ported from KodaCore DiscordPresence.swift.
///
/// When a card is on the profile:
/// <list type="bullet">
/// <item>Only while the listener has it on and listening history is on.</item>
/// <item>A playing song shows straight away. A frame goes only when something the card shows
/// changes, or when its progress bar has drifted (a seek, a long buffer); Discord runs the bar
/// on its own clock, so steady playback sends nothing.</item>
/// <item>A pause swaps in the same card without a bar, taken down after a minute. A pause never
/// puts a card up on its own: a session restored paused at launch stays off the profile until
/// it plays.</item>
/// </list>
/// With no card to show, the connection is let go too. While one should show and Discord isn't
/// there, Koda looks for it again every 15 seconds, and checks a live connection as often, so
/// quitting and reopening Discord brings the card back. Playback never waits on any of this:
/// everything here runs beside it and every failure is swallowed.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    public enum Phase
    {
        /// <summary>Turned off, or listening history is off.</summary>
        Off,
        /// <summary>On, with nothing playing.</summary>
        Waiting,
        /// <summary>A song should show and Discord isn't open (or isn't answering).</summary>
        Unavailable,
        Showing,
        /// <summary>Discord is open and refused the song's card.</summary>
        Refused,
    }

    /// <summary>Koda's application in the Discord Developer Portal, the same one the Mac and Koda for
    /// Android use. Public by design; it is not a secret.</summary>
    public const string ApplicationId = "1556607739231346708";

    public sealed record Timing
    {
        /// <summary>The least time between two frames for the same card.</summary>
        public TimeSpan PushInterval { get; init; } = TimeSpan.FromSeconds(5);
        /// <summary>How far the bar's end may move before it is worth a frame.</summary>
        public double SeekDrift { get; init; } = 3;
        /// <summary>How long a paused card stays before it is taken down.</summary>
        public TimeSpan PauseLinger { get; init; } = TimeSpan.FromSeconds(60);
        /// <summary>How often a missing Discord is looked for, and a live one checked.</summary>
        public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(15);
        /// <summary>Discord takes this many activity updates per window; more are dropped.</summary>
        public int RateLimit { get; init; } = 5;
        public TimeSpan RateWindow { get; init; } = TimeSpan.FromSeconds(20);
    }

    private readonly Func<Task<IDiscordConnection?>> _connector;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timing _timing;
    private readonly object _lock = new();
    private readonly Timer _wake;
    private readonly List<DateTimeOffset> _pushes = [];

    private bool _allowed;
    private DiscordNowPlaying? _song;
    private DateTimeOffset _sampledAt;
    private IDiscordConnection? _connection;
    /// <summary>The card on the profile, without its bar; null when none is showing.</summary>
    private DiscordActivity? _shown;
    private double? _shownEnd;
    /// <summary>Discord refused the newer fields on this connection: send the basic card.</summary>
    private bool _sendsBasic;
    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;
    private DateTimeOffset? _pausedSince;
    private bool _running;
    private bool _needsSync;
    private bool _disposed;

    public DiscordPresence(Func<Task<IDiscordConnection?>>? connector = null, Timing? timing = null, Func<DateTimeOffset>? clock = null)
    {
        _connector = connector ?? (() => DiscordPipeConnection.ConnectAsync(ApplicationId));
        _timing = timing ?? new Timing();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _wake = new Timer(_ => Schedule(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Phase Status { get; private set; } = Phase.Off;

    /// <summary>The status changed, for the settings line.</summary>
    public event Action? Changed;

    /// <summary>What is true now: whether the card may show, and what plays. Cheap to call often;
    /// a frame goes to Discord only when the card would look different.</summary>
    public void Update(bool allowed, DiscordNowPlaying? song)
    {
        lock (_lock)
        {
            _allowed = allowed;
            _song = song;
            _sampledAt = _clock();
        }

        Schedule();
    }

    /// <summary>Runs <see cref="SyncAsync"/> until nothing is left to do, never two at once.</summary>
    private void Schedule()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _needsSync = true;
            if (_running) return;
            _running = true;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                lock (_lock)
                {
                    if (!_needsSync || _disposed)
                    {
                        _running = false;
                        return;
                    }

                    _needsSync = false;
                }

                try
                {
                    await SyncAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Discord is a nicety; nothing it does may reach playback.
                }
            }
        });
    }

    /// <summary>The song as it is at <paramref name="now"/>, its position run forward while playing.</summary>
    private (bool Allowed, DiscordNowPlaying? Song) Snapshot(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_song is not { } song) return (_allowed, null);
            var position = song.IsPlaying ? song.Position + (now - _sampledAt).TotalSeconds : song.Position;
            return (_allowed, song with { Position = position });
        }
    }

    private async Task SyncAsync()
    {
        var now = _clock();
        var (allowed, song) = Snapshot(now);
        if (!allowed)
        {
            await TakeDownAsync().ConfigureAwait(false);
            SetStatus(Phase.Off);
            return;
        }

        if (song is null || DiscordActivity.For(song, now) is not { } card)
        {
            await TakeDownAsync().ConfigureAwait(false);
            SetStatus(Phase.Waiting);
            return;
        }

        if (song.IsPlaying)
        {
            _pausedSince = null;
        }
        else
        {
            // Nothing on the profile: a pause has no card to replace.
            if (_shown is null)
            {
                await TakeDownAsync().ConfigureAwait(false);
                SetStatus(Phase.Waiting);
                return;
            }

            var since = _pausedSince ?? now;
            _pausedSince = since;
            if (now - since >= _timing.PauseLinger)
            {
                await TakeDownAsync().ConfigureAwait(false);
                SetStatus(Phase.Waiting);
                return;
            }
        }

        // A connection that went quiet is checked now and then, so a Discord that quit and came
        // back gets the card again.
        if (_connection is { } live && now >= _nextCheck)
        {
            _nextCheck = now + _timing.RetryInterval;
            if (!await live.IsAliveAsync().ConfigureAwait(false)) await DropAsync(live).ConfigureAwait(false);
        }

        var end = DiscordActivity.EndTime(song, now);
        if (_connection is not null && card.Content.Same(_shown))
        {
            var drifted = (end, _shownEnd) switch
            {
                ({ } a, { } b) => Math.Abs(a - b) > _timing.SeekDrift,
                (null, null) => false,
                _ => true,
            };
            if (!drifted)
            {
                ArmWake(now);
                return;
            }

            if (_pushes.Count > 0 && now - _pushes[^1] < _timing.PushInterval)
            {
                ArmWake(now, _pushes[^1] + _timing.PushInterval);
                return;
            }
        }

        _pushes.RemoveAll(p => now - p >= _timing.RateWindow);
        if (_pushes.Count >= _timing.RateLimit)
        {
            ArmWake(now, _pushes[0] + _timing.RateWindow);
            return;
        }

        if (await ConnectAsync(now).ConfigureAwait(false) is not { } connection)
        {
            SetStatus(Phase.Unavailable);
            ArmWake(now);
            return;
        }

        var result = await connection.SetActivityAsync(_sendsBasic ? card.Basic : card).ConfigureAwait(false);
        if (result == DiscordSendResult.Rejected && !_sendsBasic)
        {
            // An older Discord that doesn't take the newer fields still takes the rest.
            _sendsBasic = true;
            _pushes.Add(now);
            result = await connection.SetActivityAsync(card.Basic).ConfigureAwait(false);
        }

        _pushes.Add(now);
        switch (result)
        {
            case DiscordSendResult.Accepted:
                _shown = card.Content;
                _shownEnd = end;
                SetStatus(Phase.Showing);
                break;
            case DiscordSendResult.Rejected:
                // Remembered as shown, so the same refused card isn't sent again and again.
                _shown = card.Content;
                _shownEnd = end;
                SetStatus(Phase.Refused);
                break;
            default:
                await DropAsync(connection).ConfigureAwait(false);
                _lastAttempt = now;
                SetStatus(Phase.Unavailable);
                break;
        }

        ArmWake(now);
    }

    /// <summary>The live connection, or a new one when the retry interval allows.</summary>
    private async Task<IDiscordConnection?> ConnectAsync(DateTimeOffset now)
    {
        if (_connection is not null) return _connection;
        if (_lastAttempt is { } last && now - last < _timing.RetryInterval) return null;
        _lastAttempt = now;
        if (await _connector().ConfigureAwait(false) is not { } fresh) return null;
        _connection = fresh;
        _sendsBasic = false;
        _lastAttempt = null;
        _nextCheck = _clock() + _timing.RetryInterval;
        return fresh;
    }

    private async Task DropAsync(IDiscordConnection live)
    {
        if (!ReferenceEquals(_connection, live)) return;
        _connection = null;
        _shown = null;
        _shownEnd = null;
        await live.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Takes the card down and lets go of Discord, so Koda never keeps it busy for nothing.</summary>
    private async Task TakeDownAsync()
    {
        _pausedSince = null;
        _wake.Change(Timeout.Infinite, Timeout.Infinite);
        var wasShown = _shown is not null;
        _shown = null;
        _shownEnd = null;
        if (_connection is not { } live) return;
        _connection = null;
        if (wasShown) await live.SetActivityAsync(null).ConfigureAwait(false);
        await live.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Comes back at <paramref name="at"/>, or at the next moment something is due: the end of
    /// a pause, a retry, or a check on the connection.</summary>
    private void ArmWake(DateTimeOffset now, DateTimeOffset? at = null)
    {
        var due = now + _timing.RetryInterval;
        if (at is { } a && a < due) due = a;
        if (_pausedSince is { } paused && paused + _timing.PauseLinger < due) due = paused + _timing.PauseLinger;
        var delay = due - now;
        if (delay < TimeSpan.FromMilliseconds(50)) delay = TimeSpan.FromMilliseconds(50);
        lock (_lock)
        {
            if (!_disposed) _wake.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SetStatus(Phase status)
    {
        if (Status == status) return;
        Status = status;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // The settings line is not worth a broken presence.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _wake.Dispose();
        if (_connection is { } live)
        {
            // Take the card down on the way out, without holding the app up.
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_shown is not null) await live.SetActivityAsync(null).ConfigureAwait(false);
                    await live.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Discord cleans up a card whose process is gone.
                }
            });
        }
    }
}

/// <summary>The Discord app on this PC, over its local RPC pipe (<c>\\.\pipe\discord-ipc-0</c> to <c>-9</c>).</summary>
public sealed class DiscordPipeConnection : IDiscordConnection
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _pid = Environment.ProcessId;

    private DiscordPipeConnection(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>Connects and handshakes with the first Discord that answers, or returns null when none
    /// is running or it refuses Koda's application.</summary>
    public static async Task<IDiscordConnection?> ConnectAsync(string clientId)
    {
        for (var index = 0; index < 10; index++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using (var connecting = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
                {
                    await pipe.ConnectAsync(connecting.Token).ConfigureAwait(false);
                }

                var connection = new DiscordPipeConnection(pipe);
                if (await connection.HandshakeAsync(clientId).ConfigureAwait(false))
                {
                    return connection;
                }

                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<bool> HandshakeAsync(string clientId)
    {
        if (!await WriteAsync(DiscordIpc.Opcode.Handshake, DiscordIpc.Handshake(clientId)).ConfigureAwait(false)) return false;
        for (var i = 0; i < 4; i++)
        {
            if (await ReadAsync().ConfigureAwait(false) is not { } frame) return false;
            var (opcode, payload) = frame;
            switch (opcode)
            {
                case DiscordIpc.Opcode.Frame when DiscordIpc.Read(payload).Kind == DiscordIpc.ReplyKind.Ready:
                    return true;
                case DiscordIpc.Opcode.Ping:
                    if (!await WriteAsync(DiscordIpc.Opcode.Pong, payload).ConfigureAwait(false)) return false;
                    break;
                case DiscordIpc.Opcode.Close:
                    // An application id Discord doesn't know, or a Discord refusing RPC.
                    return false;
            }
        }

        return false;
    }

    public async Task<DiscordSendResult> SetActivityAsync(DiscordActivity? activity)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var nonce = Guid.NewGuid().ToString();
            if (!await WriteAsync(DiscordIpc.Opcode.Frame, DiscordIpc.SetActivity(activity, _pid, nonce)).ConfigureAwait(false))
            {
                return DiscordSendResult.Failed;
            }

            // Discord answers each command; anything else first (a ping, an event) is handled and skipped.
            for (var i = 0; i < 16; i++)
            {
                if (await ReadAsync().ConfigureAwait(false) is not { } frame) return DiscordSendResult.Failed;
                var (opcode, payload) = frame;
                switch (opcode)
                {
                    case DiscordIpc.Opcode.Frame:
                        var reply = DiscordIpc.Read(payload);
                        if (reply.Kind == DiscordIpc.ReplyKind.Success && reply.Nonce == nonce) return DiscordSendResult.Accepted;
                        if (reply.Kind == DiscordIpc.ReplyKind.Error && (reply.Nonce == nonce || reply.Nonce is null)) return DiscordSendResult.Rejected;
                        break;
                    case DiscordIpc.Opcode.Ping:
                        if (!await WriteAsync(DiscordIpc.Opcode.Pong, payload).ConfigureAwait(false)) return DiscordSendResult.Failed;
                        break;
                    case DiscordIpc.Opcode.Close:
                        return DiscordSendResult.Failed;
                }
            }

            return DiscordSendResult.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> IsAliveAsync() => Task.FromResult(_pipe.IsConnected);

    private async Task<bool> WriteAsync(DiscordIpc.Opcode opcode, byte[] payload)
    {
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            await _pipe.WriteAsync(DiscordIpc.Frame(opcode, payload), timeout.Token).ConfigureAwait(false);
            await _pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<(DiscordIpc.Opcode, byte[])?> ReadAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            var header = new byte[8];
            await _pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            var (opcode, length) = DiscordIpc.Header(header);
            if (length < 0 || length > DiscordIpc.MaxPayload) return null;
            var payload = new byte[length];
            await _pipe.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
            return ((DiscordIpc.Opcode)opcode, payload);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await WriteAsync(DiscordIpc.Opcode.Close, "{}"u8.ToArray()).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closing a pipe Discord already closed.
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
