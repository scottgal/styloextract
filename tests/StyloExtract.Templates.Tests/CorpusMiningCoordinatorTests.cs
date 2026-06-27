using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Mostlylucid.Ephemeral;
using StyloExtract.Abstractions;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class CorpusMiningCoordinatorTests
{
    [Fact]
    public async Task Coordinator_EmitsCandidates_OnPeriodicPass()
    {
        var (index, _) = NewIndex();
        const int bucket = 200;

        // Seed three observations sharing the same leaf claim — enough
        // for ComputeStableSubsequenceAsync to find a stable anchor.
        for (var i = 0; i < 3; i++)
        {
            await index.AppendObservationAsync(
                NewObservation("seed.example", BlockRole.MainContent, bucket,
                    NewClaim("main", "post-content")), default);
        }

        var emitter = new EvolvedSelectorEmitter(index, new CorpusMiner(index));
        var coordinator = new CorpusMiningCoordinator(emitter, TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await coordinator.StartAsync(cts.Token);

        // First pass fires after one interval; wait for it plus a small
        // margin to cover scheduler jitter.
        var waitUntil = DateTimeOffset.UtcNow.AddSeconds(2);
        IReadOnlyList<EvolvedSelectorCandidate> candidates = Array.Empty<EvolvedSelectorCandidate>();
        while (DateTimeOffset.UtcNow < waitUntil)
        {
            candidates = await index.GetCandidatesByHostAsync("seed.example", BlockRole.MainContent);
            if (candidates.Count > 0) break;
            await Task.Delay(25);
        }

        candidates.Should().HaveCount(1,
            "the mining pass should upsert one candidate per contributing host");
        candidates[0].SourceCount.Should().Be(3);

        cts.Cancel();
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_RaisesCorpusMiningPassSignal()
    {
        var (index, _) = NewIndex();
        const int bucket = 201;
        for (var i = 0; i < 3; i++)
        {
            await index.AppendObservationAsync(
                NewObservation("signal.example", BlockRole.MainContent, bucket,
                    NewClaim("main", "post-content")), default);
        }

        var emitter = new EvolvedSelectorEmitter(index, new CorpusMiner(index));
        var signals = new TypedSignalSink<StyloExtractSignal>();
        var observed = new List<StyloExtractSignal>();
        signals.TypedSignalRaised += ev =>
        {
            if (ev.Signal == StyloExtractSignals.CorpusMiningPass) observed.Add(ev.Payload);
        };

        var coordinator = new CorpusMiningCoordinator(
            emitter, TimeSpan.FromMilliseconds(50), logger: null, signals: signals);

        using var cts = new CancellationTokenSource();
        await coordinator.StartAsync(cts.Token);

        var waitUntil = DateTimeOffset.UtcNow.AddSeconds(2);
        while (observed.Count == 0 && DateTimeOffset.UtcNow < waitUntil)
        {
            await Task.Delay(25);
        }

        observed.Should().NotBeEmpty("at least one mining pass should have fired");
        observed[0].ClustersTouched.Should().BeGreaterThanOrEqualTo(1);
        observed[0].CandidatesEmitted.Should().BeGreaterThanOrEqualTo(1);
        observed[0].ElapsedMs.Should().NotBeNull();

        cts.Cancel();
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Coordinator_StopsCleanly_OnCancellation()
    {
        var (index, _) = NewIndex();
        var emitter = new EvolvedSelectorEmitter(index, new CorpusMiner(index));
        // Interval long enough that the service is parked in Task.Delay
        // when we cancel — exercises the OCE handler around the delay.
        var coordinator = new CorpusMiningCoordinator(emitter, TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        await coordinator.StartAsync(cts.Token);
        await Task.Delay(50);

        cts.Cancel();

        // StopAsync must return promptly once the token fires, not after
        // the full 30s delay.
        var stopTask = coordinator.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(stopTask, "the coordinator must honour cancellation within the interval");
        await stopTask;
    }

    [Fact]
    public async Task Coordinator_KeepsRunning_AfterPerPassException()
    {
        var (real, _) = NewIndex();
        const int bucket = 202;
        for (var i = 0; i < 3; i++)
        {
            await real.AppendObservationAsync(
                NewObservation("resilient.example", BlockRole.MainContent, bucket,
                    NewClaim("main", "post-content")), default);
        }

        // Wrap the real index so the first EnumerateObservationsAsync call
        // throws, then subsequent calls delegate normally. The emitter
        // surfaces the exception; the coordinator's catch block must absorb
        // it and the next interval must still produce candidates.
        var wrapped = new ThrowOnceIndex(real);
        var emitter = new EvolvedSelectorEmitter(wrapped, new CorpusMiner(wrapped));

        var coordinator = new CorpusMiningCoordinator(
            emitter, TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await coordinator.StartAsync(cts.Token);

        var waitUntil = DateTimeOffset.UtcNow.AddSeconds(3);
        IReadOnlyList<EvolvedSelectorCandidate> candidates = Array.Empty<EvolvedSelectorCandidate>();
        while (DateTimeOffset.UtcNow < waitUntil)
        {
            candidates = await real.GetCandidatesByHostAsync("resilient.example", BlockRole.MainContent);
            if (candidates.Count > 0) break;
            await Task.Delay(25);
        }

        wrapped.EnumerateCallCount.Should().BeGreaterThanOrEqualTo(2,
            "the loop must keep ticking after a per-pass exception");
        candidates.Should().NotBeEmpty(
            "a later pass after the throwing one should still produce candidates");

        cts.Cancel();
        await coordinator.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Test-only <see cref="ITemplateIndex"/> wrapper. Throws on the first
    /// call to <see cref="EnumerateObservationsAsync"/> and delegates to
    /// the inner index on every subsequent call. Every other method is a
    /// straight pass-through. Used to prove the coordinator catches
    /// per-pass exceptions and keeps ticking.
    /// </summary>
    private sealed class ThrowOnceIndex : ITemplateIndex
    {
        private readonly ITemplateIndex _inner;
        private int _enumerateCalls;
        public int EnumerateCallCount => _enumerateCalls;

        public ThrowOnceIndex(ITemplateIndex inner) => _inner = inner;

        public async IAsyncEnumerable<TemplateObservation> EnumerateObservationsAsync(
            int? lshBucket = null,
            BlockRole? role = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _enumerateCalls);
            if (n == 1) throw new InvalidOperationException("simulated pass failure");
            await foreach (var o in _inner
                .EnumerateObservationsAsync(lshBucket, role, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return o;
            }
        }

        public Task<Guid?> ProbeFastPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
            => _inner.ProbeFastPathAsync(hostHash, fingerprint, threshold, cancellationToken);
        public Task<(Guid TemplateId, double Cosine)?> ProbeSlowPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken)
            => _inner.ProbeSlowPathAsync(hostHash, fingerprint, threshold, cancellationToken);
        public Task<LearnedExtractor?> GetExtractorAsync(Guid templateId, CancellationToken cancellationToken)
            => _inner.GetExtractorAsync(templateId, cancellationToken);
        public Task<int> GetObservationCountAsync(Guid templateId, CancellationToken cancellationToken)
            => _inner.GetObservationCountAsync(templateId, cancellationToken);
        public Task<int> GetTemplateVersionAsync(Guid templateId, CancellationToken cancellationToken)
            => _inner.GetTemplateVersionAsync(templateId, cancellationToken);
        public Task<Guid> RegisterAsync(byte[] hostHash, StructuralFingerprint fingerprint, LearnedExtractor extractor, CancellationToken cancellationToken)
            => _inner.RegisterAsync(hostHash, fingerprint, extractor, cancellationToken);
        public Task RecordObservationAsync(Guid templateId, StructuralFingerprint fingerprint, double similarity, CancellationToken cancellationToken)
            => _inner.RecordObservationAsync(templateId, fingerprint, similarity, cancellationToken);
        public ValueTask AppendObservationAsync(TemplateObservation observation, CancellationToken cancellationToken = default)
            => _inner.AppendObservationAsync(observation, cancellationToken);
        public ValueTask<IReadOnlyList<TemplateObservation>> GetObservationsByHostAsync(string host, BlockRole? role = null, int limit = 100, CancellationToken cancellationToken = default)
            => _inner.GetObservationsByHostAsync(host, role, limit, cancellationToken);
        public ValueTask<IReadOnlyList<TemplateObservation>> GetObservationsByBucketAsync(int lshBucket, BlockRole? role = null, int limit = 1000, CancellationToken cancellationToken = default)
            => _inner.GetObservationsByBucketAsync(lshBucket, role, limit, cancellationToken);
        public ValueTask UpsertCandidateAsync(EvolvedSelectorCandidate candidate, CancellationToken cancellationToken = default)
            => _inner.UpsertCandidateAsync(candidate, cancellationToken);
        public ValueTask<IReadOnlyList<EvolvedSelectorCandidate>> GetCandidatesByHostAsync(string host, BlockRole? role = null, int limit = 100, CancellationToken cancellationToken = default)
            => _inner.GetCandidatesByHostAsync(host, role, limit, cancellationToken);
        public ValueTask RecordCandidateOutcomeAsync(Guid candidateId, bool won, DateTimeOffset at, CancellationToken cancellationToken = default)
            => _inner.RecordCandidateOutcomeAsync(candidateId, won, at, cancellationToken);
    }

    internal static (SqliteTemplateIndex Index, SqliteConnection KeepAlive) NewIndex()
    {
        var cs = $"Data Source=file:coord-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        var keepAlive = new SqliteConnection(cs);
        keepAlive.Open();
        SqliteSchema.EnsureCreated(keepAlive);
        return (new SqliteTemplateIndex(cs), keepAlive);
    }

    internal static IdentityClaim NewClaim(string tag, string? id = null)
    {
        var tagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag));
        ulong? idHash = id is null ? null : XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(id));
        return new IdentityClaim
        {
            Tag = tag,
            TagHash = tagHash,
            Id = id,
            IdHash = idHash,
            Classes = Array.Empty<string>(),
            ClassHashes = Array.Empty<ulong>(),
        };
    }

    internal static TemplateObservation NewObservation(
        string host, BlockRole role, int bucket, IdentityClaim leaf)
        => new()
        {
            ObservationId = Guid.NewGuid(),
            Host = host,
            LshBucket = bucket,
            Role = role,
            Claims = new[] { leaf },
            TargetSignature = leaf.TagHash ^ (leaf.IdHash ?? 0UL),
            Cardinality = 1,
            Confidence = 0.9,
            InducedAt = DateTimeOffset.UtcNow,
            InducerKind = InducerKind.Heuristic,
        };
}
