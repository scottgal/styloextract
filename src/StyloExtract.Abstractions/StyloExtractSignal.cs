namespace StyloExtract.Abstractions;

/// <summary>
/// Rich payload carried on StyloExtract signal emissions. All fields are
/// optional — populate what's available for the signal type. Designed for
/// extension as future extractor types are added.
/// </summary>
public readonly record struct StyloExtractSignal(
    Guid? TemplateId = null,
    int? TemplateVersion = null,
    string? FingerprintHex = null,
    double? Similarity = null,
    double? DriftDelta = null,
    int? OldVersion = null,
    int? NewVersion = null,
    int? ObservationCount = null,
    string? HostDisplayName = null,
    Guid? CandidateId = null,
    bool? Won = null,
    int? MatchedElementCount = null);
