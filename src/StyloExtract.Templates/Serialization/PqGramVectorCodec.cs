using System.Text;

namespace StyloExtract.Templates.Serialization;

public static class PqGramVectorCodec
{
    // Sanity cap on key length: no legitimate pq-gram key exceeds 1 KB.
    private const int MaxReasonableKeyLength = 1024;

    public static byte[] Encode(IReadOnlyDictionary<string, double> counts)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((uint)counts.Count);
        foreach (var kv in counts)
        {
            var keyBytes = Encoding.UTF8.GetBytes(kv.Key);
            bw.Write((uint)keyBytes.Length);
            bw.Write(keyBytes);
            bw.Write(kv.Value);
        }
        return ms.ToArray();
    }

    public static IReadOnlyDictionary<string, double> Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);
        var count = br.ReadUInt32();
        var result = new Dictionary<string, double>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var keyLen = br.ReadUInt32();
            // Sanity checks prevent OOM on truncated or malformed input.
            if (keyLen > MaxReasonableKeyLength)
                throw new InvalidDataException(
                    $"PqGramVectorCodec.Decode: key length {keyLen} at entry {i} exceeds maximum {MaxReasonableKeyLength}.");
            var remainingBytes = (long)ms.Length - ms.Position;
            // Each entry needs at least keyLen bytes for the key plus 8 bytes for the double value.
            if (keyLen > remainingBytes - 8)
                throw new InvalidDataException(
                    $"PqGramVectorCodec.Decode: declared key length {keyLen} at entry {i} exceeds remaining stream bytes ({remainingBytes}).");
            var key = Encoding.UTF8.GetString(br.ReadBytes((int)keyLen));
            var value = br.ReadDouble();
            result[key] = value;
        }
        return result;
    }
}
