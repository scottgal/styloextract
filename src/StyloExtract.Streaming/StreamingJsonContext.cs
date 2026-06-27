using System.Text.Json.Serialization;
using StyloExtract.Abstractions;

namespace StyloExtract.Streaming;

[JsonSerializable(typeof(StreamingTemplate))]
[JsonSerializable(typeof(IdentityClaim))]
internal sealed partial class StreamingJsonContext : JsonSerializerContext;
