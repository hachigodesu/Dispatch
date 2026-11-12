namespace Dispatch.Core.Games;

/// <summary>
/// Cuts a byte stream into pieces whose boundaries depend on the content (a rolling "gear" hash), not on offsets. Two
/// files that contain the same run of data are cut the same way inside it even when the run sits at different offsets,
/// so identical pieces can be found by their hash. SCS rebuilds every archive for each game version, but most of the
/// data inside (textures, models, sounds) stays the same: between 1.60 and 1.62, about 90% of it.
/// </summary>
public sealed class ContentChunker
{
    public delegate void ChunkHandler(ReadOnlySpan<byte> chunk);

    private static readonly ulong[] Gear = MakeGear();

    private readonly int _min;
    private readonly int _max;
    private readonly ulong _mask;
    private readonly byte[] _buffer;

    /// <param name="averageSize">Typical piece size (a power of two); pieces are between a quarter and eight times this.</param>
    public ContentChunker(int averageSize = 16 * 1024)
    {
        if (averageSize < 64 || (averageSize & (averageSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(averageSize), "Must be a power of two of at least 64.");
        }

        _mask = (ulong)averageSize - 1;
        _min = averageSize / 4;
        _max = averageSize * 8;
        _buffer = new byte[Math.Max(_max * 4, 4 << 20)];
    }

    public int MaxChunk => _max;

    /// <summary>Reads the stream to its end and hands over each piece in order. The span is only valid during the call.</summary>
    public void Split(Stream input, ChunkHandler onChunk, CancellationToken cancel = default)
    {
        int filled = 0;
        bool eof = false;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            while (!eof && filled < _buffer.Length)
            {
                int read = input.Read(_buffer, filled, _buffer.Length - filled);
                if (read == 0)
                {
                    eof = true;
                }

                filled += read;
            }

            int start = 0;
            while (filled - start > 0 && (eof || filled - start >= _max))
            {
                int cut = Cut(_buffer.AsSpan(start, Math.Min(filled - start, _max)));
                onChunk(_buffer.AsSpan(start, cut));
                start += cut;
            }

            Buffer.BlockCopy(_buffer, start, _buffer, 0, filled - start);
            filled -= start;
            if (eof && filled == 0)
            {
                return;
            }
        }
    }

    private int Cut(ReadOnlySpan<byte> s)
    {
        if (s.Length <= _min)
        {
            return s.Length;
        }

        ulong h = 0;
        for (int i = _min; i < s.Length; i++)
        {
            h = (h << 1) + Gear[s[i]];
            if ((h & _mask) == 0)
            {
                return i + 1;
            }
        }

        return s.Length;
    }

    /// <summary>A fixed table of 256 random-looking numbers (SplitMix64), the same in every run and .NET version.</summary>
    private static ulong[] MakeGear()
    {
        var table = new ulong[256];
        ulong x = 0x9E3779B97F4A7C15;
        for (int i = 0; i < table.Length; i++)
        {
            ulong z = x += 0x9E3779B97F4A7C15;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            table[i] = z ^ (z >> 31);
        }

        return table;
    }
}
