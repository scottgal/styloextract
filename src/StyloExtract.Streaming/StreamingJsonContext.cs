using System.Text.Json.Serialization;

namespace StyloExtract.Streaming;

[JsonSerializable(typeof(StreamingTemplate))]
[JsonSerializable(typeof(TemplateFence))]
[JsonSerializable(typeof(uint[]))]
[JsonSerializable(typeof(ulong[]))]
internal sealed partial class StreamingJsonContext : JsonSerializerContext;
