using System.IO.Hashing;

namespace StyloExtract.Fingerprint;

public sealed class MinHashSketcher
{
    private readonly int _signatureSize;
    private readonly ulong[] _seeds;

    public MinHashSketcher(int signatureSize = 128)
    {
        _signatureSize = signatureSize;
        _seeds = new ulong[signatureSize];
        for (int i = 0; i < signatureSize; i++)
        {
            _seeds[i] = 0x9E3779B97F4A7C15UL * (ulong)(i + 1);
        }
    }

    public uint[] Sketch(IReadOnlyList<ulong> shingles)
    {
        var sig = new uint[_signatureSize];
        Array.Fill(sig, uint.MaxValue);
        if (shingles.Count == 0) return sig;
        Span<byte> buf = stackalloc byte[16];
        foreach (var shingle in shingles)
        {
            BitConverter.TryWriteBytes(buf, shingle);
            for (int i = 0; i < _signatureSize; i++)
            {
                BitConverter.TryWriteBytes(buf[8..], _seeds[i]);
                var h = (uint)(XxHash64.HashToUInt64(buf) & 0xFFFFFFFFUL);
                if (h < sig[i]) sig[i] = h;
            }
        }
        return sig;
    }
}
