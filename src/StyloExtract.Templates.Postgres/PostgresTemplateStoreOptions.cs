namespace StyloExtract.Templates.Postgres;

/// <summary>
/// Options for the PostgreSQL-backed template index.
/// </summary>
public sealed class PostgresTemplateStoreOptions
{
    /// <summary>
    /// Npgsql connection string. Example:
    /// "Host=localhost;Port=5432;Database=styloextract;Username=se;Password=secret"
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// EWMA lambda that weights the observation-count bonus in the aging priority scorer.
    /// Matches the SQLite provider default (0.02).
    /// </summary>
    public double AgingLambdaObs { get; set; } = 0.02;

    /// <summary>
    /// EWMA lambda that weights the recency bonus in the aging priority scorer.
    /// Matches the SQLite provider default (0.05).
    /// </summary>
    public double AgingLambdaRecent { get; set; } = 0.05;

    /// <summary>
    /// Time constant (days) for the exponential recency decay.
    /// Matches the SQLite provider default (30 days).
    /// </summary>
    public double AgingTauDays { get; set; } = 30.0;
}
