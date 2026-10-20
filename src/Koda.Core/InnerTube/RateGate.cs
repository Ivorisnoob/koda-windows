namespace Koda.Core;

/// <summary>
/// The record of YouTube telling this client to slow down. Ported from the
/// Android sibling's <c>YouTubeRateLimit</c>, whose two narrow rules carry over:
///
/// - Only HTTP 429 arms it. A googlevideo 403 is a visitorData verdict with its
///   own recovery (<see cref="StreamResolver"/>), not a throttle.
/// - The hold gates fan-out work only (background refreshes, prefetch, bulk
///   backfills), which asks <see cref="IsHeld"/> before it starts. It never
///   gates a request a listener is waiting on: someone who presses play or
///   opens a page during a hold still gets a request.
///
/// [scar] The first port was a 6-per-10s token bucket in front of every
/// WEB_REMIX call, which neither KodaCore (no limiter at all) nor Android
/// ever had: an album with a long audio playlist spent the budget and the
/// next page the listener opened sat waiting on a local timer.
/// </summary>
public sealed class RateGate
{
    /// <summary>The hold after a 429 that named no Retry-After, as Android's.</summary>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromMinutes(5);

    /// <summary>The ceiling on a server-named Retry-After, as Android's.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(30);

    private readonly object _lock = new();
    private DateTimeOffset _heldUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// Raised when a 429 arms (or lengthens) the hold, with how long it lasts.
    /// Informational: the shell shows it, nothing waits on it.
    /// </summary>
    public event Action<TimeSpan>? Throttled;

    /// <summary>
    /// Record a response status. Returns true when it was a rate limit, so the
    /// caller can stop rather than fall back to a costlier request.
    /// </summary>
    public bool Hit(int status, TimeSpan? retryAfter = null)
    {
        if (status != 429) return false;
        var hold = retryAfter is { } named
            ? TimeSpan.FromTicks(Math.Clamp(named.Ticks, DefaultHold.Ticks, MaxHold.Ticks))
            : DefaultHold;
        var until = DateTimeOffset.UtcNow + hold;
        lock (_lock)
        {
            // Never shorten a hold: a second 429's shorter window must not undo a longer one.
            if (until > _heldUntil) _heldUntil = until;
        }
        try
        {
            Throttled?.Invoke(hold);
        }
        catch (Exception)
        {
            // A listener that throws must never break the request path.
        }
        return true;
    }

    /// <summary>Whether fan-out work should stand down right now.</summary>
    public bool IsHeld => Remaining > TimeSpan.Zero;

    /// <summary>What is left of the hold, zero when there is none.</summary>
    public TimeSpan Remaining
    {
        get
        {
            lock (_lock)
            {
                var left = _heldUntil - DateTimeOffset.UtcNow;
                // A clock moved backwards must not pin the hold forever.
                if (left > MaxHold)
                {
                    _heldUntil = DateTimeOffset.MinValue;
                    return TimeSpan.Zero;
                }
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Lift the hold, for a request that succeeded after it expired.</summary>
    public void Clear()
    {
        lock (_lock) _heldUntil = DateTimeOffset.MinValue;
    }
}
