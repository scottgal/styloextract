using System.IO.Hashing;

namespace StyloExtract.Fingerprint;

public sealed class LshBander
{
    private readonly int _bands;
    private readonly int _rowsPerBand;

    public LshBander(int bands = 16, int rowsPerBand = 8)
    {
        _bands = bands;
        _rowsPerBand = rowsPerBand;
    }

    public ulong[] BandHashes(uint[] signature)
    {
        if (signature.Length != _bands * _rowsPerBand)
            throw new ArgumentException($"Signature size {signature.Length} != bands*rows {_bands * _rowsPerBand}.");
        var result = new ulong[_bands];
        var buf = new byte[_rowsPerBand * 4];
        for (int b = 0; b < _bands; b++)
        {
            for (int r = 0; r < _rowsPerBand; r++)
            {
                BitConverter.GetBytes(signature[b * _rowsPerBand + r]).CopyTo(buf, r * 4);
            }
            result[b] = XxHash64.HashToUInt64(buf);
        }
        return result;
    }
}
