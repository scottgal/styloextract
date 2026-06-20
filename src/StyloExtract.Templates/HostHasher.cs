using System.Security.Cryptography;
using System.Text;

namespace StyloExtract.Templates;

public sealed class HostHasher
{
    private readonly byte[] _key;

    public HostHasher(byte[] key)
    {
        if (key.Length < 16) throw new ArgumentException("Key must be ≥16 bytes.", nameof(key));
        _key = (byte[])key.Clone();
    }

    public static HostHasher FromConfiguredKeyOrRandom(string? base64Key)
    {
        if (!string.IsNullOrEmpty(base64Key))
        {
            return new HostHasher(Convert.FromBase64String(base64Key));
        }
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return new HostHasher(key);
    }

    public byte[] Hash(string host)
    {
        var normalized = host.ToLowerInvariant();
        using var hmac = new HMACSHA256(_key);
        var full = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        var truncated = new byte[16];
        Array.Copy(full, truncated, 16);
        return truncated;
    }
}
