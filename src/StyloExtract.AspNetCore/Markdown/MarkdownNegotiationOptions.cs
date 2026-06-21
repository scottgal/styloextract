using StyloExtract.Abstractions;

namespace StyloExtract.AspNetCore.Markdown;

public sealed class MarkdownNegotiationOptions
{
    public ExtractionProfile DefaultProfile { get; set; } = ExtractionProfile.RagFull;
    public int MaxBodyBytes { get; set; } = 4 * 1024 * 1024;
    public HashSet<int> StatusCodes { get; init; } = [200];
    public string ProfileHeaderName { get; set; } = "X-Stylo-Profile";
    public string ProfileQueryName { get; set; } = "stylo_profile";
    public bool EmitVaryHeader { get; set; } = true;
}
