using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// Pins <see cref="ApplicatorBrokenCheck.IsBroken"/>. Three failure modes:
/// empty / chrome-heavy / signal-loss carry over from the original local
/// function; the noisy-content gate is the Move 2 addition.
/// </summary>
public class ApplicatorBrokenCheckTests
{
    private static ExtractedBlock Block(
        BlockRole role,
        string text,
        double linkDensity = 0.05,
        IReadOnlyList<ExtractedLink>? links = null)
    {
        return new ExtractedBlock
        {
            Id = Guid.NewGuid().ToString("N"),
            Role = role,
            Confidence = 0.9,
            Text = text,
            Markdown = text,
            XPath = "/html/body/div",
            CssSelector = null,
            TextLength = text.Length,
            LinkDensity = linkDensity,
            Links = links ?? Array.Empty<ExtractedLink>(),
        };
    }

    private static IReadOnlyList<ExtractedLink> ImageAnchors(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ExtractedLink { Text = "", Href = $"/lang/{i}", IsExternal = false })
            .ToList();

    private static IReadOnlyList<ExtractedLink> RealLinks(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ExtractedLink
            {
                Text = $"This is a substantial link text with real words number {i}",
                Href = $"/article/{i}",
                IsExternal = false,
            })
            .ToList();

    private static ApplicatorResult Result(int applied, int missed, params ExtractedBlock[] blocks)
        => new(blocks, applied, missed);

    private static string LongProse(int chars) =>
        string.Concat(Enumerable.Repeat("This is a sentence of substantial prose body content. ", chars / 54 + 1))
            .Substring(0, chars);

    // ---------- Original gates carry-over (regression coverage) ----------

    [Fact]
    public void Empty_Output_Is_Broken()
    {
        var result = Result(applied: 2, missed: 0, Block(BlockRole.MainContent, "tiny"));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeTrue("combined text below MinViableExtractText");
    }

    [Fact]
    public void Chrome_Heavy_Output_With_Low_Content_Is_Broken()
    {
        var result = Result(
            applied: 3,
            missed: 0,
            Block(BlockRole.Header, LongProse(800)),
            Block(BlockRole.Footer, LongProse(400)),
            Block(BlockRole.MainContent, "tiny content"));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeTrue("chrome-heavy total + sub-100 content");
    }

    [Fact]
    public void High_Miss_Ratio_Is_Broken()
    {
        var result = Result(
            applied: 1,
            missed: 4,
            Block(BlockRole.MainContent, LongProse(800)));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeTrue("4/5 rules missed");
    }

    [Fact]
    public void Healthy_Output_Is_Not_Broken()
    {
        var result = Result(
            applied: 3,
            missed: 0,
            Block(BlockRole.MainContent, LongProse(1500), linkDensity: 0.08));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeFalse();
    }

    // ---------- Move 2: noisy-content gate ----------

    [Fact]
    public void Noisy_MainContent_Is_Broken()
    {
        // Article-shaped from the totals: 800 chars, plenty above 200 / 100 /
        // ratio gates. But link density 0.62 says the bytes are mostly link
        // text — language picker / nav-list leaked into MainContent.
        var result = Result(
            applied: 3,
            missed: 0,
            Block(BlockRole.MainContent, LongProse(800), linkDensity: 0.62));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeTrue(
            "MainContent over the noisy-content threshold must trip the gate");
    }

    [Fact]
    public void Noisy_Article_Block_Is_Broken()
    {
        // Same rule but for the Article role, which the renderer treats as
        // primary content equally with MainContent.
        var result = Result(
            applied: 3,
            missed: 0,
            Block(BlockRole.Article, LongProse(800), linkDensity: 0.55));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeTrue(
            "Article role is also gated by the noisy-content threshold");
    }

    [Fact]
    public void Short_MainContent_Does_Not_Trip_NoisyContent_Gate()
    {
        // Short content blocks naturally spike link density (one 'Read more'
        // link inside a 100-char block hits 0.4 link density legitimately).
        // The noisy-content gate has to defer to the empty/chrome gates below
        // the NoisyContentMinTextLength threshold.
        var result = Result(
            applied: 3,
            missed: 0,
            Block(BlockRole.MainContent, LongProse(300), linkDensity: 0.65));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeFalse(
            "short MainContent legitimately has high link density; gate must defer");
    }

    [Fact]
    public void Noisy_RepeatedItem_Does_Not_Trip_NoisyContent_Gate()
    {
        // HN front page, Reddit listings, forum threads: RepeatedItem blocks
        // are intentionally link-shaped. The noisy-content gate must not fire
        // on them or every navigation/listing page would refit-spam the LLM.
        var result = Result(
            applied: 3,
            missed: 0,
            Block(BlockRole.RepeatedItem, LongProse(800), linkDensity: 0.85),
            Block(BlockRole.RepeatedItem, LongProse(800), linkDensity: 0.85));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeFalse(
            "RepeatedItem is link-shaped by design; noisy-content gate is silent here");
    }

    [Fact]
    public void Noisy_SecondaryNavigation_Does_Not_Trip_NoisyContent_Gate()
    {
        // Footer link bundles routinely run 0.9 link density. They're not
        // content; the gate is scoped to MainContent + Article so they don't
        // trip it.
        var result = Result(
            applied: 3,
            missed: 0,
            Block(BlockRole.MainContent, LongProse(1500), linkDensity: 0.08),
            Block(BlockRole.SecondaryNavigation, LongProse(600), linkDensity: 0.92));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeFalse(
            "navigation roles outside MainContent / Article are silent");
    }

    [Fact]
    public void Mixed_Blocks_Trip_If_Any_MainContent_Is_Noisy()
    {
        // A page may emit multiple MainContent blocks (rare but possible). Any
        // single one over the threshold counts as broken since the renderer
        // concatenates them into the visible output.
        var result = Result(
            applied: 3,
            missed: 0,
            Block(BlockRole.MainContent, LongProse(1500), linkDensity: 0.08),
            Block(BlockRole.MainContent, LongProse(600), linkDensity: 0.7));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeTrue(
            "one noisy MainContent block is enough to trip the gate");
    }

    // ---------- Move 2b: image-picker (empty-text-anchor) gate ----------

    [Fact]
    public void Image_Picker_Anchors_Trip_The_Image_Picker_Gate()
    {
        // Models the mostlylucid language-flag picker leak shape: <a><img/></a>
        // anchors with empty link text. The link-density metric is zero
        // because there's no link TEXT, but the anchors are still chrome
        // disguised as content.
        var result = Result(
            applied: 3,
            missed: 0,
            Block(
                BlockRole.MainContent,
                LongProse(1500),
                linkDensity: 0.02,
                links: ImageAnchors(20)));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeTrue(
            "20 empty-text anchors inside MainContent is an image-picker leak");
    }

    [Fact]
    public void Few_Image_Anchors_Do_Not_Trip_The_Image_Picker_Gate()
    {
        // Article with a couple of badges (NuGet, GitHub stars) at the top —
        // these legitimately render as image-only anchors. Below the
        // threshold the gate stays silent.
        var result = Result(
            applied: 3,
            missed: 0,
            Block(
                BlockRole.MainContent,
                LongProse(1500),
                linkDensity: 0.02,
                links: ImageAnchors(4)));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeFalse(
            "a small number of badge-shaped anchors is normal article shape");
    }

    [Fact]
    public void Long_Text_Anchors_Do_Not_Trip_The_Image_Picker_Gate()
    {
        // An article with many embedded prose links (a wiki article with
        // 30 inline references). These have real link text, so the
        // image-picker gate must stay silent — the link-density gate
        // would handle this case if the link-text-ratio actually crossed
        // the threshold, but real prose with many inline links stays well
        // below 0.5.
        var result = Result(
            applied: 3,
            missed: 0,
            Block(
                BlockRole.MainContent,
                LongProse(3000),
                linkDensity: 0.18,
                links: RealLinks(30)));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeFalse(
            "30 substantial-text inline links in a long article are normal prose, not a picker");
    }

    [Fact]
    public void Image_Picker_Gate_Respects_MainContent_Scope()
    {
        // RepeatedItem-heavy pages: link cards each have an image + a short
        // headline. Each card's anchor text might be short, but the cards
        // are intentional content. The image-picker gate is scoped to
        // MainContent + Article so RepeatedItem blocks pass through.
        var result = Result(
            applied: 3,
            missed: 0,
            Block(BlockRole.RepeatedItem, LongProse(2000), linkDensity: 0.3, links: ImageAnchors(30)),
            Block(BlockRole.RepeatedItem, LongProse(2000), linkDensity: 0.3, links: ImageAnchors(30)));
        ApplicatorBrokenCheck.IsBroken(result).Should().BeFalse(
            "RepeatedItem blocks are out of scope for the image-picker gate");
    }
}