using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// Phase 2 Task 9: passive evaluation of evolved-selector candidates. The
/// invariant under test is "cached extraction output is unchanged by candidate
/// evaluation" — every test compares blocks before/after the flag toggle.
/// </summary>
public class LayoutExtractorEvolvedCandidatesTests
{
    private const string TestHost = "example.com";

    private sealed class Harness : IDisposable
    {
        public required ILayoutExtractor Extractor { get; init; }
        public required SqliteTemplateIndex Index { get; init; }
        public required SqliteConnection Connection { get; init; }
        public void Dispose() => Connection.Dispose();
    }

    private static Harness Build()
    {
        var cs = $"Data Source=file:testdb-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fingerprinter = new StructuralFingerprinter(
            new ShingleGenerator(noise),
            sketcher,
            new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher),
            new PqGramExtractor());
        var filter = new DefaultClassStabilityFilter();
        var extractor = new LayoutExtractor(
            new AngleSharpHtmlDomParser(),
            new DomCleaner(),
            fingerprinter,
            new BlockSegmenter(),
            HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(),
            index,
            new HostHasher(new byte[32]),
            new ExtractorInducer(filter),
            new ExtractorApplicator(filter),
            fastPathThreshold: 0.85,
            slowPathThreshold: 0.75,
            new RefitOrchestrator(index, new ExtractorInducer(filter), 0.35, 5, 3),
            new DefaultNoopVersionEventSink(),
            stabilityFilter: filter);
        return new Harness { Extractor = extractor, Index = index, Connection = conn };
    }

    private const string ArticleHtml =
        "<html><head><title>Doc</title></head><body>" +
        "<main id='content'><article><h1>Topic</h1><p>" +
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit. " +
        "Vestibulum nec urna in dui blandit pulvinar at quis tortor. " +
        "Fusce ut metus magna. Nulla facilisi. Curabitur sed mauris a " +
        "augue suscipit fermentum quis vitae ipsum dolor. " +
        "Praesent dapibus, neque id cursus faucibus, tortor neque " +
        "egestas augue, eu vulputate magna eros eu erat." +
        "</p></article></main></body></html>";

    private static IdentityClaim Tag(string tag) => new()
    {
        Tag = tag,
        TagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag)),
    };

    private static EvolvedSelectorCandidate MakeCandidate(
        BlockRole role, IReadOnlyList<IdentityClaim> claims, string host = TestHost) => new()
    {
        CandidateId = Guid.NewGuid(),
        Host = host,
        LshBucket = 0,
        Role = role,
        Claims = claims,
        TargetSignature = claims[^1].TagHash ^ (claims[^1].IdHash ?? 0UL),
        SourceCount = 3,
        Confidence = 0.8,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task DefaultFlagOff_NoOutcomeRowsTouched_EvenWhenCandidatesExist()
    {
        using var h = Build();
        var sourceUri = new Uri($"https://{TestHost}/a");

        // Seed a candidate that WOULD match the doc — proves the flag, not the
        // matcher, is what suppresses the outcome write.
        var seed = MakeCandidate(BlockRole.MainContent, new[] { Tag("article") });
        await h.Index.UpsertCandidateAsync(seed);

        await h.Extractor.ExtractAsync(ArticleHtml, sourceUri,
            new ExtractionOptions { LearnNewTemplates = true });

        var after = await h.Index.GetCandidatesByHostAsync(TestHost);
        var seedAfter = after.Single(c => c.CandidateId == seed.CandidateId);
        seedAfter.ReputationScore.Should().Be(0);
        seedAfter.LastWonAt.Should().BeNull();
        seedAfter.LastLostAt.Should().BeNull();
    }

    [Fact]
    public async Task FlagOn_NoCandidates_ExtractionSucceedsWithoutError()
    {
        using var h = Build();
        var sourceUri = new Uri($"https://{TestHost}/a");

        var result = await h.Extractor.ExtractAsync(ArticleHtml, sourceUri,
            new ExtractionOptions { LearnNewTemplates = true, EvaluateEvolvedCandidates = true });

        result.Markdown.Should().NotBeNullOrWhiteSpace();
        result.Blocks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FlagOn_BothCandidatesMatch_BothIncrementReputation()
    {
        using var h = Build();
        var sourceUri = new Uri($"https://{TestHost}/a");

        // Two candidates that match the article doc: <article> leaf and <p> leaf.
        var c1 = MakeCandidate(BlockRole.MainContent, new[] { Tag("article") });
        var c2 = MakeCandidate(BlockRole.MainContent, new[] { Tag("p") });
        await h.Index.UpsertCandidateAsync(c1);
        await h.Index.UpsertCandidateAsync(c2);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await h.Extractor.ExtractAsync(ArticleHtml, sourceUri,
            new ExtractionOptions { LearnNewTemplates = true, EvaluateEvolvedCandidates = true });

        var after = await h.Index.GetCandidatesByHostAsync(TestHost);
        var c1After = after.Single(c => c.CandidateId == c1.CandidateId);
        var c2After = after.Single(c => c.CandidateId == c2.CandidateId);
        c1After.ReputationScore.Should().Be(1);
        c2After.ReputationScore.Should().Be(1);
        c1After.LastWonAt.Should().NotBeNull().And.BeAfter(before);
        c2After.LastWonAt.Should().NotBeNull().And.BeAfter(before);
        c1After.LastLostAt.Should().BeNull();
        c2After.LastLostAt.Should().BeNull();
    }

    [Fact]
    public async Task FlagOn_MixedOutcomes_WinnerIncrementsLoserDecrements()
    {
        using var h = Build();
        var sourceUri = new Uri($"https://{TestHost}/a");

        // Winner: <article> exists in the doc.
        var winner = MakeCandidate(BlockRole.MainContent, new[] { Tag("article") });
        // Loser: <video> does not exist in the doc.
        var loser = MakeCandidate(BlockRole.MainContent, new[] { Tag("video") });
        await h.Index.UpsertCandidateAsync(winner);
        await h.Index.UpsertCandidateAsync(loser);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await h.Extractor.ExtractAsync(ArticleHtml, sourceUri,
            new ExtractionOptions { LearnNewTemplates = true, EvaluateEvolvedCandidates = true });

        var after = await h.Index.GetCandidatesByHostAsync(TestHost);
        var winnerAfter = after.Single(c => c.CandidateId == winner.CandidateId);
        var loserAfter = after.Single(c => c.CandidateId == loser.CandidateId);

        winnerAfter.ReputationScore.Should().Be(1);
        winnerAfter.LastWonAt.Should().NotBeNull().And.BeAfter(before);
        winnerAfter.LastLostAt.Should().BeNull();

        loserAfter.ReputationScore.Should().Be(-1);
        loserAfter.LastLostAt.Should().NotBeNull().And.BeAfter(before);
        loserAfter.LastWonAt.Should().BeNull();
    }

    [Fact]
    public async Task FlagToggle_DoesNotAlterCachedExtractionOutput()
    {
        using var h = Build();
        var sourceUri = new Uri($"https://{TestHost}/a");

        // Prime a template so subsequent extractions hit the apply path.
        await h.Extractor.ExtractAsync(ArticleHtml, sourceUri,
            new ExtractionOptions { LearnNewTemplates = true });

        // Seed candidates so the evaluation path actually runs work.
        await h.Index.UpsertCandidateAsync(MakeCandidate(BlockRole.MainContent, new[] { Tag("article") }));
        await h.Index.UpsertCandidateAsync(MakeCandidate(BlockRole.MainContent, new[] { Tag("video") }));

        var off = await h.Extractor.ExtractAsync(ArticleHtml, sourceUri,
            new ExtractionOptions { LearnNewTemplates = true, EvaluateEvolvedCandidates = false });
        var on = await h.Extractor.ExtractAsync(ArticleHtml, sourceUri,
            new ExtractionOptions { LearnNewTemplates = true, EvaluateEvolvedCandidates = true });

        on.Blocks.Should().HaveCount(off.Blocks.Count);
        for (var i = 0; i < off.Blocks.Count; i++)
        {
            on.Blocks[i].Role.Should().Be(off.Blocks[i].Role);
            on.Blocks[i].Text.Should().Be(off.Blocks[i].Text);
            on.Blocks[i].CssSelector.Should().Be(off.Blocks[i].CssSelector);
        }
        on.Markdown.Should().Be(off.Markdown);
    }

    [Fact]
    public async Task BuilderDefault_OverriddenByPerCallOption()
    {
        // When AddStyloExtract registers the LayoutExtractor with the global
        // EvaluateEvolvedCandidates default ON, a per-call option that is also
        // ON still works (the per-call code path is what's been wired through
        // ExtractionOptions). Equally, the global ON default fires even when
        // the caller passes default-constructed ExtractionOptions.
        var services = new ServiceCollection();
        services.AddStyloExtract(o =>
        {
            o.StorePath = $"file:testdb-{Guid.NewGuid():N}?mode=memory&cache=shared";
            o.EvaluateEvolvedCandidates = true;
        });
        var sp = services.BuildServiceProvider();
        var extractor = sp.GetRequiredService<ILayoutExtractor>();
        var index = sp.GetRequiredService<ITemplateIndex>();

        var winner = MakeCandidate(BlockRole.MainContent, new[] { Tag("article") });
        await index.UpsertCandidateAsync(winner);

        // Default ExtractionOptions (flag false at the request layer) — the
        // global builder default carries the toggle through.
        var result = await extractor.ExtractAsync(ArticleHtml, new Uri($"https://{TestHost}/a"),
            new ExtractionOptions { LearnNewTemplates = true });
        result.Markdown.Should().NotBeNullOrWhiteSpace();

        var after = await index.GetCandidatesByHostAsync(TestHost);
        after.Single(c => c.CandidateId == winner.CandidateId).ReputationScore.Should().Be(1);
    }
}
