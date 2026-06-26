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
          MainContent, Article, Title, Heading, Summary,
          PrimaryNavigation, SecondaryNavigation, Breadcrumb,
          Sidebar, RelatedLinks, Footer, Header,
          Advertisement, CookieBanner, Form, Table, CodeBlock,
          Boilerplate, RepeatedItem

        Title is the page-level <h1> the rest of the page is "about" (one per page);
        Heading is an intra-content H2/H3/H4 inside the body.

        Rules:
          1. Emit AT MOST 6 rules. Fewer is better. Skip a role rather than guess.
          2. Each `selectors` entry MUST be a valid CSS selector (tag, .class,
             #id, descendant combinator, child combinator). NO :has(), no
             :nth-child without need, no quirky pseudo-classes.
          3. The MainContent rule is the most important; emit it whenever you
             can identify the body of the page. Pick the NARROWEST container
             that wraps the article body / blog post list / product description.
             Prefer an id-bearing inner container (e.g. `#content`, `#main`,
             `#post-list`) over a broad outer wrapper (e.g. `.container`,
             `body > div`) when both are available — the inner container
             excludes auxiliary chrome that surrounds the content.
          4. Use RepeatedItem for list-of-things blocks (forum posts,
             product reviews, search results), not the container itself.
          5. Output the YAML INSIDE a fenced code block tagged `yaml`. No
             prose before or after. The block is your entire reply.
          6. Use RepeatedItem for list-of-things blocks (forum posts, product
             reviews, search results, blog-post cards), not the container itself.
             Do NOT use RepeatedItem for header/footer navigation lists — those
             are PrimaryNavigation / SecondaryNavigation at the <ul>/<nav> level,
             even when they contain many <li> children.

        IMPORTANT — auxiliary UI is NOT MainContent. The following patterns
        commonly appear next to the real content and MUST NOT be classified as
        MainContent (or Article, Summary, Heading, or RepeatedItem):

          * Language pickers / locale switchers — a list of language or country
            names (often with flag icons), e.g. text like "العربية (Arabic)
            Deutsch (German) English Español…". Often a high-linkDensity (≥0.5)
            list of short anchors.
          * Filter / faceted-search panels — UI with labels "Date:", "Lang:",
            "Cat:", "Sort:", "Page size:" plus inputs / select dropdowns /
            chip buttons. The panel that drives a listing is NOT the listing.
          * Pagination strips — "1 2 3 … Next Page 1 of 80 (Total items: 791)".
          * Cookie / GDPR consent banners — "We use cookies / Accept all".
          * Newsletter signup forms — "Subscribe to our newsletter".
          * Social-share / author-action strips ("Share on X / Copy link").
          * Author bio / "About the author" cards near the top of a blog.
          * Top-of-page announcement bars, toast / notification containers.

        These should be one of: PrimaryNavigation, SecondaryNavigation,
        Breadcrumb, Form, CookieBanner, Boilerplate — or simply OMITTED. Not
        every visible region needs a rule. Quality over coverage: a template
        with only `MainContent` + `Footer` is better than one that
        misclassifies the language picker as MainContent.

        Sanity check before answering: re-read the text excerpt of every
        selector you chose as MainContent. If it reads like a list of language
        names, a filter UI, "Page 1 of N", a cookie notice, or a bio blurb —
        it's wrong. Pick a different (usually deeper) container.

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

        Example output for a blog homepage with header nav and footer:

        ```yaml
        host: example-blog.com
        description: Blog homepage with header navigation, post list, and footer.
        version: 1
        rules:
          - role: PrimaryNavigation
            selectors:
              - body > header > nav
              - body > header > ul
            confidence: 0.9
          - role: Breadcrumb
            selectors:
              - nav[aria-label='breadcrumb']
            confidence: 0.95
          - role: MainContent
            selectors:
              - main
              - article
            confidence: 0.92
          - role: RepeatedItem
            selectors:
              - main > ul > li
              - main article.post-card
            confidence: 0.85
          - role: SecondaryNavigation
            selectors:
              - body > footer > nav
              - body > footer > ul
            confidence: 0.85
        ```
        """;

    /// <summary>
    /// Build the user-side prompt for a given (host, skeleton) pair. The
    /// skeleton is the output of <see cref="StyloExtract.Core.Skeleton.DomSkeletonRenderer"/>.
    /// <paramref name="availableSelectors"/> is an optional closed list of
    /// real CSS selectors from the document (one per line) the model is
    /// instructed to pick from; pass empty when the catalog isn't available.
    /// </summary>
    public static string BuildUserPrompt(string host, string skeleton, string availableSelectors = "")
    {
        var selectorSection = string.IsNullOrWhiteSpace(availableSelectors)
            ? ""
            : $"\n\nAvailable CSS selectors on this page (USE ONLY THESE — inventing other selectors will fail):\n\n```\n{availableSelectors}\n```";
        return $"Host: {host}\n\nDOM skeleton:\n\n```\n{skeleton}\n```{selectorSection}\n\nProduce the YAML template now.";
    }

    /// <summary>
    /// System prompt for repair: identical schema rules to <see cref="System"/>,
    /// but the model is told it is FIXING a template that produced bad output,
    /// not inducing from scratch. The shape of the response is the same.
    /// </summary>
    public const string SystemRepair = """
        You are a web template diagnostician. You are given:

          1. A slim representation of a web page's DOM tree (tags, classes, child
             counts, short text exemplars).
          2. An EXISTING template that failed to extract the page's main content —
             its selectors did not match the actual content block.
          3. (Optionally) the empty/wrong Markdown output produced by the failing
             template, so you can see what went wrong.
          4. (Optionally) a closed list of CSS selectors that actually exist on
             the page — pick from these only.

        Your job has two parts. First, internally ask yourself: WHY is this
        failing? What did the old selector target, and why didn't that match
        the article body / item list / product description on THIS page? Then,
        HOW should it work for this page? Which container in the skeleton
        actually holds the main content?

        Use that diagnosis to write a CORRECTED YAML template — same shape as
        the existing one — with selectors that actually match the page's
        content. Preserve roles that were correct; replace selectors that
        were wrong.

        The YAML must be a single document with this exact shape:

          host: <the host you were told>
          description: <one-line description>
          version: <bump the previous version by 1>
          rules:
            - role: <BlockRole value>
              selectors:
                - <CSS selector>
              confidence: <0.0-1.0>

        BlockRole values you may use (case-sensitive):
          MainContent, Article, Title, Heading, Summary,
          PrimaryNavigation, SecondaryNavigation, Breadcrumb,
          Sidebar, RelatedLinks, Footer, Header,
          Advertisement, CookieBanner, Form, Table, CodeBlock,
          Boilerplate, RepeatedItem

        Title is the page-level <h1> the rest of the page is "about" (one per page);
        Heading is an intra-content H2/H3/H4 inside the body.

        Rules:
          1. Emit AT MOST 6 rules. Fewer is better. Skip a role rather than guess.
          2. Each `selectors` entry MUST be a valid CSS selector (tag, .class,
             #id, descendant combinator, child combinator). NO :has(), no
             :nth-child without need, no quirky pseudo-classes.
          3. The MainContent rule is the highest priority — fixing it is the
             whole point of the repair. Look at the skeleton and pick the
             NARROWEST container that actually holds the article body / item
             list / product description. Prefer an id-bearing inner container
             (e.g. `#content`, `#main`, `#post-list`) over a broad outer
             wrapper (e.g. `.container`, `body > div`) when both exist — the
             inner container excludes surrounding chrome (author bio, filter
             panel, language picker, pagination, ads).
          4. Use RepeatedItem for list-of-things blocks (forum posts, product
             reviews, search results), not the container itself.
          5. Output the YAML INSIDE a fenced code block tagged `yaml`. No
             prose before or after. The block is your entire reply.
          6. Use RepeatedItem for list-of-things blocks (forum posts, product
             reviews, search results, blog-post cards), not the container itself.
             Do NOT use RepeatedItem for header/footer navigation lists — those
             are PrimaryNavigation / SecondaryNavigation at the <ul>/<nav> level,
             even when they contain many <li> children.

        IMPORTANT — auxiliary UI is NOT MainContent. The previous template
        likely failed because its MainContent selector matched a wrapper that
        ALSO captured one of these patterns. Do NOT classify any of these as
        MainContent (or Article, Summary, Heading, or RepeatedItem):

          * Language pickers / locale switchers — lists of language or country
            names (often with flag icons), e.g. "العربية (Arabic) Deutsch
            (German) English Español…".
          * Filter / faceted-search panels — "Date:", "Lang:", "Cat:", "Sort:",
            "Page size:" labels paired with inputs / dropdowns / chip buttons.
          * Pagination strips — "1 2 3 … Next Page 1 of N".
          * Cookie / GDPR consent banners.
          * Newsletter signup forms; social-share / share-this strips.
          * Author bio / "About the author" cards near the top of a blog.

        These belong to: PrimaryNavigation, SecondaryNavigation, Breadcrumb,
        Form, CookieBanner, Boilerplate — or are simply OMITTED. Not every
        visible region needs a rule. A template with only `MainContent` +
        `Footer` is better than one that misclassifies a language picker.

        Sanity check before answering: re-read the text excerpt of every
        selector you chose as MainContent. If it reads like a list of language
        names, a filter UI, "Page 1 of N", or a bio blurb — it's wrong. Pick
        a different (usually deeper) container.

        Example output for a blog homepage with header nav and footer:

        ```yaml
        host: example-blog.com
        description: Blog homepage with header navigation, post list, and footer.
        version: 1
        rules:
          - role: PrimaryNavigation
            selectors:
              - body > header > nav
              - body > header > ul
            confidence: 0.9
          - role: Breadcrumb
            selectors:
              - nav[aria-label='breadcrumb']
            confidence: 0.95
          - role: MainContent
            selectors:
              - main
              - article
            confidence: 0.92
          - role: RepeatedItem
            selectors:
              - main > ul > li
              - main article.post-card
            confidence: 0.85
          - role: SecondaryNavigation
            selectors:
              - body > footer > nav
              - body > footer > ul
            confidence: 0.85
        ```
        """;

    /// <summary>
    /// Build the user-side prompt for a repair operation. <paramref name="existingTemplateYaml"/>
    /// is the broken template that produced low-quality output;
    /// <paramref name="badMarkdownSample"/> is what the template produced (truncated
    /// to a few hundred chars; pass empty if not available).
    /// </summary>
    public static string BuildRepairPrompt(string host, string skeleton, string existingTemplateYaml, string badMarkdownSample, string availableSelectors = "")
    {
        var sampleSection = string.IsNullOrWhiteSpace(badMarkdownSample)
            ? ""
            : $"\n\nThe failing template produced this Markdown (which is too short / wrong content):\n\n```\n{badMarkdownSample}\n```";
        var selectorSection = string.IsNullOrWhiteSpace(availableSelectors)
            ? ""
            : $"\n\nAvailable CSS selectors on this page (USE ONLY THESE — inventing other selectors will fail):\n\n```\n{availableSelectors}\n```";

        return $"Host: {host}\n\nDOM skeleton:\n\n```\n{skeleton}\n```\n\nExisting (failing) template:\n\n```yaml\n{existingTemplateYaml}\n```{sampleSection}{selectorSection}\n\nProduce the repaired YAML template now.";
    }
}
