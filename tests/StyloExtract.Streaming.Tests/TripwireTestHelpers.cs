using System.Text;
using StyloExtract.Abstractions;

namespace StyloExtract.Streaming.Tests;

/// <summary>
/// Common builders for the Task 13 byte-pattern-shape tests. Centralises the
/// boilerplate of constructing <see cref="BytePattern"/> values from string
/// tag/attr literals so tests read as state-machine behaviour, not byte
/// encoding trivia.
///
/// The class name retains the "Tripwire" prefix because several existing test
/// files reference it; the helpers themselves emit the new byte-pattern shape.
/// </summary>
internal static class TripwireTestHelpers
{
    /// <summary>Open-tag byte pattern matching any element with the given tag.</summary>
    public static BytePattern TagPattern(string tag) =>
        new()
        {
            TagName = Encoding.UTF8.GetBytes(tag),
            Attrs = Array.Empty<AttrConstraint>(),
            IsClose = false,
        };

    /// <summary>Close-tag byte pattern for the given tag name.</summary>
    public static BytePattern ClosePattern(string tag) =>
        new()
        {
            TagName = Encoding.UTF8.GetBytes(tag),
            Attrs = Array.Empty<AttrConstraint>(),
            IsClose = true,
        };

    /// <summary>Open-tag pattern requiring an exact (name, value) attribute pair.</summary>
    public static BytePattern TagWithAttr(string tag, string attrName, string attrValue) =>
        new()
        {
            TagName = Encoding.UTF8.GetBytes(tag),
            Attrs = new[]
            {
                new AttrConstraint
                {
                    Name = Encoding.UTF8.GetBytes(attrName),
                    Value = Encoding.UTF8.GetBytes(attrValue),
                },
            },
            IsClose = false,
        };

    /// <summary>Default-shaped streaming template for tests that care about
    /// the FSM but not pattern selection.</summary>
    public static StreamingTemplate MakeTemplate(
        BytePattern prefix,
        BytePattern contentStart,
        BytePattern contentEnd,
        int bailoutBytes = 1_000_000,
        int maxCaptureBytes = 1_000_000) => new()
    {
        TemplateId = Guid.NewGuid(),
        Host = "",
        PrefixPattern = prefix,
        ContentStartPattern = contentStart,
        ContentEndPattern = contentEnd,
        BailoutBytes = bailoutBytes,
        MaxCaptureBytes = maxCaptureBytes,
    };
}
