namespace Koda.Core;

/// <summary>
/// Pages already fetched, kept for a few minutes so going back to one, or
/// switching away and back, shows it at once instead of loading it again.
/// In memory only; the newest are kept. Failures are never cached.
/// Ported from the KodaCore PageCache actor.
///
/// [scar] The first port held one semaphore across <c>load()</c>: every page
/// fetch in the app queued behind whichever loaded first, and a loader that
/// asked the cache for another page deadlocked. A Swift actor is reentrant
/// across <c>await</c>, so the contract never serialized loads. Here the lock
/// guards only the dictionaries; loads of different keys run side by side and
/// callers after the same key share one load.
/// </summary>
public sealed class PageCache
{
    private sealed record Entry(object Value, DateTimeOffset At);

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = [];
    /// <summary>Loads in flight, by key: a <c>Lazy&lt;Task&lt;T&gt;&gt;</c> each.</summary>
    private readonly Dictionary<string, object> _loading = [];
    private readonly TimeSpan _lifetime;
    private readonly int _limit;
    private int _generation;

    public PageCache(TimeSpan? lifetime = null, int limit = 40)
    {
        _lifetime = lifetime ?? TimeSpan.FromMinutes(5);
        _limit = limit;
    }

    /// <summary>The cached page for <paramref name="key"/> if it is fresh, else the loader's, which is then kept.</summary>
    public async Task<T> Value<T>(string key, Func<Task<T>> load)
    {
        Lazy<Task<T>> task;
        int generation;
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry) && DateTimeOffset.UtcNow - entry.At < _lifetime
                && entry.Value is T hit)
            {
                return hit;
            }
            if (_loading.TryGetValue(key, out var running) && running is Lazy<Task<T>> shared)
            {
                task = shared;
            }
            else
            {
                // Started outside the lock: the loader's synchronous part must not run under it.
                task = new Lazy<Task<T>>(load);
                _loading[key] = task;
            }
            generation = _generation;
        }

        try
        {
            var value = await task.Value.ConfigureAwait(false);
            lock (_lock)
            {
                // A RemoveAll while this load ran means the caller asked for a fresh fetch;
                // the answer still goes to this caller but is not kept.
                if (generation == _generation)
                {
                    _entries[key] = new Entry(value!, DateTimeOffset.UtcNow);
                    if (_entries.Count > _limit) EvictOldestLocked();
                }
            }
            return value;
        }
        finally
        {
            lock (_lock)
            {
                if (_loading.TryGetValue(key, out var registered) && registered == task) _loading.Remove(key);
            }
        }
    }

    public void RemoveAll()
    {
        lock (_lock)
        {
            _entries.Clear();
            _loading.Clear();
            _generation++;
        }
    }

    private void EvictOldestLocked()
    {
        string? oldest = null;
        var oldestAt = DateTimeOffset.MaxValue;
        foreach (var (key, entry) in _entries)
        {
            if (entry.At < oldestAt)
            {
                oldestAt = entry.At;
                oldest = key;
            }
        }
        if (oldest is not null) _entries.Remove(oldest);
    }
}
