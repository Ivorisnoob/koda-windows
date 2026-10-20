using System.Buffers.Binary;

namespace Koda.Core.Playback;

/// <summary>
/// The AAC frames inside a YouTube DASH audio file (itag 139/140: <c>ftyp dash</c>,
/// <c>moov</c> with <c>mvex</c>, a <c>sidx</c>, then <c>moof</c>/<c>mdat</c> pairs).
///
/// [verified October 2026] Media Foundation's MP4 source opens these files with the
/// right duration and then ends at once, from a file URI and from a stream alike, so
/// the container never reaches it: this reads the frames out and the player feeds
/// them to MF one at a time as ADTS, which every AAC decoder takes. AVFoundation on
/// the iOS sibling plays the container itself, so this has no Swift counterpart.
/// Bytes come through <see cref="ReadAt"/>, so a fragment is fetched only when it is played.
/// </summary>
public sealed class Fmp4Audio
{
    /// <summary>Reads up to <c>count</c> bytes at <c>offset</c>; fewer only at the file's end.</summary>
    public delegate Task<byte[]> ReadAt(long offset, int count, CancellationToken cancellation);

    /// <summary>One moof/mdat pair: where it starts and when, in <see cref="Timescale"/> ticks.</summary>
    public readonly record struct Fragment(long Offset, long StartTicks);

    /// <summary>One AAC frame: its bytes in the file and its place in time, in ticks.</summary>
    public readonly record struct Frame(long Offset, int Size, long StartTicks, long DurationTicks);

    private readonly ReadAt _read;
    private readonly long _length;
    private readonly uint _defaultDuration;
    private readonly uint _defaultSize;

    public uint Timescale { get; }
    public long DurationTicks { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    /// <summary>The ADTS profile (audio object type minus one, LC for HE-AAC's core).</summary>
    public int Profile { get; }
    public int SamplingIndex { get; }
    public IReadOnlyList<Fragment> Fragments { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)DurationTicks / Timescale);

    private Fmp4Audio(ReadAt read, long length, uint timescale, long duration, AudioConfig config,
        uint defaultDuration, uint defaultSize, IReadOnlyList<Fragment> fragments)
    {
        _read = read;
        _length = length;
        Timescale = timescale;
        DurationTicks = duration;
        SampleRate = config.SampleRate;
        Channels = config.Channels;
        Profile = config.Profile;
        SamplingIndex = config.SamplingIndex;
        _defaultDuration = defaultDuration;
        _defaultSize = defaultSize;
        Fragments = fragments;
    }

    public static async Task<Fmp4Audio> OpenAsync(ReadAt read, long length, CancellationToken cancellation = default)
    {
        byte[]? moov = null;
        byte[]? sidx = null;
        long sidxEnd = 0;
        long firstMoof = -1;
        long position = 0;
        while (position + 8 <= length && firstMoof < 0)
        {
            var (size, type, header) = await BoxHeaderAsync(read, position, length, cancellation);
            if (type == "moov") moov = await ReadExactAsync(read, position + header, (int)(size - header), cancellation);
            else if (type == "sidx")
            {
                sidx = await ReadExactAsync(read, position + header, (int)(size - header), cancellation);
                sidxEnd = position + size;
            }
            else if (type == "moof") firstMoof = position;
            position += size;
        }
        if (moov is null) throw new InvalidDataException("no moov box");

        var mdhd = Find(moov, "trak", "mdia", "mdhd") ?? throw new InvalidDataException("no mdhd");
        uint timescale;
        long duration;
        if (mdhd.Span[0] == 1)
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(mdhd.Span[20..]);
            duration = (long)BinaryPrimitives.ReadUInt64BigEndian(mdhd.Span[24..]);
        }
        else
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(mdhd.Span[12..]);
            duration = BinaryPrimitives.ReadUInt32BigEndian(mdhd.Span[16..]);
        }

        var stsd = Find(moov, "trak", "mdia", "minf", "stbl", "stsd") ?? throw new InvalidDataException("no stsd");
        var config = AudioConfig.FromStsd(stsd.Span);

        uint defaultDuration = 1024, defaultSize = 0;
        if (Find(moov, "mvex", "trex") is { } trex)
        {
            defaultDuration = BinaryPrimitives.ReadUInt32BigEndian(trex.Span[12..]);
            defaultSize = BinaryPrimitives.ReadUInt32BigEndian(trex.Span[16..]);
        }

        List<Fragment> fragments;
        if (sidx is not null)
        {
            fragments = FragmentsFromSidx(sidx, sidxEnd, timescale, out var sidxDuration);
            if (duration == 0) duration = sidxDuration;
        }
        else
        {
            // No index: walk the box headers. Start times come from each fragment's tfdt later.
            fragments = [];
            var walk = firstMoof;
            while (walk >= 0 && walk + 8 <= length)
            {
                var (size, type, _) = await BoxHeaderAsync(read, walk, length, cancellation);
                if (type == "moof") fragments.Add(new Fragment(walk, -1));
                walk += size;
            }
        }
        if (fragments.Count == 0) throw new InvalidDataException("no fragments");
        return new Fmp4Audio(read, length, timescale, duration, config, defaultDuration, defaultSize, fragments);
    }

    /// <summary>The fragment holding <paramref name="ticks"/>, by the index's start times.</summary>
    public int FragmentAt(long ticks)
    {
        var index = 0;
        for (var i = 0; i < Fragments.Count; i++)
        {
            if (Fragments[i].StartTicks >= 0 && Fragments[i].StartTicks <= ticks) index = i;
        }
        return index;
    }

    /// <summary>The frames of fragment <paramref name="index"/>, read from its moof.</summary>
    public async Task<Frame[]> FramesAsync(int index, long fallbackStart, CancellationToken cancellation = default)
    {
        var moofStart = Fragments[index].Offset;
        var (size, type, header) = await BoxHeaderAsync(_read, moofStart, _length, cancellation);
        if (type != "moof") throw new InvalidDataException($"expected moof at {moofStart}, found {type}");
        var moof = await ReadExactAsync(_read, moofStart + header, (int)(size - header), cancellation);
        var start = Fragments[index].StartTicks >= 0 ? Fragments[index].StartTicks : fallbackStart;
        return ParseMoof(moof, moofStart, moofStart + size, start, _defaultDuration, _defaultSize);
    }

    /// <summary>The frame's bytes behind a 7-byte ADTS header.</summary>
    public async Task<byte[]> AdtsFrameAsync(Frame frame, CancellationToken cancellation = default)
    {
        var payload = await ReadExactAsync(_read, frame.Offset, frame.Size, cancellation);
        var length = payload.Length + 7;
        var adts = new byte[length];
        adts[0] = 0xFF;
        adts[1] = 0xF1; // MPEG-4, layer 0, no CRC
        adts[2] = (byte)((Profile << 6) | (SamplingIndex << 2) | ((Channels >> 2) & 1));
        adts[3] = (byte)(((Channels & 3) << 6) | (length >> 11));
        adts[4] = (byte)((length >> 3) & 0xFF);
        adts[5] = (byte)(((length & 7) << 5) | 0x1F);
        adts[6] = 0xFC;
        payload.CopyTo(adts, 7);
        return adts;
    }

    // MARK: Boxes

    private static async Task<(long Size, string Type, int Header)> BoxHeaderAsync(ReadAt read, long at, long length, CancellationToken cancellation)
    {
        var head = await ReadExactAsync(read, at, (int)Math.Min(16, length - at), cancellation);
        long size = BinaryPrimitives.ReadUInt32BigEndian(head);
        var type = System.Text.Encoding.ASCII.GetString(head, 4, 4);
        var header = 8;
        if (size == 1 && head.Length >= 16)
        {
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8));
            header = 16;
        }
        else if (size == 0)
        {
            size = length - at; // to the end of the file
        }
        if (size < header) throw new InvalidDataException($"bad box size at {at}");
        return (size, type, header);
    }

    private static async Task<byte[]> ReadExactAsync(ReadAt read, long at, int count, CancellationToken cancellation)
    {
        var bytes = await read(at, count, cancellation);
        if (bytes.Length < count) throw new EndOfStreamException($"wanted {count} bytes at {at}, got {bytes.Length}");
        return bytes;
    }

    /// <summary>The body of the box at the end of <paramref name="path"/>, searched from a box body.</summary>
    private static ReadOnlyMemory<byte>? Find(ReadOnlyMemory<byte> body, params string[] path)
    {
        var current = body;
        foreach (var name in path)
        {
            var found = Child(current, name);
            if (found is null) return null;
            current = found.Value;
        }
        return current;
    }

    private static ReadOnlyMemory<byte>? Child(ReadOnlyMemory<byte> body, string name)
    {
        var span = body.Span;
        var at = 0;
        while (at + 8 <= span.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(span[at..]);
            var header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(span[(at + 8)..]);
                header = 16;
            }
            else if (size == 0) size = span.Length - at;
            if (size < header || at + size > span.Length) return null;
            if (span.Slice(at + 4, 4).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(name)))
                return body.Slice(at + header, (int)size - header);
            at += (int)size;
        }
        return null;
    }

    private static List<Fragment> FragmentsFromSidx(byte[] sidx, long sidxEnd, uint timescale, out long total)
    {
        var s = sidx.AsSpan();
        var version = s[0];
        var sidxScale = BinaryPrimitives.ReadUInt32BigEndian(s[8..]);
        long earliest, firstOffset;
        int at;
        if (version == 0)
        {
            earliest = BinaryPrimitives.ReadUInt32BigEndian(s[12..]);
            firstOffset = BinaryPrimitives.ReadUInt32BigEndian(s[16..]);
            at = 20;
        }
        else
        {
            earliest = (long)BinaryPrimitives.ReadUInt64BigEndian(s[12..]);
            firstOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(s[20..]);
            at = 28;
        }
        var count = BinaryPrimitives.ReadUInt16BigEndian(s[(at + 2)..]);
        at += 4;
        var fragments = new List<Fragment>(count);
        var offset = sidxEnd + firstOffset;
        var time = earliest;
        for (var i = 0; i < count && at + 12 <= s.Length; i++, at += 12)
        {
            var reference = BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
            var size = reference & 0x7FFFFFFF;
            var duration = BinaryPrimitives.ReadUInt32BigEndian(s[(at + 4)..]);
            fragments.Add(new Fragment(offset, Rescale(time, sidxScale, timescale)));
            offset += size;
            time += duration;
        }
        total = Rescale(time, sidxScale, timescale);
        return fragments;
    }

    private static long Rescale(long ticks, uint from, uint to) =>
        from == to || from == 0 ? ticks : (long)((double)ticks * to / from);

    /// <summary>Every track fragment's frames, in order. Exposed for tests.</summary>
    public static Frame[] ParseMoof(ReadOnlySpan<byte> moof, long moofStart, long moofEnd, long start,
        uint trexDuration, uint trexSize)
    {
        var frames = new List<Frame>();
        var time = start;
        var at = 0;
        while (at + 8 <= moof.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(moof[at..]);
            if (size < 8 || at + size > moof.Length) break;
            if (moof.Slice(at + 4, 4).SequenceEqual("traf"u8))
            {
                time = ParseTraf(moof.Slice(at + 8, size - 8), moofStart, moofEnd, time, trexDuration, trexSize, frames);
            }
            at += size;
        }
        return [.. frames];
    }

    private static long ParseTraf(ReadOnlySpan<byte> traf, long moofStart, long moofEnd, long time,
        uint defaultDuration, uint defaultSize, List<Frame> frames)
    {
        var baseOffset = moofStart;
        long? decodeTime = null;
        var at = 0;
        // tfhd and tfdt come before trun; read them first.
        while (at + 8 <= traf.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(traf[at..]);
            if (size < 8 || at + size > traf.Length) break;
            var type = traf.Slice(at + 4, 4);
            var body = traf.Slice(at + 8, size - 8);
            if (type.SequenceEqual("tfhd"u8))
            {
                var flags = BinaryPrimitives.ReadUInt32BigEndian(body) & 0xFFFFFF;
                var p = 8; // version/flags + track id
                if ((flags & 0x1) != 0) { baseOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(body[p..]); p += 8; }
                if ((flags & 0x2) != 0) p += 4;
                if ((flags & 0x8) != 0) { defaultDuration = BinaryPrimitives.ReadUInt32BigEndian(body[p..]); p += 4; }
                if ((flags & 0x10) != 0) { defaultSize = BinaryPrimitives.ReadUInt32BigEndian(body[p..]); p += 4; }
            }
            else if (type.SequenceEqual("tfdt"u8))
            {
                decodeTime = body[0] == 1
                    ? (long)BinaryPrimitives.ReadUInt64BigEndian(body[4..])
                    : BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
            }
            at += size;
        }
        if (decodeTime is { } decoded) time = decoded;

        // Without a data offset the first run starts right after this moof's mdat header.
        var dataPos = moofEnd + 8;
        at = 0;
        while (at + 8 <= traf.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(traf[at..]);
            if (size < 8 || at + size > traf.Length) break;
            if (traf.Slice(at + 4, 4).SequenceEqual("trun"u8))
            {
                var body = traf.Slice(at + 8, size - 8);
                var flags = BinaryPrimitives.ReadUInt32BigEndian(body) & 0xFFFFFF;
                var count = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                var p = 8;
                if ((flags & 0x1) != 0) { dataPos = baseOffset + BinaryPrimitives.ReadInt32BigEndian(body[p..]); p += 4; }
                if ((flags & 0x4) != 0) p += 4;
                for (var i = 0; i < count; i++)
                {
                    var duration = defaultDuration;
                    var sampleSize = defaultSize;
                    if ((flags & 0x100) != 0) { duration = BinaryPrimitives.ReadUInt32BigEndian(body[p..]); p += 4; }
                    if ((flags & 0x200) != 0) { sampleSize = BinaryPrimitives.ReadUInt32BigEndian(body[p..]); p += 4; }
                    if ((flags & 0x400) != 0) p += 4;
                    if ((flags & 0x800) != 0) p += 4;
                    frames.Add(new Frame(dataPos, (int)sampleSize, time, duration));
                    dataPos += sampleSize;
                    time += duration;
                }
            }
            at += size;
        }
        return time;
    }

    /// <summary>The AAC setup from the sample entry's esds (AudioSpecificConfig).</summary>
    private readonly record struct AudioConfig(int SampleRate, int Channels, int Profile, int SamplingIndex)
    {
        private static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

        public static AudioConfig FromStsd(ReadOnlySpan<byte> stsd)
        {
            // stsd: version/flags(4) count(4), then the mp4a entry box.
            var entry = stsd[8..];
            var entrySize = (int)BinaryPrimitives.ReadUInt32BigEndian(entry);
            entry = entry[..Math.Min(entrySize, entry.Length)];
            if (!entry.Slice(4, 4).SequenceEqual("mp4a"u8)) throw new InvalidDataException("not an mp4a track");
            // mp4a: header(8) reserved(6) dataRef(2) version..(8) channels(2) bits(2) predef(2) reserved(2) rate(4)
            var channels = BinaryPrimitives.ReadUInt16BigEndian(entry[24..]);
            var rate = (int)(BinaryPrimitives.ReadUInt32BigEndian(entry[32..]) >> 16);
            var esdsAt = entry.IndexOf("esds"u8);
            if (esdsAt < 0) return Fallback(rate, channels);
            var esds = entry[(esdsAt + 8)..];
            // Find the DecoderSpecificInfo descriptor (tag 5) and read the AudioSpecificConfig.
            for (var i = 0; i < esds.Length - 2; i++)
            {
                if (esds[i] != 0x05) continue;
                var j = i + 1;
                var len = 0;
                for (var k = 0; k < 4 && j < esds.Length; k++)
                {
                    var b = esds[j++];
                    len = (len << 7) | (b & 0x7F);
                    if ((b & 0x80) == 0) break;
                }
                if (len < 2 || j + len > esds.Length) continue;
                var asc = esds.Slice(j, len);
                var aot = asc[0] >> 3;
                var sfi = ((asc[0] & 0x07) << 1) | (asc[1] >> 7);
                var chan = (asc[1] >> 3) & 0x0F;
                if (aot == 31 || sfi == 15) return Fallback(rate, channels);
                // HE-AAC (5, 29) is LC with SBR on top: ADTS carries the LC core.
                var profile = aot is 5 or 29 ? 1 : Math.Clamp(aot - 1, 0, 3);
                return new AudioConfig(Rates[Math.Min(sfi, Rates.Length - 1)], chan == 0 ? channels : chan, profile, sfi);
            }
            return Fallback(rate, channels);
        }

        private static AudioConfig Fallback(int rate, int channels)
        {
            var index = Array.IndexOf(Rates, rate);
            return new AudioConfig(rate, channels, 1, index < 0 ? 4 : index);
        }
    }
}
