using FluentAssertions;
using StyloExtract.Core.Llm;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// Prompt-text snapshot coverage for the alpha.14 LLM nav-classification
/// few-shot extension. The induction prompt previously carried only a simple
/// article-page example; alpha.14 appends a SECOND worked example showing
/// a blog homepage with header nav, breadcrumb, MainContent, RepeatedItem
/// post cards, and footer nav — mirroring the shapes the alpha.13
/// NavPreDetector heuristic correctly classifies.
///
/// <para>
/// These tests are deterministic; no LLM run required. They guard against
/// accidental future deletion of the blog-homepage example block or the
/// Rule 6 clarifier that disambiguates RepeatedItem from header/footer nav.
/// </para>
/// </summary>
public class LlmInducerPromptsTests
{
    [Fact]
    public void SystemPrompt_ContainsBlogHomepageExample()
    {
        // The second worked example MUST land verbatim — the LLM sees the
        // shape of a richer page, not just a one-rule article example.
        LlmInducerPrompts.System.Should().Contain(
            "Example output for a blog homepage with header nav and footer:",
            "the alpha.14 second few-shot example must be present so the LLM " +
            "learns the correct shape for nav-bearing pages");

        LlmInducerPrompts.System.Should().Contain("host: example-blog.com");
        LlmInducerPrompts.System.Should().Contain("body > header > nav");
        LlmInducerPrompts.System.Should().Contain("body > footer > nav");
        LlmInducerPrompts.System.Should().Contain("nav[aria-label='breadcrumb']");
        LlmInducerPrompts.System.Should().Contain("main article.post-card");
    }

    [Fact]
    public void SystemPrompt_ClarifiesRepeatedItemVsNav()
    {
        // The alpha.14 Rule 6 clarifier closes a known LLM confusion mode:
        // emitting RepeatedItem at the <li> level for header/footer nav
        // instead of PrimaryNavigation / SecondaryNavigation at the
        // <ul>/<nav> level.
        LlmInducerPrompts.System.Should().Contain(
            "Do NOT use RepeatedItem for header/footer navigation lists",
            "Rule 6 must explicitly forbid RepeatedItem for header/footer nav " +
            "lists, since the heuristic NavPreDetector now classifies those " +
            "as PrimaryNavigation / SecondaryNavigation at the parent level");

        LlmInducerPrompts.System.Should().Contain(
            "PrimaryNavigation / SecondaryNavigation at the <ul>/<nav> level");
    }

    [Fact]
    public void SystemRepairPrompt_ContainsBlogHomepageExample()
    {
        // Repair benefits from the same shape guidance — when the LLM is
        // told to fix a broken template, the second example shows it the
        // correct shape for a nav-rich page.
        LlmInducerPrompts.SystemRepair.Should().Contain(
            "Example output for a blog homepage with header nav and footer:",
            "the alpha.14 second few-shot example must also appear in the " +
            "repair prompt so refit benefits from the same guidance");

        LlmInducerPrompts.SystemRepair.Should().Contain("host: example-blog.com");
        LlmInducerPrompts.SystemRepair.Should().Contain("body > header > nav");
        LlmInducerPrompts.SystemRepair.Should().Contain("body > footer > nav");
        LlmInducerPrompts.SystemRepair.Should().Contain("nav[aria-label='breadcrumb']");
        LlmInducerPrompts.SystemRepair.Should().Contain("main article.post-card");

        // And the same Rule 6 clarifier — refit must also avoid
        // RepeatedItem-at-li for nav lists.
        LlmInducerPrompts.SystemRepair.Should().Contain(
            "Do NOT use RepeatedItem for header/footer navigation lists");
    }
}
