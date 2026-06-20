using System.Text;

namespace StyloExtract.Templates.Serialization;

public static class PqGramVectorCodec
{
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
            var key = Encoding.UTF8.GetString(br.ReadBytes((int)keyLen));
            var value = br.ReadDouble();
            result[key] = value;
        }
        return result;
    }
}
