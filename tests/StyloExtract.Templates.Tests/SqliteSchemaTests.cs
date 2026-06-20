using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class SqliteSchemaTests
{
    [Fact]
    public void EnsureCreated_CreatesAllExpectedTables()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        SqliteSchema.EnsureCreated(conn);

        var tables = ListTables(conn);
        tables.Should().Contain(new[] { "templates", "template_lsh_band_index", "template_version_history", "template_observations" });
    }

    [Fact]
    public void EnsureCreated_IsIdempotent()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        SqliteSchema.EnsureCreated(conn);
        Action again = () => SqliteSchema.EnsureCreated(conn);

        again.Should().NotThrow();
    }

    private static List<string> ListTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }
}
