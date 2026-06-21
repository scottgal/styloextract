using System.Text.Json.Serialization;
using StyloExtract.Abstractions;
using StyloExtract.Templates.Serialization;

namespace StyloExtract.Templates;

/// <summary>
/// Source-generated JSON context for AOT-compatible serialization of template types.
/// All serialization (SQLite extractor_blob, export JSON) uses camelCase.
/// BREAKING CHANGE (v1 pre-release): existing SQLite databases with PascalCase extractor_blob
/// blobs will fail to deserialize after this upgrade. The v1 product was never released, so
/// there are no users; a fresh DB will be created automatically.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(LearnedExtractor))]
[JsonSerializable(typeof(BlockRule))]
[JsonSerializable(typeof(BlockRole))]
[JsonSerializable(typeof(ExtractorCentroidState))]
[JsonSerializable(typeof(RoleCentroid))]
[JsonSerializable(typeof(ExportSchemaV1))]
[JsonSerializable(typeof(ExportHost))]
[JsonSerializable(typeof(ExportTemplate))]
[JsonSerializable(typeof(ExportFingerprints))]
[JsonSerializable(typeof(ExportPqGramVector))]
[JsonSerializable(typeof(ExportObservationSummary))]
internal sealed partial class TemplatesJsonContext : JsonSerializerContext;

/// <summary>
/// Indented variant for human-readable export output.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(LearnedExtractor))]
[JsonSerializable(typeof(BlockRule))]
[JsonSerializable(typeof(BlockRole))]
[JsonSerializable(typeof(ExtractorCentroidState))]
[JsonSerializable(typeof(RoleCentroid))]
[JsonSerializable(typeof(ExportSchemaV1))]
[JsonSerializable(typeof(ExportHost))]
[JsonSerializable(typeof(ExportTemplate))]
[JsonSerializable(typeof(ExportFingerprints))]
[JsonSerializable(typeof(ExportPqGramVector))]
[JsonSerializable(typeof(ExportObservationSummary))]
internal sealed partial class TemplatesJsonContextIndented : JsonSerializerContext;
