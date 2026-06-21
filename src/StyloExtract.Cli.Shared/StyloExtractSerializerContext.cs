using System.Text.Json.Serialization;
using StyloExtract.Abstractions;

namespace StyloExtract.Cli.Shared;

/// <summary>
/// Source-generated JSON serializer context for all CLI serialization paths.
/// This avoids reflection-based JSON at runtime and is required for AOT compatibility.
/// </summary>
[JsonSerializable(typeof(ExtractionResult))]
[JsonSerializable(typeof(MonitorEnvelope<NewTemplateEvent>))]
[JsonSerializable(typeof(MonitorEnvelope<VersionChangeEvent>))]
public sealed partial class StyloExtractSerializerContext : JsonSerializerContext;

/// <summary>Typed envelope replacing the anonymous object previously emitted by MonitorEventSink.</summary>
public sealed record MonitorEnvelope<T>
{
    public required string Kind { get; init; }
    public required DateTimeOffset EmittedAt { get; init; }
    public required T Payload { get; init; }
}

/// <summary>Pretty-print variant of the context (WriteIndented = true).</summary>
[JsonSerializable(typeof(ExtractionResult))]
[JsonSerializable(typeof(MonitorEnvelope<NewTemplateEvent>))]
[JsonSerializable(typeof(MonitorEnvelope<VersionChangeEvent>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public sealed partial class StyloExtractSerializerContextPretty : JsonSerializerContext;
