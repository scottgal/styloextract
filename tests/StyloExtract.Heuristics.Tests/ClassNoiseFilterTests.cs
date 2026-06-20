using FluentAssertions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class ClassNoiseFilterTests
{
    private static readonly ClassNoiseFilter Filter = ClassNoiseFilter.LoadFromEmbeddedResource();

    [Fact]
    public void Filter_RemovesExactNoiseTokens()
    {
        Filter.Filter(["btn", "primary", "dark-mode", "active"]).Should().BeEquivalentTo(["btn", "primary"]);
    }

    [Fact]
    public void Filter_RemovesPrefixedNoiseTokens()
    {
        Filter.Filter(["nav", "is-open", "js-toggle", "has-children"]).Should().BeEquivalentTo(["nav"]);
    }

    [Fact]
    public void Filter_RemovesHashedBemSuffixes()
    {
        Filter.Filter(["MainNav__abc123", "Logo", "Item__xyz9"]).Should().BeEquivalentTo(["MainNav", "Logo", "Item"]);
    }

    [Fact]
    public void Filter_PreservesStableTokens()
    {
        Filter.Filter(["header", "footer", "primary-nav"]).Should().BeEquivalentTo(["header", "footer", "primary-nav"]);
    }
}
