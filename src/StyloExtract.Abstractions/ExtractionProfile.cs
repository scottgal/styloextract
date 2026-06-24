namespace StyloExtract.Abstractions;

public enum ExtractionProfile
{
    /// <summary>Main content roles only, rendered as GFM Markdown.</summary>
    MainContentOnly,

    /// <summary>Everything except primary chrome, rendered as GFM Markdown for LLM ingestion.</summary>
    RagFull,

    /// <summary>Navigation roles only — for crawler / sitemap-style consumers.</summary>
    AgentNavigation,

    /// <summary>All blocks with role+confidence comments — for debugging the classifier.</summary>
    DebugFull,

    /// <summary>
    /// MainContentOnly's role set, but emit each block's plain <c>Text</c> instead of
    /// its Markdown. The default output keeps GFM structure (sidebar TOCs, headings,
    /// blockquotes, lists, tables) that improves AI / human readability — but that
    /// structure shows up as "precision noise" against word-overlap benchmarks like
    /// WCXB whose ground truth is plain text. This profile gives a fair benchmark
    /// comparison without forcing the runtime output to lose its structure.
    /// </summary>
    Wcxb,
}
