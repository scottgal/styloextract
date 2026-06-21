using System.Security.Cryptography;
using System.Text;
using Mostlylucid.Ephemeral;

namespace StyloExtract.Templates;

public sealed class HostHasher : IAsyncDisposable
{
    private readonly byte[] _key;
    // Cached HMAC instance — reusable across calls, serialized by the SqliteSingleWriter
    // coordinator so no concurrent access occurs on the hot path.
    private readonly HMACSHA256 _hmac;
    // Bounded LRU cache: lowercase-host → 16-byte HMAC-SHA256 truncated hash.
    // Eliminates hash computation entirely on repeat hosts. Size 64 is ample for
    // per-process host diversity; EphemeralLruCache evicts by TTL + size cap.
    private readonly EphemeralLruCache<string, byte[]> _cache;

    public HostHasher(byte[] key)
    {
        if (key.Length < 16) throw new ArgumentException("Key must be ≥16 bytes.", nameof(key));
        _key = (byte[])key.Clone();
        _hmac = new HMACSHA256(_key);
        _cache = new EphemeralLruCache<string, byte[]>(new EphemeralLruCacheOptions
        {
            MaxSize = 64,
            DefaultTtl = TimeSpan.FromHours(1),
            HotKeyExtension = TimeSpan.FromHours(6),
            SampleRate = 10
        });
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
        return _cache.GetOrAdd(normalized, ComputeHash);
    }

    private byte[] ComputeHash(string normalized)
    {
        // _hmac is an instance field and not thread-safe by itself, but HostHasher
        // is only called from the LayoutExtractor pipeline which serializes through
        // SqliteSingleWriter, so concurrent calls are not expected on the hot path.
        lock (_hmac)
        {
            var full = _hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            var truncated = new byte[16];
            Array.Copy(full, truncated, 16);
            return truncated;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _hmac.Dispose();
        await _cache.DisposeAsync().ConfigureAwait(false);
    }
}
