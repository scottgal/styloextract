using System.IO.Hashing;
using System.Text;
using StyloExtract.Abstractions;

namespace StyloExtract.Streaming.Tests;

/// <summary>
/// Common builders for the Task 4 tripwire-shape tests. Centralises the
/// boilerplate of hashing a tag name into an <see cref="IdentityClaim"/>
/// so the tests read as state-machine behaviour, not byte-hashing trivia.
/// </summary>
internal static class TripwireTestHelpers
{
    /// <summary>Tag-name only identity claim — matches any element with the given tag.</summary>
    public static IdentityClaim TagClaim(string tag)
    {
        Span<byte> buf = stackalloc byte[64];
        var len = Encoding.UTF8.GetBytes(tag.AsSpan(), buf);
        return new IdentityClaim
        {
            Tag = tag,
            TagHash = XxHash3.HashToUInt64(buf[..len]),
        };
    }

    /// <summary>Tag + classes identity claim — matches an element whose class
    /// list contains every named class (order-insensitive).</summary>
    public static IdentityClaim TagWithClassesClaim(string tag, params string[] classes)
    {
        Span<byte> buf = stackalloc byte[64];
        var len = Encoding.UTF8.GetBytes(tag.AsSpan(), buf);
        var classHashes = new ulong[classes.Length];
        for (int i = 0; i < classes.Length; i++)
        {
            var cn = Encoding.UTF8.GetBytes(classes[i].AsSpan(), buf);
            classHashes[i] = XxHash3.HashToUInt64(buf[..cn]);
        }
        return new IdentityClaim
        {
            Tag = tag,
            TagHash = XxHash3.HashToUInt64(buf[..len]),
            Classes = classes,
            ClassHashes = classHashes,
        };
    }

    /// <summary>Hash a tag name for synthetic <see cref="TagEvent"/> construction.</summary>
    public static ulong TagHash(string tag)
    {
        Span<byte> buf = stackalloc byte[64];
        var len = Encoding.UTF8.GetBytes(tag.AsSpan(), buf);
        return XxHash3.HashToUInt64(buf[..len]);
    }

    /// <summary>Default-shaped streaming template — useful where the test cares
    /// about the FSM but not about which tripwires are chosen.</summary>
    public static StreamingTemplate MakeTemplate(
        IdentityClaim prefix,
        IdentityClaim contentStart,
        IdentityClaim contentEnd,
        int bailoutBytes = 1_000_000,
        int maxCaptureBytes = 1_000_000) => new()
    {
        TemplateId = Guid.NewGuid(),
        Host = "",
        PrefixTripwire = prefix,
        ContentStartTripwire = contentStart,
        ContentEndTripwire = contentEnd,
        BailoutBytes = bailoutBytes,
        MaxCaptureBytes = maxCaptureBytes,
    };
}
