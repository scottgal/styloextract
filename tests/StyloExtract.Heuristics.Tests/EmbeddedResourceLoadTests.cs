using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Verifies that every embedded JSON resource in StyloExtract.Heuristics loads
/// successfully and that item counts and required entries match the JSON files.
/// A failing test here means a resource name was mistyped, the JSON is malformed,
/// or the EmbeddedResource include was accidentally removed from the .csproj.
///
/// Internal DTOs cannot be referenced from the test assembly, so this file reads
/// JSON directly via <see cref="JsonDocument"/> and verifies counts + spot-check
/// values. Correctness of loading into typed DTOs is verified by exercising the
/// public factory methods (<see cref="HeuristicBlockClassifier.LoadFromEmbeddedResources"/>,
/// <see cref="BlockSegmenter"/>, <see cref="ClassNoiseFilter.LoadFromEmbeddedResource"/>).
/// </summary>
public sealed class EmbeddedResourceLoadTests
{
    private static readonly Assembly HeuristicsAssembly = typeof(HeuristicBlockClassifier).Assembly;

    // Reads an embedded resource stream by file-name suffix and returns a parsed JsonDocument.
    private static JsonDocument ReadJsonDocument(string fileNameSuffix)
    {
        var resName = HeuristicsAssembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileNameSuffix, StringComparison.Ordinal));
        resName.Should().NotBeNull($"embedded resource ending with '{fileNameSuffix}' must exist in the assembly");
        using var stream = HeuristicsAssembly.GetManifestResourceStream(resName!);
        stream.Should().NotBeNull($"stream for '{resName}' must be readable");
        return JsonDocument.Parse(stream!);
    }

    // Returns the string items from a { "hints": [...] } or { "phrases": [...] } root array.
    private static List<string> ReadStringArray(string fileNameSuffix, string propertyName)
    {
        using var doc = ReadJsonDocument(fileNameSuffix);
        doc.RootElement.GetProperty(propertyName).ValueKind.Should().Be(JsonValueKind.Array);
        return doc.RootElement.GetProperty(propertyName)
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();
    }

    // ---------------------------------------------------------------------------
    // framework-content-class-hints.json
    // ---------------------------------------------------------------------------

    [Fact]
    public void FrameworkContentClassHints_resource_exists_and_is_valid_json()
    {
        var act = () => ReadJsonDocument("framework-content-class-hints.json");
        act.Should().NotThrow("framework-content-class-hints.json must be a valid embedded JSON resource");
    }

    [Fact]
    public void FrameworkContentClassHints_has_expected_count()
    {
        var hints = ReadStringArray("framework-content-class-hints.json", "hints");
        hints.Should().HaveCount(27,
            "27 hints are declared in framework-content-class-hints.json; update this assertion if hints are added/removed");
    }

    [Fact]
    public void FrameworkContentClassHints_contains_canonical_cms_entries()
    {
        var hints = ReadStringArray("framework-content-class-hints.json", "hints");
        hints.Should().Contain("entry-content", "WordPress canonical post wrapper");
        hints.Should().Contain("gh-content", "Ghost Casper theme wrapper");
        hints.Should().Contain("field--name-body", "Drupal article body class");
        hints.Should().Contain("magento-content-area", "Magento CMS wrapper");
        hints.Should().Contain("primary-content", "generic CMS content class");
        hints.Should().Contain("story-body", "news site template class");
        hints.Should().Contain("wp-block-post-content", "Gutenberg block theme wrapper");
        hints.Should().Contain("main-content", "generic main content class");
    }

    // ---------------------------------------------------------------------------
    // intra-block-contamination-hints.json
    // ---------------------------------------------------------------------------

    [Fact]
    public void IntraBlockContaminationHints_resource_exists_and_is_valid_json()
    {
        var act = () => ReadJsonDocument("intra-block-contamination-hints.json");
        act.Should().NotThrow();
    }

    [Fact]
    public void IntraBlockContaminationHints_has_more_than_20_entries()
    {
        var hints = ReadStringArray("intra-block-contamination-hints.json", "hints");
        hints.Count.Should().BeGreaterThan(20,
            "contamination hints include nav/toc/toolbar/breadcrumb plus e-commerce widget patterns");
    }

    [Fact]
    public void IntraBlockContaminationHints_contains_nav_and_toc_entries()
    {
        var hints = ReadStringArray("intra-block-contamination-hints.json", "hints");
        hints.Should().Contain("nav");
        hints.Should().Contain("toc");
        hints.Should().Contain("breadcrumb");
        hints.Should().Contain("toolbar");
    }

    // ---------------------------------------------------------------------------
    // repeated-item-tag-rules.json
    // ---------------------------------------------------------------------------

    [Fact]
    public void RepeatedItemTagRules_resource_exists_and_is_valid_json()
    {
        var act = () => ReadJsonDocument("repeated-item-tag-rules.json");
        act.Should().NotThrow();
    }

    [Fact]
    public void RepeatedItemTagRules_has_all_four_required_sections()
    {
        using var doc = ReadJsonDocument("repeated-item-tag-rules.json");
        var root = doc.RootElement;
        root.TryGetProperty("skipContainerTags", out _).Should().BeTrue();
        root.TryGetProperty("skipChildTags", out _).Should().BeTrue();
        root.TryGetProperty("skipAncestorTags", out _).Should().BeTrue();
        root.TryGetProperty("selfTypedTags", out _).Should().BeTrue();
    }

    [Fact]
    public void RepeatedItemTagRules_skipContainerTags_includes_table_and_semantic_chrome()
    {
        using var doc = ReadJsonDocument("repeated-item-tag-rules.json");
        var tags = doc.RootElement.GetProperty("skipContainerTags")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        tags.Should().Contain("table", "tables are handled by the Table renderer, not repeated-item detector");
        tags.Should().Contain("header", "header chrome must be skipped");
        tags.Should().Contain("footer", "footer chrome must be skipped");
    }

    // ---------------------------------------------------------------------------
    // block-segmenter-tags.json
    // ---------------------------------------------------------------------------

    [Fact]
    public void BlockSegmenterTags_resource_exists_and_is_valid_json()
    {
        var act = () => ReadJsonDocument("block-segmenter-tags.json");
        act.Should().NotThrow();
    }

    [Fact]
    public void BlockSegmenterTags_contains_primary_html5_semantic_tags()
    {
        var tags = ReadStringArray("block-segmenter-tags.json", "hints");
        tags.Should().Contain("main");
        tags.Should().Contain("article");
        tags.Should().Contain("section");
        tags.Should().Contain("header");
        tags.Should().Contain("footer");
        tags.Should().Contain("nav");
    }

    // ---------------------------------------------------------------------------
    // Public factory method smoke tests (prove typed loading works end-to-end)
    // ---------------------------------------------------------------------------

    [Fact]
    public void HeuristicBlockClassifier_LoadFromEmbeddedResources_does_not_throw()
    {
        var act = () => HeuristicBlockClassifier.LoadFromEmbeddedResources();
        act.Should().NotThrow(
            "LoadFromEmbeddedResources must succeed with all embedded resources present");
    }

    [Fact]
    public void BlockSegmenter_construction_does_not_throw()
    {
        // BlockSegmenter loads block-segmenter-tags.json in its static initializer.
        var act = () => new BlockSegmenter();
        act.Should().NotThrow("BlockSegmenter must load its embedded resource on construction");
    }

    [Fact]
    public void ClassNoiseFilter_LoadFromEmbeddedResource_does_not_throw()
    {
        var act = () => ClassNoiseFilter.LoadFromEmbeddedResource();
        act.Should().NotThrow(
            "ClassNoiseFilter must load class-noise-tokens.json on first call");
    }

    [Fact]
    public void ClassNoiseFilter_filters_known_noise_tokens()
    {
        var filter = ClassNoiseFilter.LoadFromEmbeddedResource();
        // "dark-mode" and "active" are exact-token noise entries defined in class-noise-tokens.json.
        // "js-toggle" matches the "js-" prefix rule.
        var result = filter.Filter(["dark-mode", "active", "js-toggle", "entry-content"]);
        result.Should().NotContain("dark-mode", "dark-mode is an exact noise token in class-noise-tokens.json");
        result.Should().NotContain("active", "active is an exact noise token in class-noise-tokens.json");
        result.Should().NotContain("js-toggle", "js- prefix tokens are filtered by the prefix rule");
        result.Should().Contain("entry-content", "entry-content is not a noise token");
    }

    [Fact]
    public void All_four_embedded_json_resources_are_present_in_assembly()
    {
        var allNames = HeuristicsAssembly.GetManifestResourceNames();
        allNames.Should().Contain(n => n.EndsWith("framework-content-class-hints.json", StringComparison.Ordinal),
            "framework-content-class-hints.json must be embedded");
        allNames.Should().Contain(n => n.EndsWith("intra-block-contamination-hints.json", StringComparison.Ordinal),
            "intra-block-contamination-hints.json must be embedded");
        allNames.Should().Contain(n => n.EndsWith("repeated-item-tag-rules.json", StringComparison.Ordinal),
            "repeated-item-tag-rules.json must be embedded");
        allNames.Should().Contain(n => n.EndsWith("block-segmenter-tags.json", StringComparison.Ordinal),
            "block-segmenter-tags.json must be embedded");
    }
}
