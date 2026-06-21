using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;

namespace StyloExtract.Templates.Postgres;

/// <summary>
/// DI registration for the PostgreSQL-backed template index.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresTemplateIndex"/> as <see cref="ITemplateIndex"/> and
    /// <see cref="PostgresRefitOrchestrator"/> for use with the StyloExtract pipeline.
    ///
    /// Call this INSTEAD OF the SQLite registration in <c>AddStyloExtract()</c> (or after it,
    /// in which case it replaces the <see cref="ITemplateIndex"/> descriptor added by that method).
    ///
    /// The <see cref="PostgresTemplateIndex"/> singleton also remains resolvable directly so that
    /// integration code that needs the extended Postgres surface (e.g. migration tooling) can
    /// ask for it without down-casting from <see cref="ITemplateIndex"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// // Standalone Postgres wiring (no AddStyloExtract call required for the store layer):
    /// services.AddStyloExtractPostgres(o =>
    ///     o.ConnectionString = "Host=localhost;Database=styloextract;Username=se;Password=secret");
    /// </code>
    /// </example>
    public static IServiceCollection AddStyloExtractPostgres(
        this IServiceCollection services,
        Action<PostgresTemplateStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PostgresTemplateStoreOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException(
                "PostgresTemplateStoreOptions.ConnectionString must not be empty. " +
                "Set it in the configure delegate passed to AddStyloExtractPostgres.");

        services.AddSingleton(options);

        services.AddSingleton<PostgresTemplateIndex>(_ => new PostgresTemplateIndex(
            options.ConnectionString,
            options.AgingLambdaObs,
            options.AgingLambdaRecent,
            options.AgingTauDays));

        // Register as ITemplateIndex; replaces any prior registration if the caller has
        // already called AddStyloExtract (the SQLite variant).
        services.AddSingleton<ITemplateIndex>(sp => sp.GetRequiredService<PostgresTemplateIndex>());

        return services;
    }

    /// <summary>
    /// Registers <see cref="PostgresRefitOrchestrator"/> using the configured centroid options.
    /// Call after <see cref="AddStyloExtractPostgres"/> when drift-triggered refit is required.
    /// </summary>
    public static IServiceCollection AddStyloExtractPostgresRefit(
        this IServiceCollection services,
        double driftRefitThreshold = 0.35,
        int observationsBeforeStable = 5,
        int versionHistoryDepth = 3)
    {
        services.AddSingleton<PostgresRefitOrchestrator>(sp => new PostgresRefitOrchestrator(
            sp.GetRequiredService<PostgresTemplateIndex>(),
            sp.GetRequiredService<IExtractorInducer>(),
            driftRefitThreshold,
            observationsBeforeStable,
            versionHistoryDepth));

        return services;
    }
}
