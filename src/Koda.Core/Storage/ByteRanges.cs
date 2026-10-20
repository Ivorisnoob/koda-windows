namespace Koda.Core.Storage;

/// <summary>
/// Which bytes of a file are on disk: sorted ranges that neither overlap nor touch.
/// Half-open like the Swift port's <c>Range&lt;Int64&gt;</c>: lower held, upper not.
/// Ported 1:1 from the KodaCore ByteRanges.swift contract.
/// </summary>
public sealed class ByteRanges
{
    private readonly List<(long Lower, long Upper)> _ranges = [];

    /// <summary>Bytes held in all.</summary>
    public long Count => _ranges.Sum(held => held.Upper - held.Lower);

    /// <summary>True when nothing is held.</summary>
    public bool IsEmpty => _ranges.Count == 0;

    /// <summary>Read-only view, in order.</summary>
    public IReadOnlyList<(long Lower, long Upper)> InOrder => _ranges;

    public void Insert(long lower, long upper)
    {
        if (lower >= upper)
        {
            return;
        }

        var kept = new List<(long Lower, long Upper)>();
        foreach (var held in _ranges)
        {
            if (held.Upper < lower || held.Lower > upper)
            {
                kept.Add(held);
            }
            else
            {
                // Overlapping or touching: one range from then on.
                lower = Math.Min(lower, held.Lower);
                upper = Math.Max(upper, held.Upper);
            }
        }

        kept.Add((lower, upper));
        kept.Sort((a, b) => a.Lower.CompareTo(b.Lower));
        _ranges.Clear();
        _ranges.AddRange(kept);
    }

    /// <summary>Held bytes, cleared (a file that failed to play).</summary>
    public void Clear() => _ranges.Clear();

    /// <summary>Held bytes copied from another, for a reader's snapshot.</summary>
    public void ReplaceWith(ByteRanges other)
    {
        _ranges.Clear();
        _ranges.AddRange(other._ranges);
    }

    /// <summary>How many bytes from <paramref name="offset"/> on are held without a gap.</summary>
    public long ContiguousLength(long offset)
    {
        foreach (var held in _ranges)
        {
            if (held.Lower <= offset && offset < held.Upper)
            {
                return held.Upper - offset;
            }
        }

        return 0;
    }

    public bool Contains(long lower, long upper) =>
        lower >= upper || ContiguousLength(lower) >= upper - lower;

    /// <summary>The first missing byte at or after <paramref name="offset"/>, if one is before <paramref name="end"/>.</summary>
    public long? FirstMissing(long offset, long end)
    {
        var missing = offset + ContiguousLength(offset);
        return missing < end ? missing : null;
    }

    /// <summary>Where a gap starting at the missing byte <paramref name="offset"/> ends: the next held range, or <paramref name="end"/>.</summary>
    public long GapEnd(long offset, long end)
    {
        foreach (var held in _ranges)
        {
            if (held.Lower > offset)
            {
                return Math.Min(held.Lower, end);
            }
        }

        return end;
    }
}
