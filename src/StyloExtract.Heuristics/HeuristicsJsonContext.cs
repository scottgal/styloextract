using System.Text.Json.Serialization;

namespace StyloExtract.Heuristics;

// DTOs for JSON deserialization of embedded heuristic definition files.
// These are internal and file-scoped so the source-gen context can see them.

internal sealed class ClassNoiseDto
{
    public List<string> ExactTokens { get; set; } = new();
    public string[] Prefixes { get; set; } = [];
    public string HashedBemSuffixPattern { get; set; } = "";
}

internal sealed class PhraseList
{
    public List<string> Phrases { get; set; } = new();
}

internal sealed class PatternList
{
    public List<string> Patterns { get; set; } = new();
}

internal sealed class HintList
{
    public List<string> Hints { get; set; } = new();
}

internal sealed class RepeatedItemTagRules
{
    public List<string> SkipContainerTags { get; set; } = new();
    public List<string> SkipChildTags { get; set; } = new();
    public List<string> SkipAncestorTags { get; set; } = new();
    public List<string> SelfTypedTags { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClassNoiseDto))]
[JsonSerializable(typeof(PhraseList))]
[JsonSerializable(typeof(PatternList))]
[JsonSerializable(typeof(HintList))]
[JsonSerializable(typeof(RepeatedItemTagRules))]
internal sealed partial class HeuristicsJsonContext : JsonSerializerContext;
