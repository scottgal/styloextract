using Npgsql;

namespace StyloExtract.Templates.Postgres;

/// <summary>
/// DDL for the PostgreSQL template store.
/// Schema mirrors the SQLite provider 1:1:
///   - BLOB columns become bytea
///   - INTEGER timestamps (Unix ms) become bigint
///   - REAL columns become double precision
///   - GUIDs stored as bytea (16 bytes) for cross-provider parity
///
/// Run on first connection via EnsureCreatedAsync. All statements are idempotent.
/// </summary>
public static class PostgresSchema
{
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS templates (
          template_id            bytea          PRIMARY KEY,
          host_hash              bytea          NOT NULL,
          version_number         integer        NOT NULL DEFAULT 1,
          signature_minhash      bytea          NOT NULL,
          anchor_signature       bytea          NOT NULL,
          pq_gram_vector         bytea          NOT NULL,
          pq_gram_norm           double precision NOT NULL,
          extractor_blob         bytea          NOT NULL,
          observation_count      integer        NOT NULL DEFAULT 1,
          created_at             bigint         NOT NULL,
          last_seen              bigint         NOT NULL,
          last_refit_at          bigint
        );

        CREATE INDEX IF NOT EXISTS ix_templates_host ON templates(host_hash, last_seen);

        CREATE TABLE IF NOT EXISTS template_lsh_band_index (
          band_hash   bytea   NOT NULL,
          band_index  integer NOT NULL,
          template_id bytea   NOT NULL,
          PRIMARY KEY (band_hash, band_index, template_id)
        );

        CREATE TABLE IF NOT EXISTS template_version_history (
          template_id          bytea   NOT NULL,
          version_number       integer NOT NULL,
          signature_minhash    bytea,
          pq_gram_vector       bytea,
          extractor_blob       bytea,
          retired_at           bigint  NOT NULL,
          retirement_reason    text,
          PRIMARY KEY (template_id, version_number)
        );

        CREATE TABLE IF NOT EXISTS template_observations (
          template_id          bytea            NOT NULL,
          observed_at          bigint           NOT NULL,
          signature_minhash    bytea            NOT NULL,
          similarity_at_match  double precision NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_obs_template ON template_observations(template_id, observed_at);
        """;

    public static async Task EnsureCreatedAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = CreateSql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
