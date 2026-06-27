using FluentAssertions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class DefaultClassStabilityFilterTests
{
    private static readonly DefaultClassStabilityFilter Filter = new();

    [Theory]
    [InlineData("article-body")]
    [InlineData("post-content")]
    [InlineData("primary-nav")]
    [InlineData("header__title")]
    [InlineData("main")]
    [InlineData("mw-content-text")]
    public void Accepts_ReadableTokens(string token)
    {
        Filter.IsStable(token).Should().BeTrue();
    }

    [Theory]
    [InlineData("css-1a2b3c4")]
    [InlineData("sc-abc123")]
    [InlineData("tx7k9q2")]
    [InlineData("_1ab2cd__3ef4gh")]
    [InlineData("abc12345")]
    public void Rejects_HashShapedTokens(string token)
    {
        Filter.IsStable(token).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Rejects_EmptyOrNullOrWhitespace(string? token)
    {
        // The interface contract accepts non-null strings. We test the implementation
        // tolerates the null/empty/whitespace edge cases by returning false.
        Filter.IsStable(token!).Should().BeFalse();
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("nav")]
    [InlineData("h1")]
    public void Accepts_ShortStrings(string token)
    {
        Filter.IsStable(token).Should().BeTrue();
    }

    // ---- Tailwind variant prefixes (responsive / theme / state) ----

    [Theory]
    [InlineData("sm:gap-2")]
    [InlineData("md:flex")]
    [InlineData("lg:hidden")]
    [InlineData("xl:block")]
    [InlineData("2xl:text-base")]
    [InlineData("dark:text-white")]
    [InlineData("light:bg-white")]
    [InlineData("hover:bg-blue-500")]
    [InlineData("focus:ring-2")]
    [InlineData("active:scale-95")]
    [InlineData("disabled:opacity-50")]
    [InlineData("group-hover:opacity-50")]
    [InlineData("peer-checked:bg-blue")]
    [InlineData("first:rounded-t")]
    [InlineData("last:border-b-0")]
    public void Rejects_TailwindVariantPrefixed(string token)
    {
        Filter.IsStable(token).Should().BeFalse();
    }

    // ---- Tailwind utility prefixes (spacing / sizing / colour) ----

    [Theory]
    [InlineData("p-4")]
    [InlineData("m-2")]
    [InlineData("gap-x-8")]
    [InlineData("text-xl")]
    [InlineData("bg-gray-100")]
    [InlineData("border-2")]
    [InlineData("rounded-md")]
    [InlineData("w-full")]
    [InlineData("h-screen")]
    [InlineData("grid-cols-3")]
    [InlineData("z-10")]
    [InlineData("opacity-75")]
    [InlineData("px-4")]
    [InlineData("py-2")]
    [InlineData("mt-3")]
    [InlineData("mb-1")]
    public void Rejects_TailwindUtilityPrefixed(string token)
    {
        Filter.IsStable(token).Should().BeFalse();
    }

    // ---- Atomic layout singletons ----

    [Theory]
    [InlineData("flex")]
    [InlineData("grid")]
    [InlineData("block")]
    [InlineData("inline")]
    [InlineData("hidden")]
    [InlineData("absolute")]
    [InlineData("relative")]
    [InlineData("sticky")]
    [InlineData("fixed")]
    [InlineData("inline-block")]
    [InlineData("inline-flex")]
    [InlineData("table")]
    [InlineData("contents")]
    [InlineData("flow-root")]
    public void Rejects_AtomicLayoutSingletons(string token)
    {
        Filter.IsStable(token).Should().BeFalse();
    }

    // ---- Heuristic boundary: hyphenated semantic tokens with no number
    // are accepted, even if the first word coincidentally matches a Tailwind
    // utility prefix (e.g. "p-content"). Documents the false-negative side
    // of the trade-off — these tokens can stay as anchors. ----

    [Theory]
    [InlineData("post-list")]
    [InlineData("nav-primary")]
    [InlineData("main-content")]
    [InlineData("article-header")]
    [InlineData("site-footer")]
    public void Accepts_HyphenatedWords(string token)
    {
        Filter.IsStable(token).Should().BeTrue();
    }

    // ---- Bootstrap atomic utilities ----

    [Theory]
    [InlineData("col-md-6")]
    [InlineData("d-flex")]
    [InlineData("d-none")]
    [InlineData("p-0")]
    [InlineData("mt-3")]
    [InlineData("mx-auto")]
    public void Rejects_BootstrapAtomicUtilities(string token)
    {
        Filter.IsStable(token).Should().BeFalse();
    }
}
