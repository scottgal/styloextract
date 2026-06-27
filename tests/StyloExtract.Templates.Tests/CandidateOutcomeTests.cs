using FluentAssertions;
using StyloExtract.Abstractions;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class CandidateOutcomeTests
{
    [Fact]
    public async Task RecordOutcome_Win_IncrementsReputationAndStampsWonAt()
    {
        var (index, _) = EvolvedSelectorEmitterTests.NewIndex();
        const int bucket = 60;

        for (var i = 0; i < 3; i++)
        {
            await index.AppendObservationAsync(
                EvolvedSelectorEmitterTests.NewObservation(
                    "a.com", BlockRole.MainContent, bucket,
                    EvolvedSelectorEmitterTests.NewClaim("main", "post")), default);
        }

        var emitter = new EvolvedSelectorEmitter(index, new CorpusMiner(index));
        await emitter.EmitForClusterAsync(bucket, BlockRole.MainContent);

        var candidate = (await index.GetCandidatesByHostAsync("a.com", BlockRole.MainContent))[0];
        var winAt = DateTimeOffset.UtcNow;

        await index.RecordCandidateOutcomeAsync(candidate.CandidateId, won: true, winAt);

        var after = (await index.GetCandidatesByHostAsync("a.com", BlockRole.MainContent))[0];
        after.ReputationScore.Should().Be(1);
        after.LastWonAt.Should().NotBeNull();
        after.LastWonAt!.Value.ToUnixTimeMilliseconds()
            .Should().Be(winAt.ToUnixTimeMilliseconds());
        after.LastLostAt.Should().BeNull();
    }

    [Fact]
    public async Task RecordOutcome_Loss_DecrementsReputationAndStampsLostAt()
    {
        var (index, _) = EvolvedSelectorEmitterTests.NewIndex();
        const int bucket = 61;

        for (var i = 0; i < 3; i++)
        {
            await index.AppendObservationAsync(
                EvolvedSelectorEmitterTests.NewObservation(
                    "a.com", BlockRole.MainContent, bucket,
                    EvolvedSelectorEmitterTests.NewClaim("main", "post")), default);
        }

        var emitter = new EvolvedSelectorEmitter(index, new CorpusMiner(index));
        await emitter.EmitForClusterAsync(bucket, BlockRole.MainContent);

        var candidate = (await index.GetCandidatesByHostAsync("a.com", BlockRole.MainContent))[0];
        var lossAt = DateTimeOffset.UtcNow;

        await index.RecordCandidateOutcomeAsync(candidate.CandidateId, won: false, lossAt);

        var after = (await index.GetCandidatesByHostAsync("a.com", BlockRole.MainContent))[0];
        after.ReputationScore.Should().Be(-1);
        after.LastLostAt.Should().NotBeNull();
        after.LastLostAt!.Value.ToUnixTimeMilliseconds()
            .Should().Be(lossAt.ToUnixTimeMilliseconds());
        after.LastWonAt.Should().BeNull();
    }
}
