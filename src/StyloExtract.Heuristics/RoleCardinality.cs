using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

/// <summary>
/// Maps each <see cref="BlockRole"/> to its expected per-page cardinality:
/// singleton (matches exactly one element) or repeated (matches a set of K
/// elements that all share the role on a given page).
///
/// Used by the inducer dispatch in <see cref="ExtractorInducer"/> to pick
/// between <see cref="IdentityClaimSelectorBuilder.BuildAncestorChain"/>
/// (singleton) and
/// <see cref="IdentityClaimSelectorBuilder.BuildForRepeatedRole"/> (repeated).
///
/// <para><b>Safety bias.</b> When uncertain about a role's expected cardinality,
/// the default is singleton. Over-restrictive cardinality is a soft fail (the
/// emitted selector still matches its target; sibling targets just need a
/// separate rule). Under-restrictive cardinality is a hard wrong selector that
/// matches outside the role-target set and pollutes the apply path.</para>
/// </summary>
internal static class RoleCardinality
{
    /// <summary>
    /// Returns true if the role expects exactly one matching element per page
    /// (e.g., <see cref="BlockRole.MainContent"/>, <see cref="BlockRole.Title"/>).
    /// Returns false for roles that can have N matches
    /// (<see cref="BlockRole.RepeatedItem"/>, repeated articles, list items).
    /// </summary>
    public static bool IsSingleton(BlockRole role) => role switch
    {
        // Page-level singletons — exactly one per document by definition.
        BlockRole.MainContent => true,
        BlockRole.Title => true,
        BlockRole.Header => true,
        BlockRole.Footer => true,
        BlockRole.PrimaryNavigation => true,
        BlockRole.Breadcrumb => true,
        BlockRole.Summary => true,
        BlockRole.CookieBanner => true,

        // Repeated-by-construction. RepeatedItem is the explicit case the
        // detector emits when it finds a homogeneous repeated-block container
        // (forum posts, listing cards, product tiles).
        BlockRole.RepeatedItem => false,

        // The classifier emits Article when a page hosts multiple <article>
        // siblings inside a non-article container (blog index style). Treating
        // Article as repeated lets a single selector pick up all of them.
        BlockRole.Article => false,

        // Conservative default: singleton. A wrongly-singleton role just emits
        // a more restrictive selector; a wrongly-repeated role emits one that
        // over-matches and breaks apply.
        _ => true,
    };
}
