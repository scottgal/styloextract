namespace StyloExtract.Abstractions;

public enum BlockRole
{
    Unknown = 0,
    MainContent,
    Article,
    /// <summary>
    /// Intra-content heading (H2/H3/H4/H5/H6, or a non-primary H1). Headings live
    /// inside the body of a page and structure its sections. The page-level title
    /// — the single H1 that the rest of the page is "about" — is the distinct
    /// <see cref="Title"/> role.
    /// </summary>
    Heading,
    Summary,
    PrimaryNavigation,
    SecondaryNavigation,
    Breadcrumb,
    Sidebar,
    RelatedLinks,
    Footer,
    Header,
    Advertisement,
    CookieBanner,
    Form,
    Table,
    CodeBlock,
    Boilerplate,
    /// <summary>
    /// One item within a repeated-block container (forum posts, listing cards, product tiles,
    /// collection entries). Multiple RepeatedItem blocks are emitted in document order when
    /// the page is a multi-item layout. The role is treated as content by all renderers.
    /// </summary>
    RepeatedItem,
    /// <summary>
    /// The page-level / article-level title: the single <c>&lt;h1&gt;</c> the rest of the
    /// page is "about". Distinct from <see cref="Heading"/> (intra-content H2/H3/H4) so
    /// consumers like sitemap or outline builders can ask for the page's title without
    /// pulling its body. The heuristic picks the H1 inside (or closest to) the
    /// <c>&lt;main&gt;</c>/<c>&lt;article&gt;</c> element; with multiple H1s on the
    /// page, the earliest-in-document wins as the fallback.
    /// </summary>
    Title,
}
