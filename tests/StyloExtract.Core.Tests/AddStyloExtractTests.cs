using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;
using Xunit;

namespace StyloExtract.Core.Tests;

public class AddStyloExtractTests
{
    [Fact]
    public async Task AddStyloExtract_ResolvesILayoutExtractor()
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        var sp = services.BuildServiceProvider();

        var extractor = sp.GetRequiredService<ILayoutExtractor>();
        var result = await extractor.ExtractAsync("<html><body><main><article><p>hi</p></article></main></body></html>");

        result.Should().NotBeNull();
        result.Match.Status.Should().BeOneOf(MatchStatus.Novel, MatchStatus.FastPathHit, MatchStatus.SlowPathMatch);
    }
}
