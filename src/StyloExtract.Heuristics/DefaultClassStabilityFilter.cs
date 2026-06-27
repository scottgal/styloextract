using System.Text.RegularExpressions;

namespace StyloExtract.Heuristics;

/// <summary>
/// Default stability filter for class-token candidates considered as identity
/// anchors by <see cref="IdentityClaimSelectorBuilder"/>.
///
/// <para><b>What the filter rejects.</b></para>
/// <list type="bullet">
///   <item><b>Framework hashes.</b> CSS-modules (<c>_abc12__def34</c>),
///     Emotion (<c>css-1a2b3c4</c>), styled-components (<c>sc-abc123</c>),
///     and generic no-vowel/high-digit hash shapes.</item>
///   <item><b>Tailwind responsive variants.</b> <c>sm:</c>, <c>md:</c>,
///     <c>lg:</c>, <c>xl:</c>, <c>2xl:</c> — these vary with viewport and
///     describe presentation, not identity (e.g. <c>sm:gap-2</c>).</item>
///   <item><b>Tailwind theme variants.</b> <c>dark:</c>, <c>light:</c>.</item>
///   <item><b>Tailwind state variants.</b> <c>hover:</c>, <c>focus:</c>,
///     <c>active:</c>, <c>disabled:</c>, <c>group-*:</c>, <c>peer-*:</c>,
///     etc. — interaction-state utilities, never anchors.</item>
///   <item><b>Tailwind/Bootstrap utility prefixes.</b> Size/spacing/colour
///     utilities like <c>p-4</c>, <c>m-2</c>, <c>gap-x-8</c>, <c>text-xl</c>,
///     <c>bg-gray-100</c>, <c>border-2</c>, <c>rounded-md</c>, <c>w-full</c>,
///     <c>h-screen</c>, <c>z-10</c>, <c>opacity-75</c>. The prefix is followed
///     by a number, fraction, colour name, or known utility value.</item>
///   <item><b>Atomic layout singletons.</b> Exact-match generic tokens like
///     <c>flex</c>, <c>grid</c>, <c>block</c>, <c>hidden</c>, <c>absolute</c>,
///     <c>relative</c>, <c>sticky</c>, etc. — single-purpose presentation.</item>
/// </list>
///
/// <para><b>Heuristic boundary.</b> The filter trades false negatives
/// (rejecting a stable semantic token that happens to look Tailwind-shaped —
/// e.g. a hypothetical <c>p-content</c> would slip through because the prefix
/// isn't followed by a number, but <c>w-2</c> would be wrongly rejected even
/// if it were a real custom class) for false positives (accepting a Tailwind
/// class). Erring toward false-negative is safer: a rejected stable token
/// just forces the inducer to walk one more ancestor, while a false positive
/// pollutes the anchor and breaks apply-time matching.</para>
///
/// <para><b>One layer of defence, not two.</b> The uniqueness probe at
/// <see cref="IdentityClaimSelectorBuilder.BuildAncestorChain"/> is the other
/// layer — even if a junk token escapes here, the probe will reject any chain
/// that isn't unique on the induction document.</para>
/// </summary>
public sealed partial class DefaultClassStabilityFilter : IClassStabilityFilter
{
    // Tailwind responsive + theme variant prefix: sm:/md:/lg:/xl:/2xl:/dark:/light:
    [GeneratedRegex(@"^(sm|md|lg|xl|2xl|dark|light):", RegexOptions.CultureInvariant)]
    private static partial Regex TailwindVariantPrefix();

    // Tailwind state variants — interaction/structural pseudo-class wrappers.
    // group/peer take an optional sub-state too (group-hover:, peer-checked:).
    [GeneratedRegex(@"^(hover|focus|focus-within|focus-visible|active|disabled|first|last|odd|even|group|peer|placeholder|target|enabled|checked|required|valid|invalid|in-range|out-of-range|read-only|read-write|empty|root|file|marker|selection|backdrop|motion-safe|motion-reduce|print|portrait|landscape|rtl|ltr)(-[a-z][a-z-]*)?:", RegexOptions.CultureInvariant)]
    private static partial Regex TailwindStateVariantPrefix();

    // Tailwind utility prefixes followed by a number / fraction / known token.
    // The trailing value is: digit-led (e.g. p-4, m-1.5, w-2/3, z-10),
    //                       common keywords (full, auto, screen, min, max, none, px),
    //                       or a colour name + optional shade (gray-100, red-500),
    //                       or a known sub-utility token (cols-N, rows-N, span-N,
    //                       flex, grid, block, hidden, etc. — Tailwind uses these
    //                       as values for grid-*, flex-*, items-*, justify-*, ...).
    [GeneratedRegex(
        @"^(p|m|w|h|gap|space|text|bg|border|rounded|grid|flex|col|row|min|max|inset|top|right|bottom|left|z|opacity|shadow|ring|fill|stroke|cursor|select|resize|object|overflow|whitespace|break|content|origin|transition|duration|delay|ease|animate|transform|translate|rotate|scale|skew|filter|blur|brightness|contrast|saturate|p[xytrbl]|m[xytrbl]|aspect|order|font|leading|tracking|list|placeholder|divide|outline|truncate|sr|not-sr|antialiased|subpixel|italic|not-italic|underline|line-through|no-underline|uppercase|lowercase|capitalize|normal-case|items|justify|self|place|basis|grow|shrink|object)-(\d|x-\d|y-\d|t-\d|r-\d|b-\d|l-\d|full|auto|screen|min|max|none|px|fit|prose|reverse|wrap|nowrap|first|last|center|left|right|start|end|justify|between|around|evenly|baseline|stretch|hidden|visible|scroll|clip|solid|dashed|dotted|double|inherit|current|transparent|black|white|gray|red|orange|yellow|green|teal|blue|indigo|purple|pink|slate|zinc|neutral|stone|amber|lime|emerald|cyan|sky|violet|fuchsia|rose|primary|secondary|cols-\d|rows-\d|span-\d|flow-\w+|flex|grid|block|inline|cover|contain|fill|none|bold|semibold|medium|light|normal|extrabold|thin)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TailwindUtilityPrefix();

    // Bootstrap atomic utilities — same shape as Tailwind but with d-* (display)
    // and 0-12-style numeric spacing scale. Catches col-md-6, p-0, mt-3, d-flex,
    // text-center, etc.
    [GeneratedRegex(@"^(p|m|d|w|h|text|bg|border|rounded|col|row|mt|mb|ml|mr|mx|my|pt|pb|pl|pr|px|py)-(\d|sm|md|lg|xl|xxl|auto|none|fluid|flex|grid|block|inline|inline-block|inline-flex|table|none|center|left|right|start|end|justify|primary|secondary|success|warning|danger|info|light|dark|muted|white|black|body|wrap|nowrap|truncate|uppercase|lowercase|capitalize)", RegexOptions.CultureInvariant)]
    private static partial Regex BootstrapAtomicPrefix();

    // Exact-match atomic layout singletons. Generic display/position tokens that
    // describe presentation, never identity. Stored as a hash set for O(1) lookup.
    private static readonly HashSet<string> AtomicLayoutSingletons = new(StringComparer.Ordinal)
    {
        // Display utilities
        "flex", "grid", "block", "inline", "inline-block", "inline-flex",
        "inline-grid", "hidden", "visible", "invisible", "collapse",
        "table", "table-row", "table-cell", "table-caption", "table-column",
        "table-column-group", "table-footer-group", "table-header-group",
        "table-row-group", "contents", "flow-root", "list-item",
        // Position utilities
        "fixed", "absolute", "relative", "sticky", "static",
    };

    /// <summary>
    /// Returns true if <paramref name="token"/> is a candidate anchor (semantic
    /// identifier). Returns false for hash-shaped tokens, Tailwind/Bootstrap
    /// utility classes, and atomic layout singletons.
    /// </summary>
    public bool IsStable(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (token.Length < 3) return true; // too short to be a useful hash

        // Common framework-hash patterns.
        if (token.StartsWith("css-", StringComparison.Ordinal) && token.Length >= 8) return false;  // Emotion
        if (token.StartsWith("sc-", StringComparison.Ordinal) && token.Length >= 7) return false;   // styled-components
        if (token.StartsWith("_", StringComparison.Ordinal) && token.Contains("__")) return false;  // CSS modules

        // Hash-shaped tokens. Gate at length 6 so 7-char Tailwind utility hashes
        // like "tx7k9q2" are rejected; IsLikelyHash still gates the digit-ratio
        // branch at length 8.
        if (token.Length >= 6 && IsLikelyHash(token)) return false;

        // Tailwind responsive / theme / state variants — always non-anchor.
        if (TailwindVariantPrefix().IsMatch(token)) return false;
        if (TailwindStateVariantPrefix().IsMatch(token)) return false;

        // Tailwind / Bootstrap atomic utilities (prefix-shape).
        if (TailwindUtilityPrefix().IsMatch(token)) return false;
        if (BootstrapAtomicPrefix().IsMatch(token)) return false;

        // Exact atomic layout singletons.
        if (AtomicLayoutSingletons.Contains(token)) return false;

        return true;
    }

    private static bool IsLikelyHash(string s)
    {
        var digits = 0;
        var hasVowel = false;
        foreach (var c in s)
        {
            if (char.IsDigit(c)) digits++;
            else if (c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y') hasVowel = true;
        }
        // No vowel + length >= 6 → likely random.
        if (!hasVowel && s.Length >= 6) return true;
        // Digit ratio > 0.3 + length >= 8 → likely encoded.
        if ((double)digits / s.Length > 0.3) return true;
        return false;
    }
}
