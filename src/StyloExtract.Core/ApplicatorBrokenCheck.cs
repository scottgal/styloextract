using StyloExtract.Abstractions;

namespace StyloExtract.Core;

/// <summary>
/// Apply-time quality gate. When a template's selectors match cleanly but the
/// emitted content is wrong (empty, chrome-only, or noise-saturated), the
/// applicator needs to know so it can trigger a refit and bypass the cached
/// template for THIS page.
///
/// <para>Lives outside <see cref="LayoutExtractor"/> so the gate is unit-
/// testable in isolation. The integration site stays in
/// <see cref="LayoutExtractor.ExtractAsync"/>; this class just holds the
/// rules and their thresholds.</para>
///
/// <para>Two classes of broken output covered today:</para>
/// <list type="bullet">
///   <item><b>Empty / chrome-heavy</b> (the original Move 0 checks):
///   essentially no text emitted, or lots of text but almost none of it in
///   content roles, or a high rule-miss ratio. Original three thresholds:
///   <c>MinViableExtractText=200</c>, <c>ChromeHeavyTotalThreshold=1000</c>,
///   <c>MinViableContentText=100</c>, <c>SignalLossMissRatio=0.7</c>,
///   <c>SignalLossMinRules=3</c>.</item>
///   <item><b>Noisy MainContent</b> (Move 2 of the apply-time-quality-gate
///   spec): MainContent / Article block exists with adequate text, but the
///   text is dominated by link content. Catches the Wikipedia / mostlylucid
///   leak shape where a language picker or route-variant strip ends up inside
///   the MainContent subtree. Threshold: link-text-ratio &gt;= 0.5 on at least
///   one MainContent or Article block.</item>
/// </list>
/// </summary>
internal static class ApplicatorBrokenCheck
{
    public const int MinViableExtractText = 200;
    public const int ChromeHeavyTotalThreshold = 1000;
    public const int MinViableContentText = 100;
    public const double SignalLossMissRatio = 0.7;
    public const int SignalLossMinRules = 3;

    /// <summary>
    /// Move 2 threshold. A MainContent / Article block whose
    /// <see cref="ExtractedBlock.LinkDensity"/> meets or exceeds this value is
    /// almost certainly nav-shaped chrome that leaked into content, not real
    /// article text. Real article bodies on the dogfood corpus sit between
    /// 0.02 and 0.25; nav-shaped content sits at 0.6+.
    /// </summary>
    public const double NoisyContentLinkDensityThreshold = 0.5;

    /// <summary>
    /// The minimum content-role block text length below which the noisy-content
    /// gate is silenced. Short text legitimately spikes link density (a single
    /// 40-char "Read more" link inside a 60-char block hits 0.66 density without
    /// being noise). Below this size we defer to the empty/chrome gates.
    /// </summary>
    public const int NoisyContentMinTextLength = 400;

    /// <summary>
    /// Move 2b threshold. Image-anchor pickers (language-flag pickers, share-
    /// icon rows, follow-us social buttons) ship <c>&lt;a&gt;&lt;img&gt;&lt;/a&gt;</c>
    /// elements with empty or near-empty link text — each anchor's
    /// <see cref="ExtractedLink.Text"/> is 0-3 characters. The link-density
    /// gate misses these because empty-text anchors contribute zero to the
    /// link-text ratio, but they're still chrome that leaked into content.
    /// A MainContent block with this many short-or-empty-text anchors is
    /// almost certainly a picker.
    ///
    /// 10 catches real-world pickers (mostlylucid's 12-language strip,
    /// typical 8-icon share rows) without false-positiving on articles that
    /// carry a few NuGet / GitHub / build badges in their intro paragraph.
    /// </summary>
    public const int ImagePickerAnchorCountThreshold = 10;

    /// <summary>
    /// Anchors whose <see cref="ExtractedLink.Text"/> is at or below this
    /// length count toward the image-picker tally. Images have no link text,
    /// flag tooltips often emit 1-3 char codes ("en", "de"), share buttons
    /// emit single-word labels ("Tweet", "Pin"). 8 chars covers all three.
    /// </summary>
    public const int ImagePickerAnchorTextMaxChars = 8;

    /// <summary>
    /// Returns true when the applicator's output indicates the cached template
    /// is broken on THIS page (refit + auto-repair should trigger).
    /// </summary>
    public static bool IsBroken(ApplicatorResult applied)
    {
        var combinedText = 0;
        var contentText = 0;
        foreach (var b in applied.Blocks)
        {
            combinedText += b.Text.Length;
            if (IsContentRole(b.Role))
                contentText += b.Text.Length;
        }

        if (combinedText < MinViableExtractText) return true;
        if (combinedText >= ChromeHeavyTotalThreshold && contentText < MinViableContentText)
            return true;

        var ruleCount = applied.RulesApplied + applied.RulesMissed;
        if (ruleCount >= SignalLossMinRules
            && (double)applied.RulesMissed / ruleCount >= SignalLossMissRatio)
            return true;

        // Move 2: noisy-content gate. Scoped to MainContent + Article only so
        // RepeatedItem-heavy pages (HN front, forum threads) and link-shaped
        // SecondaryNavigation footers don't trip the check. A page can ship
        // multiple MainContent blocks (rare); any one of them above the
        // threshold counts as broken since the renderer concatenates them all
        // into the visible output.
        //
        // Move 2b: image-picker gate (same scope). Counts anchors whose
        // ExtractedLink.Text is empty or very short — the signature shape of
        // language-flag pickers, social share rows, and "follow us" icon
        // strips. The vanilla link-density metric misses these because the
        // anchors carry zero link-text by construction; only the surrounding
        // image alt / aria-label gives them any meaning, and the renderer
        // doesn't emit alt as link text.
        foreach (var b in applied.Blocks)
        {
            if (b.Role is not (BlockRole.MainContent or BlockRole.Article)) continue;
            if (b.Text.Length < NoisyContentMinTextLength) continue;
            if (b.LinkDensity >= NoisyContentLinkDensityThreshold) return true;
            if (CountShortTextAnchors(b.Links) >= ImagePickerAnchorCountThreshold) return true;
        }

        return false;
    }

    private static int CountShortTextAnchors(IReadOnlyList<ExtractedLink> links)
    {
        int count = 0;
        for (int i = 0; i < links.Count; i++)
        {
            var text = links[i].Text ?? string.Empty;
            if (text.Length <= ImagePickerAnchorTextMaxChars)
                count++;
        }
        return count;
    }

    private static bool IsContentRole(BlockRole role) =>
        role is BlockRole.MainContent
            or BlockRole.Article
            or BlockRole.Title
            or BlockRole.Heading
            or BlockRole.Summary
            or BlockRole.Table
            or BlockRole.CodeBlock
            or BlockRole.RepeatedItem;
}