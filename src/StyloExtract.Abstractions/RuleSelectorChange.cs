namespace StyloExtract.Abstractions;

public sealed record RuleSelectorChange
{
    public required string RuleId { get; init; }
    public required BlockRole Role { get; init; }
    public required IReadOnlyList<string> OldSelectors { get; init; }
    public required IReadOnlyList<string> NewSelectors { get; init; }
}
