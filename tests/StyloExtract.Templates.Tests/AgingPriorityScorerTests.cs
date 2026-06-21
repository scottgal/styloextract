using FluentAssertions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class AgingPriorityScorerTests
{
    private const double TieSimilarity = 0.85;

    [Theory]
    [InlineData(2, 0, 0.07)]      // brand-new
    [InlineData(50, 7, 0.12)]     // freshly active
    [InlineData(10000, 180, 0.19)] // old-but-heavy
    [InlineData(3, 180, 0.03)]    // old-and-light
    public void Score_MatchesSpecWorkedExamples(int obs, double ageDays, double expectedBonus)
    {
        var score = AgingPriorityScorer.Score(TieSimilarity, obs, ageDays);
        var bonus = score - TieSimilarity;
        bonus.Should().BeApproximately(expectedBonus, 0.015);
    }

    [Fact]
    public void Score_OldHeavyBeatsOldLight()
    {
        var heavy = AgingPriorityScorer.Score(0.85, 10_000, 180);
        var light = AgingPriorityScorer.Score(0.85, 3, 180);
        heavy.Should().BeGreaterThan(light);
    }
}
