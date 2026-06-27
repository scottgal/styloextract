using System.Text.Json.Serialization;
using StyloExtract.Abstractions;

namespace StyloExtract.Streaming;

/// <summary>
/// Source-generated JSON context for the streaming template tree. Task 13
/// replaced the <see cref="IdentityClaim"/> tripwire shape with
/// <see cref="BytePattern"/> shapes; the byte-array shape serialises cleanly
/// out of the box (System.Text.Json defaults to base64 for <c>byte[]</c>).
/// </summary>
[JsonSerializable(typeof(StreamingTemplate))]
[JsonSerializable(typeof(BytePattern))]
[JsonSerializable(typeof(AttrConstraint))]
internal sealed partial class StreamingJsonContext : JsonSerializerContext;
