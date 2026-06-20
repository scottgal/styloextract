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
        """;

    public static void EnsureCreated(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = CreateSql;
        cmd.ExecuteNonQuery();
    }
}
