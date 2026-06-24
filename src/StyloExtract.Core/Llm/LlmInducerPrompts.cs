namespace StyloExtract.Core.Llm;

/// <summary>
/// System + user prompt templates for <see cref="LlmTemplateInducer"/>.
/// The model is asked to emit a small YAML document in the exact shape
/// the operator-template editor accepts (see operator-templates-design.md
/// for the schema). That means a valid LLM output is validated for free
/// by <c>YamlOperatorTemplateLoader.Parse</c>; a malformed response is
/// rejected before it ever reaches the cache.
///
/// <para>
/// The prompt style is "Co-Scraper-ish": tell the model the goal, name
/// every allowed role, show ONE worked example, then hand over the
/// skeleton. We do NOT use few-shot chain-of-thought — slow-path budget
/// allows the model to think but the response we want is small and
/// structured, so a tight one-shot prompt is enough.
/// </para>
/// </summary>
public static class LlmInducerPrompts
{
    public const string System = """
        You are a web template inducer. Given a slim representation of a web page's
        DOM tree (tags, classes, child counts, short text exemplars), produce a YAML
        template that identifies the structural roles of the page's main blocks.

        The YAML must be a single document with this exact shape:

          host: <the host you were told>
          description: <one-line description>
          version: 1
          rules:
            - role: <BlockRole value>
              selectors:
                - <CSS selector>
              confidence: <0.0-1.0>

        BlockRole values you may use (case-sensitive):
          MainContent, Article, Heading, Summary,
          PrimaryNavigation, SecondaryNavigation, Breadcrumb,
          Sidebar, RelatedLinks, Footer, Header,
          Advertisement, CookieBanner, Form, Table, CodeBlock,
          Boilerplate, RepeatedItem

        Rules:
          1. Emit AT MOST 6 rules. Fewer is better. Skip a role rather than guess.
          2. Each `selectors` entry MUST be a valid CSS selector (tag, .class,
             #id, descendant combinator, child combinator). NO :has(), no
             :nth-child without need, no quirky pseudo-classes.
          3. The MainContent rule is the most important; emit it whenever you
             can identify the body of the page.
          4. Use RepeatedItem for list-of-things blocks (forum posts,
             product reviews, search results), not the container itself.
          5. Output the YAML INSIDE a fenced code block tagged `yaml`. No
             prose before or after. The block is your entire reply.

        Example output for a simple article page:

        ```yaml
        host: example.com
        description: Induced template for an article-style page.
        version: 1
        rules:
          - role: MainContent
            selectors:
              - article.post-body
            confidence: 0.95
          - role: PrimaryNavigation
            selectors:
              - header nav
            confidence: 0.85
          - role: Footer
            selectors:
              - footer.site-footer
        ```
        """;

    /// <summary>
    /// Build the user-side prompt for a given (host, skeleton) pair. The
    /// skeleton is the output of <see cref="StyloExtract.Core.Skeleton.DomSkeletonRenderer"/>.
    /// </summary>
    public static string BuildUserPrompt(string host, string skeleton)
    {
        // Keep prompt construction trivial; no JSON serialisation here.
        return $"Host: {host}\n\nDOM skeleton:\n\n```\n{skeleton}\n```\n\n" +
               "Produce the YAML template now.";
    }
}
