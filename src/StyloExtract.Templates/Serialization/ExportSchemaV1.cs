using StyloExtract.Abstractions;

namespace StyloExtract.Templates.Serialization;

public sealed record ExportSchemaV1
{
    public required int SchemaVersion { get; init; } = 1;
    public required DateTimeOffset ExportedAt { get; init; }
    public required ExportHost Host { get; init; }
    public required IReadOnlyList<ExportTemplate> Templates { get; init; }
}

public sealed record ExportHost
{
    public required string DisplayName { get; init; }
    public required string HashAlgorithm { get; init; }
    public required string? HashKey { get; init; }
}

public sealed record ExportTemplate
{
    public required Guid TemplateId { get; init; }
    public required int Version { get; init; }
    public required ExportFingerprints Fingerprints { get; init; }
    public required LearnedExtractor Extractor { get; init; }
    public required ExportObservationSummary Observations { get; init; }
}

public sealed record ExportFingerprints
{
    public required string SignatureMinhash { get; init; }    // base64
    public required string AnchorSignature { get; init; }     // base64
    public required ExportPqGramVector PqGramVector { get; init; }
}

public sealed record ExportPqGramVector
{
    public required int P { get; init; }
    public required int Q { get; init; }
    public required int TopK { get; init; }
    public required IReadOnlyDictionary<string, double> Values { get; init; }
    public required double Norm { get; init; }
}

public sealed record ExportObservationSummary
{
    public required int Count { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
}
