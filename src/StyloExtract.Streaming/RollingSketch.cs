using System.IO.Hashing;

namespace StyloExtract.Streaming;

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

    public void Push(ulong tagHash, ulong classHash)
    {
        _window[_writeIdx] = new EventSlot(tagHash, classHash);
        _writeIdx = (_writeIdx + 1) % _window.Length;
        _count++;
    }

    public void Recompute()
    {
        _signature.Fill(uint.MaxValue);
        var populated = Math.Min(_count, _window.Length);
        if (populated == 0) return;

        Span<byte> buf = stackalloc byte[16];
        var seeds = s_seeds.AsSpan();
        for (int i = 0; i < populated; i++)
        {
            var slot = _window[i];
            var shingle = ShingleHash(slot.TagHash, slot.ClassHash);
            BitConverter.TryWriteBytes(buf, shingle);
            for (int s = 0; s < SignatureSize; s++)
            {
                BitConverter.TryWriteBytes(buf[8..], seeds[s]);
                var h = (uint)(XxHash64.HashToUInt64(buf) & 0xFFFFFFFFUL);
                if (h < _signature[s]) _signature[s] = h;
            }
        }
    }

    private static ulong ShingleHash(ulong tagHash, ulong classHash)
    {
        Span<byte> buf = stackalloc byte[16];
        BitConverter.TryWriteBytes(buf, tagHash);
        BitConverter.TryWriteBytes(buf[8..], classHash);
        return XxHash3.HashToUInt64(buf);
    }
}
