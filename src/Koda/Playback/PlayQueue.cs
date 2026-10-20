using System;
using System.Collections.Generic;
using System.Linq;
using Koda.Core;

namespace Koda.Playback;

/// <summary>One occurrence of a song in the queue. The same song can be queued twice, so
/// anything planned across an await (a skip, a removal, a jump) anchors on this id.</summary>
public sealed record QueueItem(Guid Id, Song Song)
{
    public static QueueItem Of(Song song) => new(Guid.NewGuid(), song);
}

public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>
/// The play queue, ported from KodaCore PlayQueue.swift: what plays now, what came before
/// and what comes next. <see cref="Items"/> is always the order playback follows. Shuffle
/// reorders it and remembers the order it replaced; turning shuffle off puts that order
/// back with the current song still current.
/// </summary>
public sealed class PlayQueue
{
    private List<QueueItem> _items = [];
    /// <summary>The order before shuffle, by occurrence. Empty while not shuffled.</summary>
    private List<Guid> _unshuffled = [];

    public IReadOnlyList<QueueItem> Items => _items;
    public int? CurrentIndex { get; private set; }
    public bool IsShuffled { get; private set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    public QueueItem? Current => CurrentIndex is { } i ? _items[i] : null;
    public bool IsEmpty => _items.Count == 0;

    /// <summary>What plays after the current song, in order.</summary>
    public IReadOnlyList<QueueItem> Upcoming => CurrentIndex is { } i ? _items.Skip(i + 1).ToArray() : _items.ToArray();

    // MARK: Replacing

    /// <summary>Play <paramref name="songs"/> from <paramref name="start"/>; shuffled, the start song plays
    /// first and the rest follow in random order.</summary>
    public void Replace(IReadOnlyList<Song> songs, int start = 0, bool shuffled = false)
    {
        _items = [.. songs.Select(QueueItem.Of)];
        _unshuffled = [];
        IsShuffled = false;
        CurrentIndex = _items.Count == 0 ? null : Math.Clamp(start, 0, _items.Count - 1);
        if (shuffled) SetShuffle(true);
    }

    public void Clear()
    {
        _items = [];
        _unshuffled = [];
        IsShuffled = false;
        CurrentIndex = null;
    }

    // MARK: Adding and removing

    /// <summary>Insert right after the current song, in the order given.</summary>
    public void PlayNext(IReadOnlyList<Song> songs)
    {
        var added = songs.Select(QueueItem.Of).ToList();
        if (added.Count == 0) return;
        if (CurrentIndex is not { } current)
        {
            Append(songs);
            return;
        }

        _items.InsertRange(current + 1, added);
        if (IsShuffled && _unshuffled.IndexOf(_items[current].Id) is >= 0 and var anchor)
        {
            _unshuffled.InsertRange(anchor + 1, added.Select(a => a.Id));
        }
    }

    /// <summary>Add to the end of the queue.</summary>
    public void Append(IReadOnlyList<Song> songs)
    {
        var added = songs.Select(QueueItem.Of).ToList();
        _items.AddRange(added);
        if (IsShuffled) _unshuffled.AddRange(added.Select(a => a.Id));
        if (CurrentIndex is null && _items.Count > 0) CurrentIndex = 0;
    }

    /// <summary>Remove an occurrence. The current song cannot be removed this way; skip it instead.</summary>
    public bool Remove(Guid id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0 || index == CurrentIndex) return false;
        _items.RemoveAt(index);
        _unshuffled.Remove(id);
        if (CurrentIndex is { } current && index < current) CurrentIndex = current - 1;
        return true;
    }

    /// <summary>Drop everything after the current song.</summary>
    public void ClearUpcoming()
    {
        if (CurrentIndex is not { } current)
        {
            Clear();
            return;
        }

        var dropped = _items.Skip(current + 1).Select(i => i.Id).ToHashSet();
        _items.RemoveRange(current + 1, _items.Count - current - 1);
        _unshuffled.RemoveAll(dropped.Contains);
    }

    /// <summary>Move one upcoming song; offsets count from the first upcoming song, as a list of them does.</summary>
    public void MoveUpcoming(int from, int to)
    {
        var start = (CurrentIndex ?? -1) + 1;
        var a = start + from;
        var b = start + to;
        if (from == to || a < start || a >= _items.Count || b < start || b >= _items.Count) return;
        var item = _items[a];
        _items.RemoveAt(a);
        _items.Insert(b, item);
    }

    /// <summary>Forget all but the last <paramref name="keeping"/> songs already played, so a long
    /// session's queue stays small; going back still reaches the recent ones.</summary>
    public void TrimHistory(int keeping)
    {
        if (CurrentIndex is not { } current || current <= keeping) return;
        var drop = current - keeping;
        var dropped = _items.Take(drop).Select(i => i.Id).ToHashSet();
        _items.RemoveRange(0, drop);
        CurrentIndex = current - drop;
        if (IsShuffled) _unshuffled.RemoveAll(dropped.Contains);
    }

    // MARK: Moving through the queue

    public bool Jump(Guid id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0) return false;
        CurrentIndex = index;
        return true;
    }

    /// <summary>The index that would play next. A song ending on its own repeats under repeat
    /// one; a skip always moves on.</summary>
    public int? NextIndex(bool automatic)
    {
        if (CurrentIndex is not { } current) return _items.Count == 0 ? null : 0;
        if (automatic && Repeat == RepeatMode.One) return current;
        if (current + 1 < _items.Count) return current + 1;
        return Repeat == RepeatMode.Off ? null : 0;
    }

    public QueueItem? PeekNext(bool automatic) => NextIndex(automatic) is { } i ? _items[i] : null;

    /// <summary>Move to the next song; null at the end of a queue that does not repeat, the queue unchanged.</summary>
    public QueueItem? Advance(bool automatic)
    {
        if (NextIndex(automatic) is not { } next) return null;
        CurrentIndex = next;
        return _items[next];
    }

    /// <summary>Move to the previous song; at the start it wraps under repeat and otherwise stays.</summary>
    public QueueItem? Retreat()
    {
        if (CurrentIndex is not { } current) return null;
        if (current > 0) CurrentIndex = current - 1;
        else if (Repeat != RepeatMode.Off && _items.Count > 1) CurrentIndex = _items.Count - 1;
        return Current;
    }

    // MARK: Shuffle

    /// <summary>On: the current song stays current and every other song follows in random order.
    /// Off: the order from before shuffle comes back, songs added meanwhile kept where they were added.</summary>
    public void SetShuffle(bool on)
    {
        if (on == IsShuffled) return;
        if (on)
        {
            _unshuffled = [.. _items.Select(i => i.Id)];
            if (CurrentIndex is not { } current)
            {
                var all = _items.ToArray();
                Random.Shared.Shuffle(all);
                _items = [.. all];
            }
            else
            {
                var playing = _items[current];
                var rest = _items.Where((_, i) => i != current).ToArray();
                Random.Shared.Shuffle(rest);
                _items = [playing, .. rest];
                CurrentIndex = 0;
            }
        }
        else
        {
            var currentId = Current?.Id;
            var byId = _items.ToDictionary(i => i.Id);
            var restored = _unshuffled.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            // Anything the shuffled order gained that the old order never saw goes at the end.
            var known = _unshuffled.ToHashSet();
            restored.AddRange(_items.Where(i => !known.Contains(i.Id)));
            _items = restored;
            CurrentIndex = currentId is { } id && _items.FindIndex(i => i.Id == id) is >= 0 and var at ? at : (_items.Count == 0 ? null : 0);
            _unshuffled = [];
        }

        IsShuffled = on;
    }

    // MARK: Saving (persistQueue on the Mac)

    public sealed record Saved(Song[] Songs, int? Current, bool Shuffled, RepeatMode Repeat);

    public Saved Save() => new([.. _items.Select(i => i.Song)], CurrentIndex, IsShuffled, Repeat);

    public static PlayQueue Restore(Saved saved)
    {
        var queue = new PlayQueue { Repeat = saved.Repeat };
        queue._items = [.. saved.Songs.Select(QueueItem.Of)];
        queue.CurrentIndex = queue._items.Count == 0 ? null : Math.Clamp(saved.Current ?? 0, 0, queue._items.Count - 1);
        // The pre-shuffle order is not kept across launches; the shuffled order simply stands.
        queue.IsShuffled = false;
        return queue;
    }
}
