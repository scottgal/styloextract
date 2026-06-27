using Microsoft.Data.Sqlite;

namespace StyloExtract.Templates;

public static class SqliteSchema
{
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS templates (
          template_id            BLOB PRIMARY KEY,
          host_hash              BLOB NOT NULL,
          version_number         INTEGER NOT NULL DEFAULT 1,
          signature_minhash      BLOB NOT NULL,
          anchor_signature       BLOB NOT NULL,
          pq_gram_vector         BLOB NOT NULL,
          pq_gram_norm           REAL NOT NULL,
          extractor_blob         BLOB NOT NULL,
          observation_count      INTEGER NOT NULL DEFAULT 1,
          created_at             INTEGER NOT NULL,
          last_seen              INTEGER NOT NULL,
          last_refit_at          INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_templates_host ON templates(host_hash, last_seen);

        CREATE TABLE IF NOT EXISTS template_lsh_band_index (
          band_hash   BLOB NOT NULL,
          band_index  INTEGER NOT NULL,
          template_id BLOB NOT NULL,
          PRIMARY KEY (band_hash, band_index, template_id)
        );

        CREATE TABLE IF NOT EXISTS template_version_history (
          template_id          BLOB NOT NULL,
          version_number       INTEGER NOT NULL,
          signature_minhash    BLOB NOT NULL,
          pq_gram_vector       BLOB NOT NULL,
          extractor_blob       BLOB NOT NULL,
          retired_at           INTEGER NOT NULL,
          retirement_reason    TEXT,
          PRIMARY KEY (template_id, version_number)
        );

        CREATE TABLE IF NOT EXISTS template_observations (
          template_id          BLOB NOT NULL,
          observed_at          INTEGER NOT NULL,
          signature_minhash    BLOB NOT NULL,
          similarity_at_match  REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_obs_template ON template_observations(template_id, observed_at);

        -- Phase 1 Task 5: append-only rule-observation corpus. One row per
        -- BlockRule emitted into any persisted template, ever. Feeds the
        -- Phase 2 cross-host mining step (LSH-bucket-scoped query joins
        -- rules from different hosts that fingerprint into the same cluster).
        -- Distinct from template_observations above which records per-match
        -- similarity logs and is LRU-capped at 100 per template.
        CREATE TABLE IF NOT EXISTS template_rule_observations (
          observation_id       BLOB PRIMARY KEY NOT NULL,
          host_hash            BLOB NOT NULL,
          lsh_bucket           INTEGER NOT NULL,
          role                 INTEGER NOT NULL,
          claims_json          BLOB NOT NULL,
          target_signature     INTEGER NOT NULL,
          cardinality          INTEGER NOT NULL,
          confidence           REAL NOT NULL,
          induced_at           INTEGER NOT NULL,
          inducer_kind         INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_rule_obs_host_role ON template_rule_observations(host_hash, role, induced_at);
        CREATE INDEX IF NOT EXISTS ix_rule_obs_bucket_role ON template_rule_observations(lsh_bucket, role, induced_at);
        CREATE INDEX IF NOT EXISTS ix_rule_obs_induced_at ON template_rule_observations(induced_at);
        """;

    public static void EnsureCreated(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = CreateSql;
        cmd.ExecuteNonQuery();
    }
}
