using System.IO.Hashing;
using StyloExtract.Fingerprint;

namespace StyloExtract.Streaming;

public readonly record struct TemplateFence(
    uint[] MinHash,
    ulong[] LshBands,
    ulong TagAllowlistBloom,
    int RequiredDepth)
{
    public static TemplateFence BuildFromEvents(
        ReadOnlySpan<(ulong tagHash, ulong classHash)> events,
        int requiredDepth)
    {
        var shingles = new ulong[events.Length];
        ulong bloom = 0;
        for (int i = 0; i < events.Length; i++)
        {
            var (t, c) = events[i];
            shingles[i] = ShingleHash(t, c);
            bloom |= 1UL << (int)(t & 63);
        }

        var minhash = new MinHashSketcher(128).Sketch(shingles);
        var bands = new LshBander(16, 8).BandHashes(minhash);

        return new TemplateFence(minhash, bands, bloom, requiredDepth);
    }

    private static ulong ShingleHash(ulong tagHash, ulong classHash)
    {
        Span<byte> buf = stackalloc byte[16];
        BitConverter.TryWriteBytes(buf, tagHash);
        BitConverter.TryWriteBytes(buf[8..], classHash);
        return XxHash3.HashToUInt64(buf);
    }
}
