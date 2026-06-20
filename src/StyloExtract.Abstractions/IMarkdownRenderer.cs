namespace StyloExtract.Abstractions;

public interface IMarkdownRenderer
{
    string Render(IReadOnlyList<ExtractedBlock> blocks, ExtractionProfile profile);
}
