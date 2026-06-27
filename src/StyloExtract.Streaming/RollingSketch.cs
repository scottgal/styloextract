using System.IO.Hashing;

namespace StyloExtract.Streaming;

/// <summary>
/// Min-pooled MinHash sketch over a sliding window of recent tag events.
///
/// alpha.21: shingles upgraded to Markov bigrams of consecutive tag transitions
/// — each shingle is <c>(prevTagHash, currentTagHash, currentClassHash)</c>.
/// Order-sensitive: <c>[A, B]</c> and <c>[B, A]</c> now produce different
/// signatures. Push state retains the previous tag hash so recompute can
/// rebuild shingles from the window alone (storing each event's prev-tag
/// would double window size; instead recompute walks the window left-to-right
/// using slot[i-1].TagHash as the prev for slot[i]).
/// </summary>
public ref struct RollingSketch
{
    public const int SignatureSize = 128;

    private static readonly ulong[] s_seeds = BuildSeeds();

    private static ulong[] BuildSeeds()
    {
        var seeds = new ulong[SignatureSize];
        for (int i = 0; i < SignatureSize; i++)
            seeds[i] = 0x9E3779B97F4A7C15UL * (ulong)(i + 1);
        return seeds;
    }

    private readonly Span<uint> _signature;
    private readonly Span<EventSlot> _window;
    private int _count;
    private int _writeIdx;

    public RollingSketch(Span<uint> signature, Span<EventSlot> window)
    {
        if (signature.Length != SignatureSize)
            throw new ArgumentException($"signature must be {SignatureSize} slots", nameof(signature));
        if (window.Length == 0)
            throw new ArgumentException("window must be non-empty", nameof(window));
        _signature = signature;
        _window = window;
        _count = 0;
        _writeIdx = 0;
        _signature.Fill(uint.MaxValue);
    }

    public readonly ReadOnlySpan<uint> Signature => _signature;

    public readonly bool Matches(in TemplateFence fence)
    {
        Span<ulong> bands = stackalloc ulong[16];
        ComputeBands(_signature, bands);
        var fenceBands = fence.LshBands;
        var n = Math.Min(bands.Length, fenceBands.Length);
        for (int i = 0; i < n; i++)
            if (bands[i] == fenceBands[i]) return true;
        return false;
    }

    private static void ComputeBands(ReadOnlySpan<uint> signature, Span<ulong> bands)
    {
        const int rowsPerBand = 8;
        Span<byte> buf = stackalloc byte[rowsPerBand * 4];
        for (int b = 0; b < bands.Length; b++)
        {
            for (int r = 0; r < rowsPerBand; r++)
                BitConverter.TryWriteBytes(buf.Slice(r * 4, 4), signature[b * rowsPerBand + r]);
            bands[b] = XxHash64.HashToUInt64(buf);
        }
    }

    /// <summary>
    /// Push the next event into the sliding window. The Markov shingle uses
    /// <paramref name="prevTagHash"/> as the leading bigram element, so
    /// callers must pass the tag hash from the *immediately preceding*
    /// accepted event (or 0 for the first event in the stream).
    /// </summary>
    public void Push(ulong prevTagHash, ulong tagHash, ulong classHash)
    {
        // Store (prevTagHash, currentTagHash, classHash) in the slot so
        // Recompute can reproduce the same shingles deterministically.
        _window[_writeIdx] = new EventSlot(tagHash, classHash, prevTagHash);
        _writeIdx = (_writeIdx + 1) % _window.Length;
        _count++;
    }

    public void Recompute()
    {
        _signature.Fill(uint.MaxValue);
        var populated = Math.Min(_count, _window.Length);
        if (populated == 0) return;

        Span<byte> buf = stackalloc byte[32];
        var seeds = s_seeds.AsSpan();

        // Walk the window in insertion order. When the window has fewer
        // entries than its capacity, slots[0..populated] is the insertion
        // order. Once full, the ring wraps so insertion order starts at
        // _writeIdx and ends at (_writeIdx - 1) mod len.
        // alpha.21: the FIRST shingle in the window uses prevTag=0, not
        // the slot's stored prevTag. This is so a sliding-window scanner
        // matches a fence built from a contiguous event sequence regardless
        // of what came before the window. (Without this, the leftmost
        // shingle would depend on the event JUST BEFORE the window — which
        // the fence builder didn't see — and the LSH bands wouldn't align.)
        var len = _window.Length;
        var start = _count <= len ? 0 : _writeIdx;
        for (int i = 0; i < populated; i++)
        {
            var slot = _window[(start + i) % len];
            var prevTag = i == 0 ? 0UL : slot.PrevTagHash;
            var shingle = ShingleHash(prevTag, slot.TagHash, slot.ClassHash);
            BitConverter.TryWriteBytes(buf, shingle);
            for (int s = 0; s < SignatureSize; s++)
            {
                BitConverter.TryWriteBytes(buf[8..], seeds[s]);
                var h = (uint)(XxHash64.HashToUInt64(buf[..16]) & 0xFFFFFFFFUL);
                if (h < _signature[s]) _signature[s] = h;
            }
        }
    }

    internal static ulong ShingleHash(ulong prevTagHash, ulong tagHash, ulong classHash)
    {
        Span<byte> buf = stackalloc byte[24];
        BitConverter.TryWriteBytes(buf, prevTagHash);
        BitConverter.TryWriteBytes(buf[8..], tagHash);
        BitConverter.TryWriteBytes(buf[16..], classHash);
        return XxHash3.HashToUInt64(buf);
    }
}
