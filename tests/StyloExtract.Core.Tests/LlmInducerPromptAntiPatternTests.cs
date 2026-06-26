using FluentAssertions;
using StyloExtract.Core.Llm;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// Snapshot-style coverage of the LLM induction prompts. The induction +
/// repair system prompts MUST carry explicit anti-pattern guidance enumerating
/// the chrome shapes (language pickers, faceted-search filter panels,
/// pagination, cookie banners) that downstream LLMs were observed
/// misclassifying as MainContent on server-rendered blog homepages
/// (mostlylucid.net, 1.8.0-alpha.10 regression).
///
/// <para>
/// These tests are deterministic and do not need an LLM running. They
/// guard against an accidental future revert of the anti-pattern block.
/// </para>
/// </summary>
public class LlmInducerPromptAntiPatternTests
{
    [Fact]
    public void System_Prompt_Names_Language_Picker_As_Forbidden_For_MainContent()
    {
        LlmInducerPrompts.System.Should().Contain("Language picker");
    }

    [Fact]
    public void System_Prompt_Names_Filter_Faceted_Search_Panel_As_Forbidden_For_MainContent()
    {
        // The mostlylucid.net regression: a filter strip with
        // "Date:" / "Lang:" / "Cat:" / "Sort:" labels.
        LlmInducerPrompts.System.Should().Contain("Filter");
        LlmInducerPrompts.System.Should().Contain("faceted-search");
    }

    [Fact]
    public void System_Prompt_Names_Pagination_As_Forbidden_For_MainContent()
    {
        LlmInducerPrompts.System.Should().Contain("Pagination");
    }

    [Fact]
    public void System_Prompt_Names_Cookie_Banner_As_Forbidden_For_MainContent()
    {
        LlmInducerPrompts.System.Should().Contain("Cookie");
    }

    [Fact]
    public void System_Prompt_Asks_The_Model_To_Re_Read_The_Excerpt_Before_Answering()
    {
        // The "sanity check" paragraph: re-read the MainContent excerpt
        // and self-correct if it reads like a language list, filter UI, etc.
        LlmInducerPrompts.System.Should().Contain("Sanity check");
    }

    [Fact]
    public void System_Prompt_Prefers_Narrowest_Container_For_MainContent()
    {
        LlmInducerPrompts.System.Should().Contain("NARROWEST");
    }

    [Fact]
    public void SystemRepair_Prompt_Carries_The_Same_AntiPattern_Guidance()
    {
        // Repair is the path lucidVIEW-FULL hits when an existing template
        // produces empty/short output. It must also know to avoid the
        // language-picker / filter-panel misclassification.
        LlmInducerPrompts.SystemRepair.Should().Contain("Language picker");
        LlmInducerPrompts.SystemRepair.Should().Contain("Filter");
        LlmInducerPrompts.SystemRepair.Should().Contain("faceted-search");
        LlmInducerPrompts.SystemRepair.Should().Contain("Pagination");
        LlmInducerPrompts.SystemRepair.Should().Contain("Cookie");
        LlmInducerPrompts.SystemRepair.Should().Contain("Sanity check");
        LlmInducerPrompts.SystemRepair.Should().Contain("NARROWEST");
    }

    [Fact]
    public void System_Prompt_Still_Encodes_Yaml_Schema_And_Block_Roles()
    {
        // Existing contract preserved: the YAML shape + role enumeration
        // are still in the prompt, and the example block is still present.
        LlmInducerPrompts.System.Should().Contain("BlockRole");
        LlmInducerPrompts.System.Should().Contain("```yaml");
        LlmInducerPrompts.System.Should().Contain("MainContent");
    }
}
