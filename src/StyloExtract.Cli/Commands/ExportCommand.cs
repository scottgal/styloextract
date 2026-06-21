using System.CommandLine;
using Microsoft.Data.Sqlite;
using StyloExtract.Templates;

namespace StyloExtract.Cli.Commands;

public static class ExportCommand
{
    public static Command Build()
    {
        var storeOpt = new Option<string>("--store") { Required = true, Description = "Path to the SQLite store." };
        var hostOpt = new Option<string>("--host") { Required = true, Description = "Host display name (e.g. example.com)." };
        var outOpt = new Option<string>("--out") { Required = true, Description = "Output JSON file path." };
        var keyOpt = new Option<string?>("--host-hash-key") { DefaultValueFactory = _ => null, Description = "Base64 HMAC key for host hashing. Omit to use a random key (cross-process roundtrip will not match)." };

        var cmd = new Command("export", "Export a host's templates as JSON.");
        cmd.Add(storeOpt);
        cmd.Add(hostOpt);
        cmd.Add(outOpt);
        cmd.Add(keyOpt);

        cmd.SetAction(async (ParseResult pr) =>
        {
            var store = pr.GetValue(storeOpt)!;
            var host = pr.GetValue(hostOpt)!;
            var outFile = pr.GetValue(outOpt)!;
            var key = pr.GetValue(keyOpt);

            using var conn = new SqliteConnection($"Data Source={store}");
            conn.Open();
            SqliteSchema.EnsureCreated(conn);

            var hasher = HostHasher.FromConfiguredKeyOrRandom(key);
            var hostHash = hasher.Hash(host);

            await using var fs = File.Create(outFile);
            await TemplateExporter.ExportHostAsync(conn, hostHash, host, fs, default);
            await Console.Error.WriteLineAsync($"Exported templates for {host} -> {outFile}");
            return 0;
        });

        return cmd;
    }
}
