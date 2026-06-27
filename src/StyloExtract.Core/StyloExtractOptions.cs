using StyloExtract.Abstractions;

namespace StyloExtract.Core;

public sealed class StyloExtractOptions
{
    public ExtractionProfile DefaultProfile { get; set; } = ExtractionProfile.RagFull;
    public string StorePath { get; set; } = "styloextract-templates.db";
    public string? HostHashKey { get; set; }

    /// <summary>
    /// Global default for <see cref="ExtractionOptions.EvaluateEvolvedCandidates"/>.
    /// When true every extraction (where the caller didn't specify their own
    /// options) evaluates mined evolved-selector candidates against the doc and
    /// records win/loss into the candidate reputation columns. Cached
    /// extraction output is unchanged — this is observation-only telemetry
    /// until Task 11 wires active promotion.
    ///
    /// Default false. Per-call <see cref="ExtractionOptions.EvaluateEvolvedCandidates"/>
    /// overrides this when callers pass their own options instance.
    /// </summary>
    public bool EvaluateEvolvedCandidates { get; set; } = false;

    /// <summary>
    /// When true, registers <c>CorpusMiningCoordinator</c> as an
    /// <c>IHostedService</c>. The coordinator periodically walks
    /// <c>template_rule_observations</c>, computes stable subsequences per
    /// (LSH bucket, role) cell, and emits evolved selector candidates into
    /// the <c>evolved_selector_candidates</c> table. Combined with
    /// <see cref="EvaluateEvolvedCandidates"/> (Task 9), the candidates
    /// accumulate reputation passively. Task 11 promotes high-reputation
    /// candidates to active use.
    ///
    /// Default false. Set to true to opt the host into corpus mining.
    /// </summary>
    public bool EnableCorpusMining { get; set; } = false;

    /// <summary>
    /// Cadence at which the corpus mining coordinator runs. Default 1 hour.
    /// Values below 1 minute are clamped up — the emitter is
    /// O(observations) per pass and sub-minute cadence wastes CPU without
    /// giving the corpus time to grow.
    /// </summary>
    public TimeSpan CorpusMiningInterval { get; set; } = TimeSpan.FromHours(1);

    public FingerprintOptions Fingerprint { get; } = new();
    public MatchOptions Match { get; } = new();
    public CentroidOptions Centroid { get; } = new();

    public sealed class FingerprintOptions
    {
        public int MinHashSize { get; set; } = 128;
        public int LshBands { get; set; } = 16;
        public int LshRowsPerBand { get; set; } = 8;
        public int ShingleWidth { get; set; } = 3;
        public double AnchorWeight { get; set; } = 0.4;
    }

    public sealed class MatchOptions
    {
        public double FastPathJaccardThreshold { get; set; } = 0.85;
        public double SlowPathCosineThreshold { get; set; } = 0.75;
        public double AgingLambdaObs { get; set; } = 0.02;
        public double AgingLambdaRecent { get; set; } = 0.05;
        public double AgingTauDays { get; set; } = 30;
    }

    public sealed class CentroidOptions
    {
        public double DriftRefitThreshold { get; set; } = 0.35;
        public int ObservationsBeforeStable { get; set; } = 5;
        public int ObservationCloudSize { get; set; } = 100;
        public int VersionHistoryDepth { get; set; } = 3;
    }
}
