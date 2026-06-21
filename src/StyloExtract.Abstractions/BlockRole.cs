namespace StyloExtract.Abstractions;

public enum BlockRole
{
    Unknown = 0,
    MainContent,
    Article,
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
    RepeatedItem
}
