using System.IO.Hashing;
using System.Text.Json.Serialization;
using StyloExtract.Fingerprint;

namespace StyloExtract.Streaming;

/// <summary>
/// One fence in a streaming template — a MinHash signature + LSH bands that
/// the scanner matches against a sliding window of incoming tag events.
///
/// alpha.21 removed the per-fence <c>TagAllowlistBloom</c> field; tag
/// filtering now happens at the scanner level via the static
/// <see cref="StructuralTagAllowlist"/>. The <see cref="TagAllowlistBloom"/>
/// property is preserved as a JSON-deserialiser sink so persisted templates
/// from alpha.16–alpha.20 round-trip cleanly (the value is read and dropped).
/// </summary>
public readonly record struct TemplateFence(
    uint[] MinHash,
    ulong[] LshBands,
    int RequiredDepth)
{
    /// <summary>
    /// Back-compat shim. alpha.16–alpha.20 serialised a per-fence Bloom
    /// filter here; alpha.21 removed it. JSON deserialisation still binds
    /// this property (so old persisted templates load) but the value is
    /// always read as 0 and ignored at scan time.
    /// </summary>
    [JsonInclude]
    public ulong TagAllowlistBloom
    {
        get => 0UL;
        init { /* swallow legacy value */ }
    }

    public static TemplateFence BuildFromEvents(
        ReadOnlySpan<(ulong tagHash, ulong classHash)> events,
        int requiredDepth)
    {
        // alpha.21 Markov shingles: pair each event with the prior event's tag
        // hash. The first event uses 0 as prevTag (matches scanner behaviour
        // where _prevTagHash starts at 0). Result is events.Length shingles.
        var shingles = new ulong[events.Length];
        ulong prevTag = 0UL;
        for (int i = 0; i < events.Length; i++)
        {
            var (t, c) = events[i];
            shingles[i] = ShingleHash(prevTag, t, c);
            prevTag = t;
        }

        var minhash = new MinHashSketcher(128).Sketch(shingles);
        var bands = new LshBander(16, 8).BandHashes(minhash);

        return new TemplateFence(minhash, bands, requiredDepth);
    }

    private static ulong ShingleHash(ulong prevTagHash, ulong tagHash, ulong classHash)
    {
        Span<byte> buf = stackalloc byte[24];
        BitConverter.TryWriteBytes(buf, prevTagHash);
        BitConverter.TryWriteBytes(buf[8..], tagHash);
        BitConverter.TryWriteBytes(buf[16..], classHash);
        return XxHash3.HashToUInt64(buf);
    }
}
